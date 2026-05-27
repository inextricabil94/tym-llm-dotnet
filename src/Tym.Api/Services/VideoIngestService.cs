using System.Diagnostics;
using System.Globalization;
using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tym.Api.Contracts;
using Tym.Api.Data;
using Tym.Api.Domain;

namespace Tym.Api.Services;

public sealed class TymOptions
{
    public string MediaRoot { get; set; } = "App_Data/tym-media";
    public long MaxVideoBytes { get; set; } = 250L * 1024L * 1024L;
    public int MaxFramesPerVideo { get; set; } = 48;
    public int DefaultSecondsPerFrame { get; set; } = 5;
    public string[] AllowedVideoExtensions { get; set; } = [".mp4", ".mov", ".m4v", ".webm", ".mkv"];
    public string[] AllowedVideoMimeTypes { get; set; } =
    [
        "video/mp4",
        "video/quicktime",
        "video/webm",
        "video/x-matroska",
        "application/octet-stream"
    ];
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public int ExtractedFrameWidth { get; set; } = 640;
    public bool UseSceneDetectionByDefault { get; set; }
    public double SceneChangeThreshold { get; set; } = 0.35;
}

public interface IVideoIngestService
{
    Task<VideoIngestResponse> IngestAsync(IFormFile file, VideoIngestOptions options, CancellationToken cancellationToken);
}

public sealed class VideoIngestService : IVideoIngestService
{
    private readonly TymDbContext _db;
    private readonly IVideoFrameExtractor _frameExtractor;
    private readonly IFrameAnalyzer _frameAnalyzer;
    private readonly IYardService _yards;
    private readonly IOptions<TymOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<VideoIngestService> _logger;

    public VideoIngestService(
        TymDbContext db,
        IVideoFrameExtractor frameExtractor,
        IFrameAnalyzer frameAnalyzer,
        IYardService yards,
        IOptions<TymOptions> options,
        IHostEnvironment environment,
        ILogger<VideoIngestService> logger)
    {
        _db = db;
        _frameExtractor = frameExtractor;
        _frameAnalyzer = frameAnalyzer;
        _yards = yards;
        _options = options;
        _environment = environment;
        _logger = logger;
    }

    public async Task<VideoIngestResponse> IngestAsync(IFormFile file, VideoIngestOptions options, CancellationToken cancellationToken)
    {
        ValidateUpload(file);

        var runtime = _options.Value;
        var referenceTime = options.ReferenceTime ?? DateTimeOffset.UtcNow;
        var capturedAt = options.CapturedAt;
        var source = string.IsNullOrWhiteSpace(options.Source) ? "video_upload" : options.Source.Trim();
        var secondsPerFrame = Math.Clamp(options.SecondsPerFrame <= 0 ? runtime.DefaultSecondsPerFrame : options.SecondsPerFrame, 1, 600);
        var maxFrames = Math.Clamp(options.MaxFrames <= 0 ? runtime.MaxFramesPerVideo : options.MaxFrames, 1, runtime.MaxFramesPerVideo);

        var asset = new MediaAsset
        {
            OriginalFileName = Path.GetFileName(file.FileName),
            ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            SizeBytes = file.Length,
            Source = source,
            Status = "uploading",
            CapturedAt = capturedAt,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var mediaRoot = ResolveMediaRoot();
        var assetDirectory = Path.Combine(mediaRoot, "assets", asset.Id.ToString("N"));
        var framesDirectory = Path.Combine(assetDirectory, "frames");
        Directory.CreateDirectory(framesDirectory);

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        asset.StoredFileName = $"source{extension}";
        asset.StoredPath = Path.Combine(assetDirectory, asset.StoredFileName);

        await using (var output = File.Create(asset.StoredPath))
        {
            await file.CopyToAsync(output, cancellationToken);
        }

        asset.Status = "uploaded";
        await _db.MediaAssets.AddAsync(asset, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        var notes = new List<string>();

        try
        {
            asset.DurationSeconds = await _frameExtractor.ProbeDurationSecondsAsync(asset.StoredPath, cancellationToken);
            var frames = await _frameExtractor.ExtractFramesAsync(
                asset.StoredPath,
                framesDirectory,
                secondsPerFrame,
                maxFrames,
                options.UseSceneDetection,
                cancellationToken);

            asset.FramesExtracted = frames.Count;
            asset.Status = frames.Count == 0 ? "no_frames" : "frames_extracted";

            if (frames.Count == 0)
            {
                notes.Add("No frames were extracted. Check that FFmpeg can read the uploaded video.");
            }

            var events = new List<TimelineEvent>();
            foreach (var frame in frames)
            {
                var analysis = await _frameAnalyzer.AnalyzeAsync(frame.FilePath, frame.OffsetSeconds, options.UseVision, cancellationToken);
                var mediaTimestamp = TextUtil.FormatMediaTimestamp(frame.OffsetSeconds);
                var baseTime = capturedAt ?? referenceTime;
                var timeConfidence = capturedAt.HasValue ? 0.78 : 0.35;
                var description = string.IsNullOrWhiteSpace(analysis.Description)
                    ? $"Video frame at {mediaTimestamp}."
                    : $"At {mediaTimestamp}: {analysis.Description}";

                var related = analysis.VisibleEntities.Count > 0
                    ? analysis.VisibleEntities
                    : TextUtil.Keywords(description, 10).ToList();

                events.Add(new TimelineEvent
                {
                    Subject = string.IsNullOrWhiteSpace(analysis.Subject)
                        ? TextUtil.MakeSubject(description)
                        : analysis.Subject.Trim(),
                    EventType = string.IsNullOrWhiteSpace(analysis.EventType)
                        ? "visual_observation"
                        : analysis.EventType.Trim(),
                    Description = description,
                    TimestampStart = baseTime.AddSeconds(frame.OffsetSeconds),
                    TimestampEnd = baseTime.AddSeconds(frame.OffsetSeconds + secondsPerFrame),
                    TimeConfidence = timeConfidence,
                    Source = source,
                    StatusAfter = string.IsNullOrWhiteSpace(analysis.StatusAfter) ? null : analysis.StatusAfter,
                    RelatedEntitiesJson = JsonSerializer.Serialize(related),
                    RawText = analysis.RawText,
                    Modality = "video_frame",
                    MediaAssetId = asset.Id,
                    MediaStartSeconds = frame.OffsetSeconds,
                    MediaEndSeconds = frame.OffsetSeconds + secondsPerFrame,
                    SegmentIndex = frame.Index,
                    MediaUri = $"/media-assets/{asset.Id}/frames/{Path.GetFileName(frame.FilePath)}",
                    ThumbnailUri = $"/media-assets/{asset.Id}/frames/{Path.GetFileName(frame.FilePath)}",
                    Confidence = analysis.Confidence,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }

            if (events.Count == 0)
            {
                events.Add(BuildUploadOnlyEvent(asset, source, referenceTime, notes));
            }

            await _db.Events.AddRangeAsync(events, cancellationToken);
            asset.EventsCreated = events.Count;
            asset.Status = events.Count > 0 && frames.Count > 0 ? "analyzed" : asset.Status;
            await _db.SaveChangesAsync(cancellationToken);

            await _yards.RebuildAsync(referenceTime, cancellationToken);

            var eventIds = events.Select(e => e.Id).ToHashSet();
            var storedEvents = await _db.Events
                .Where(e => eventIds.Contains(e.Id))
                .OrderBy(e => e.MediaStartSeconds ?? 0)
                .ThenBy(e => e.TimestampStart ?? e.CreatedAt)
                .ToListAsync(cancellationToken);

            if (options.UseVision && !_frameAnalyzer.HasVisionProvider)
            {
                notes.Add("Vision was requested but no vision provider was configured. Set OPENAI_API_KEY to generate visual captions.");
            }

            if (options.UseSceneDetection)
            {
                notes.Add("Scene detection was requested, so FFmpeg selected frames from visual scene changes instead of only fixed intervals.");
            }

            notes.Add($"Extracted {asset.FramesExtracted} frame(s) at approximately one frame every {secondsPerFrame} second(s). Larger videos should use a higher secondsPerFrame value or an async background queue.");

            return new VideoIngestResponse(
                MediaAssetDto.FromEntity(asset),
                storedEvents.Count,
                storedEvents.Select(EventDto.FromEntity).ToList(),
                notes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video ingestion failed for media asset {AssetId}.", asset.Id);
            asset.Status = "failed";
            asset.LastError = ex.Message;
            await _db.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    private void ValidateUpload(IFormFile file)
    {
        if (file.Length <= 0)
        {
            throw new InvalidOperationException("The uploaded video file is empty.");
        }

        var runtime = _options.Value;
        if (file.Length > runtime.MaxVideoBytes)
        {
            throw new InvalidOperationException($"The video is too large. Max allowed size is {runtime.MaxVideoBytes} bytes.");
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!runtime.AllowedVideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported video extension '{extension}'. Allowed extensions: {string.Join(", ", runtime.AllowedVideoExtensions)}.");
        }

        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
        if (!runtime.AllowedVideoMimeTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported video content type '{contentType}'. Allowed content types: {string.Join(", ", runtime.AllowedVideoMimeTypes)}.");
        }

        if (!LooksLikeAllowedVideoContainer(file, extension))
        {
            throw new InvalidOperationException($"Uploaded file '{file.FileName}' does not match the expected video container signature for extension '{extension}'.");
        }
    }

    private static bool LooksLikeAllowedVideoContainer(IFormFile file, string extension)
    {
        Span<byte> header = stackalloc byte[12];
        using var stream = file.OpenReadStream();
        var read = stream.Read(header);
        if (read < 4)
        {
            return false;
        }

        if ((extension is ".mp4" or ".mov" or ".m4v") && read >= 8)
        {
            return Encoding.ASCII.GetString(header.Slice(4, 4)) == "ftyp";
        }

        if (extension is ".webm" or ".mkv")
        {
            return header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3;
        }

        return false;
    }

    private string ResolveMediaRoot()
    {
        var configured = _options.Value.MediaRoot;
        var root = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(_environment.ContentRootPath, configured);

        Directory.CreateDirectory(root);
        return root;
    }

    private static TimelineEvent BuildUploadOnlyEvent(MediaAsset asset, string source, DateTimeOffset referenceTime, List<string> notes)
    {
        notes.Add("The video was stored, but no frame-level visual events could be created.");
        return new TimelineEvent
        {
            Subject = Path.GetFileNameWithoutExtension(asset.OriginalFileName),
            EventType = "video_upload",
            Description = $"Video uploaded: {asset.OriginalFileName}. No frame-level observations were extracted.",
            TimestampStart = asset.CapturedAt ?? referenceTime,
            TimeConfidence = asset.CapturedAt.HasValue ? 0.70 : 0.20,
            Source = source,
            RelatedEntitiesJson = JsonSerializer.Serialize(TextUtil.Keywords(asset.OriginalFileName, 8)),
            RawText = asset.OriginalFileName,
            Modality = "video_segment",
            MediaAssetId = asset.Id,
            MediaStartSeconds = 0,
            Confidence = 0.20,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}

public interface IVideoFrameExtractor
{
    Task<double?> ProbeDurationSecondsAsync(string videoPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<ExtractedFrame>> ExtractFramesAsync(string videoPath, string outputDirectory, int secondsPerFrame, int maxFrames, bool useSceneDetection, CancellationToken cancellationToken);
}

public sealed class FfmpegVideoFrameExtractor : IVideoFrameExtractor
{
    private readonly IOptions<TymOptions> _options;
    private readonly ILogger<FfmpegVideoFrameExtractor> _logger;

    public FfmpegVideoFrameExtractor(IOptions<TymOptions> options, ILogger<FfmpegVideoFrameExtractor> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<double?> ProbeDurationSecondsAsync(string videoPath, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                _options.Value.FfprobePath,
                ["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", videoPath],
                cancellationToken);

            if (result.ExitCode == 0 && double.TryParse(result.StdOut.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                return seconds;
            }

            _logger.LogWarning("ffprobe could not read duration. Exit={ExitCode}, stderr={StdErr}", result.ExitCode, result.StdErr);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffprobe duration probing failed.");
            return null;
        }
    }

    public async Task<IReadOnlyList<ExtractedFrame>> ExtractFramesAsync(
        string videoPath,
        string outputDirectory,
        int secondsPerFrame,
        int maxFrames,
        bool useSceneDetection,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        foreach (var oldFrame in Directory.EnumerateFiles(outputDirectory, "frame_*.jpg"))
        {
            File.Delete(oldFrame);
        }

        var width = Math.Clamp(_options.Value.ExtractedFrameWidth, 160, 1920);
        var outputPattern = Path.Combine(outputDirectory, "frame_%05d.jpg");
        var result = useSceneDetection
            ? await TryExtractSceneFramesAsync(videoPath, outputPattern, width, secondsPerFrame, maxFrames, cancellationToken)
            : await ExtractIntervalFramesAsync(videoPath, outputPattern, width, secondsPerFrame, maxFrames, cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg frame extraction failed: {result.StdErr}");
        }

        return Directory.EnumerateFiles(outputDirectory, "frame_*.jpg")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select((path, i) => new ExtractedFrame(i, path, i * secondsPerFrame))
            .ToList();
    }

    private async Task<ProcessResult> TryExtractSceneFramesAsync(
        string videoPath,
        string outputPattern,
        int width,
        int secondsPerFrame,
        int maxFrames,
        CancellationToken cancellationToken)
    {
        var threshold = Math.Clamp(_options.Value.SceneChangeThreshold, 0.05, 0.95).ToString("0.###", CultureInfo.InvariantCulture);
        var result = await ProcessRunner.RunAsync(
            _options.Value.FfmpegPath,
            [
                "-hide_banner",
                "-loglevel", "error",
                "-y",
                "-i", videoPath,
                "-vf", $"select='gt(scene,{threshold})',scale={width}:-2",
                "-fps_mode", "vfr",
                "-frames:v", maxFrames.ToString(CultureInfo.InvariantCulture),
                "-q:v", "3",
                outputPattern
            ],
            cancellationToken);

        if (result.ExitCode == 0 && Directory.EnumerateFiles(Path.GetDirectoryName(outputPattern)!, "frame_*.jpg").Any())
        {
            return result;
        }

        _logger.LogWarning("Scene detection produced no frames or failed. Falling back to fixed-interval extraction. Exit={ExitCode}, stderr={StdErr}", result.ExitCode, result.StdErr);
        return await ExtractIntervalFramesAsync(videoPath, outputPattern, width, secondsPerFrame, maxFrames, cancellationToken);
    }

    private Task<ProcessResult> ExtractIntervalFramesAsync(
        string videoPath,
        string outputPattern,
        int width,
        int secondsPerFrame,
        int maxFrames,
        CancellationToken cancellationToken)
    {
        return ProcessRunner.RunAsync(
            _options.Value.FfmpegPath,
            [
                "-hide_banner",
                "-loglevel", "error",
                "-y",
                "-i", videoPath,
                "-vf", $"fps=1/{secondsPerFrame},scale={width}:-2",
                "-frames:v", maxFrames.ToString(CultureInfo.InvariantCulture),
                "-q:v", "3",
                outputPattern
            ],
            cancellationToken);
    }
}

public sealed record ExtractedFrame(int Index, string FilePath, double OffsetSeconds);

public interface IFrameAnalyzer
{
    bool HasVisionProvider { get; }
    Task<FrameAnalysis> AnalyzeAsync(string framePath, double offsetSeconds, bool useVision, CancellationToken cancellationToken);
}

public sealed class HybridFrameAnalyzer : IFrameAnalyzer
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HybridFrameAnalyzer> _logger;

    public HybridFrameAnalyzer(IConfiguration configuration, IHttpClientFactory httpClientFactory, ILogger<HybridFrameAnalyzer> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool HasVisionProvider => !string.IsNullOrWhiteSpace(ApiKey());

    public async Task<FrameAnalysis> AnalyzeAsync(string framePath, double offsetSeconds, bool useVision, CancellationToken cancellationToken)
    {
        if (useVision && HasVisionProvider)
        {
            try
            {
                var analyzer = new OpenAiVisionFrameAnalyzer(ApiKey()!, VisionModel(), _httpClientFactory.CreateClient("openai"));
                return await analyzer.AnalyzeAsync(framePath, offsetSeconds, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenAI frame analysis failed for {FramePath}; using fallback analysis.", framePath);
            }
        }

        return HeuristicFrameAnalyzer.Analyze(framePath, offsetSeconds);
    }

    private string? ApiKey() => _configuration["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

    private string VisionModel() =>
        _configuration["OpenAI:VisionModel"]
        ?? Environment.GetEnvironmentVariable("OPENAI_VISION_MODEL")
        ?? _configuration["OpenAI:Model"]
        ?? Environment.GetEnvironmentVariable("OPENAI_MODEL")
        ?? "gpt-4.1-mini";
}

internal sealed class OpenAiVisionFrameAnalyzer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _apiKey;
    private readonly string _model;
    private readonly HttpClient _http;

    public OpenAiVisionFrameAnalyzer(string apiKey, string model, HttpClient http)
    {
        _apiKey = apiKey;
        _model = model;
        _http = http;
    }

    public async Task<FrameAnalysis> AnalyzeAsync(string framePath, double offsetSeconds, CancellationToken cancellationToken)
    {
        var imageBytes = await File.ReadAllBytesAsync(framePath, cancellationToken);
        var dataUrl = $"data:image/jpeg;base64,{Convert.ToBase64String(imageBytes)}";
        var mediaTimestamp = TextUtil.FormatMediaTimestamp(offsetSeconds);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = JsonContent(new
        {
            model = _model,
            input = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "input_text",
                            text = $$"""
You are the visual event extractor for TYM, a Time Yards Model.
Analyze this single video frame from timestamp {{mediaTimestamp}}.
Return only JSON with this exact shape:
{
  "subject": "short subject for the visual event",
  "event_type": "visual_observation | movement | scene_change | screen_change | status_change | safety_event",
  "description": "one sentence describing what is visible and what seems to be happening",
  "status_after": null,
  "visible_entities": ["entity", "entity"],
  "confidence": 0.0
}
Do not identify private people by name. Do not infer sensitive traits. If the frame has text on a screen, summarize it briefly.
"""
                        },
                        new
                        {
                            type = "input_image",
                            image_url = dataUrl
                        }
                    }
                }
            },
            max_output_tokens = 450
        });

        using var response = await _http.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI vision request failed: {(int)response.StatusCode} {responseText}");
        }

        var outputText = ExtractOutputText(responseText);
        var json = ExtractJsonObject(outputText);
        var parsed = JsonSerializer.Deserialize<FrameAnalysisEnvelope>(json, JsonOptions)
            ?? throw new InvalidOperationException("The vision response did not contain a valid analysis object.");

        return new FrameAnalysis(
            parsed.Subject ?? TextUtil.MakeSubject(parsed.Description ?? "video frame"),
            parsed.EventType ?? "visual_observation",
            parsed.Description ?? "Video frame observed.",
            parsed.StatusAfter,
            parsed.VisibleEntities ?? [],
            Clamp01(parsed.Confidence),
            outputText);
    }

    private static StringContent JsonContent(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static string ExtractOutputText(string responseText)
    {
        using var doc = JsonDocument.Parse(responseText);
        var values = new List<string>();
        CollectTextValues(doc.RootElement, values);
        var combined = string.Join("\n", values.Where(v => !string.IsNullOrWhiteSpace(v)));
        return string.IsNullOrWhiteSpace(combined) ? responseText : combined;
    }

    private static void CollectTextValues(JsonElement element, List<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("text") && property.Value.ValueKind == JsonValueKind.String)
                    {
                        values.Add(property.Value.GetString() ?? string.Empty);
                    }
                    else
                    {
                        CollectTextValues(property.Value, values);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectTextValues(item, values);
                }
                break;
        }
    }

    private static string ExtractJsonObject(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
            {
                trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
            }
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return trimmed[start..(end + 1)];
        }

        throw new InvalidOperationException("The vision response did not contain a JSON object.");
    }

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value)) return 0.5;
        return Math.Max(0, Math.Min(1, value));
    }

    private sealed record FrameAnalysisEnvelope(
        [property: JsonPropertyName("subject")] string? Subject,
        [property: JsonPropertyName("event_type")] string? EventType,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("status_after")] string? StatusAfter,
        [property: JsonPropertyName("visible_entities")] List<string>? VisibleEntities,
        [property: JsonPropertyName("confidence")] double Confidence);
}

internal static class HeuristicFrameAnalyzer
{
    public static FrameAnalysis Analyze(string framePath, double offsetSeconds)
    {
        var fileName = Path.GetFileName(framePath);
        var mediaTimestamp = TextUtil.FormatMediaTimestamp(offsetSeconds);
        var description = $"Frame {fileName} extracted at {mediaTimestamp}; no visual model was configured, so this is a timestamp marker rather than a semantic visual caption.";

        return new FrameAnalysis(
            $"video frame {mediaTimestamp}",
            "visual_observation",
            description,
            null,
            ["video", "frame", mediaTimestamp],
            0.15,
            description);
    }
}

public sealed record FrameAnalysis(
    string Subject,
    string EventType,
    string Description,
    string? StatusAfter,
    IReadOnlyList<string> VisibleEntities,
    double Confidence,
    string RawText);

internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;

        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) stdOut.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stdErr.AppendLine(e.Data);
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start process '{fileName}'.");
            }
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start '{fileName}'. Install FFmpeg/FFprobe or set Tym:FfmpegPath and Tym:FfprobePath to full executable paths.",
                ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        return new ProcessResult(process.ExitCode, stdOut.ToString(), stdErr.ToString());
    }
}

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
