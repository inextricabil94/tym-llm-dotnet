using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tym.Api.Contracts;
using Tym.Api.Data;
using Tym.Api.Services;

var builder = WebApplication.CreateBuilder(args);

var tymRuntimeOptions = builder.Configuration.GetSection("Tym").Get<TymOptions>() ?? new TymOptions();
builder.Services.Configure<TymOptions>(builder.Configuration.GetSection("Tym"));
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = tymRuntimeOptions.MaxVideoBytes;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("text-ingest", limiter =>
    {
        limiter.PermitLimit = 30;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
    options.AddFixedWindowLimiter("video-ingest", limiter =>
    {
        limiter.PermitLimit = 4;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
    options.AddFixedWindowLimiter("query", limiter =>
    {
        limiter.PermitLimit = 60;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
});

builder.Services.AddHttpClient("openai");
builder.Services.AddCors(options =>
{
    options.AddPolicy("tym-ui", policy =>
    {
        policy
            .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddDbContext<TymDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("TymDb") ?? "Data Source=tym.db";
    options.UseSqlite(connectionString);
});

builder.Services.AddSingleton<ITokenSimilarity, TokenSimilarity>();
builder.Services.AddScoped<IEventExtractor, HybridEventExtractor>();
builder.Services.AddScoped<IYardService, YardService>();
builder.Services.AddScoped<IQueryService, QueryService>();
builder.Services.AddScoped<IVideoFrameExtractor, FfmpegVideoFrameExtractor>();
builder.Services.AddScoped<IFrameAnalyzer, HybridFrameAnalyzer>();
builder.Services.AddScoped<IVideoIngestService, VideoIngestService>();

var app = builder.Build();
app.UseCors("tym-ui");
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TymDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapGet("/", () => Results.Ok(new
{
    name = "TYM LLM .NET prototype",
    description = "Timeline-aware multimodal RAG MVP using event extraction, video-frame analysis, time-yard scoring, SQLite memory, and optional OpenAI answer generation.",
    endpoints = new[]
    {
        "POST /ingest",
        "POST /ingest/video",
        "POST /query",
        "GET /events",
        "GET /yard-links",
        "GET /media-assets",
        "GET /media-assets/{id}",
        "GET /media-assets/{id}/events",
        "DELETE /events"
    }
}));

app.MapGet("/health", () => Results.Ok(new { ok = true, service = "tym-api", utc = DateTimeOffset.UtcNow }));

app.MapPost("/ingest", async (
    IngestRequest request,
    IEventExtractor extractor,
    IYardService yards,
    TymDbContext db,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Text))
    {
        return Results.BadRequest(new { error = "Text is required." });
    }

    var referenceTime = request.ReferenceTime ?? DateTimeOffset.UtcNow;
    var events = await extractor.ExtractAsync(request, referenceTime, cancellationToken);

    if (events.Count == 0)
    {
        return Results.BadRequest(new { error = "No events could be extracted from the supplied text." });
    }

    await db.Events.AddRangeAsync(events, cancellationToken);
    await db.SaveChangesAsync(cancellationToken);

    await yards.RebuildAsync(referenceTime, cancellationToken);

    var ids = events.Select(e => e.Id).ToHashSet();
    var storedEvents = await db.Events
        .Where(e => ids.Contains(e.Id))
        .ToListAsync(cancellationToken);

    var orderedStoredEvents = storedEvents
        .OrderBy(e => e.TimestampStart ?? e.CreatedAt)
        .ThenBy(e => e.CreatedAt)
        .ToList();

    return Results.Created("/events", new IngestResponse(
        orderedStoredEvents.Count,
        orderedStoredEvents.Select(EventDto.FromEntity).ToList()));
}).RequireRateLimiting("text-ingest");

app.MapPost("/ingest/video", async (
    HttpRequest httpRequest,
    IVideoIngestService videoIngest,
    IOptions<TymOptions> runtimeOptions,
    CancellationToken cancellationToken) =>
{
    if (!httpRequest.HasFormContentType)
    {
        return Results.BadRequest(new { error = "Expected multipart/form-data with a video file field named 'file'." });
    }

    var form = await httpRequest.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
    if (file is null)
    {
        return Results.BadRequest(new { error = "Missing uploaded video file. Use a form field named 'file'." });
    }

    var options = new VideoIngestOptions(
        Source: ReadString(form, "source"),
        ReferenceTime: ReadDateTimeOffset(form, "referenceTime"),
        CapturedAt: ReadDateTimeOffset(form, "capturedAt"),
        SecondsPerFrame: ReadInt(form, "secondsPerFrame") ?? 5,
        MaxFrames: ReadInt(form, "maxFrames") ?? 36,
        UseVision: ReadBool(form, "useVision") ?? true,
        UseSceneDetection: ReadBool(form, "useSceneDetection") ?? runtimeOptions.Value.UseSceneDetectionByDefault);

    try
    {
        var result = await videoIngest.IngestAsync(file, options, cancellationToken);
        return Results.Created($"/media-assets/{result.Asset.Id}", result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireRateLimiting("video-ingest");

app.MapPost("/query", async (
    QueryRequest request,
    IQueryService query,
    CancellationToken cancellationToken) =>
{
    var response = await query.AnswerAsync(request, cancellationToken);
    return Results.Ok(response);
}).RequireRateLimiting("query");

app.MapGet("/events", async (TymDbContext db, CancellationToken cancellationToken) =>
{
    var events = await db.Events.ToListAsync(cancellationToken);
    var orderedEvents = events
        .OrderBy(e => e.TimestampStart ?? e.CreatedAt)
        .ThenBy(e => e.MediaStartSeconds ?? -1)
        .ThenBy(e => e.CreatedAt)
        .ToList();

    return Results.Ok(orderedEvents.Select(EventDto.FromEntity));
});

app.MapGet("/yard-links", async (TymDbContext db, CancellationToken cancellationToken) =>
{
    var links = await db.YardLinks
        .OrderBy(l => l.LinkType)
        .ThenBy(l => l.Distance)
        .ToListAsync(cancellationToken);

    return Results.Ok(links);
});

app.MapGet("/media-assets", async (TymDbContext db, CancellationToken cancellationToken) =>
{
    var assets = await db.MediaAssets.ToListAsync(cancellationToken);
    var orderedAssets = assets
        .OrderByDescending(a => a.CreatedAt)
        .ToList();

    return Results.Ok(orderedAssets.Select(MediaAssetDto.FromEntity));
});

app.MapGet("/media-assets/{assetId:guid}", async (Guid assetId, TymDbContext db, CancellationToken cancellationToken) =>
{
    var asset = await db.MediaAssets.FindAsync([assetId], cancellationToken);
    return asset is null ? Results.NotFound() : Results.Ok(MediaAssetDto.FromEntity(asset));
});

app.MapGet("/media-assets/{assetId:guid}/events", async (Guid assetId, TymDbContext db, CancellationToken cancellationToken) =>
{
    var exists = await db.MediaAssets.AnyAsync(a => a.Id == assetId, cancellationToken);
    if (!exists)
    {
        return Results.NotFound();
    }

    var events = await db.Events
        .Where(e => e.MediaAssetId == assetId)
        .ToListAsync(cancellationToken);

    var orderedEvents = events
        .OrderBy(e => e.MediaStartSeconds ?? 0)
        .ThenBy(e => e.TimestampStart ?? e.CreatedAt)
        .ToList();

    return Results.Ok(orderedEvents.Select(EventDto.FromEntity));
});

app.MapGet("/media-assets/{assetId:guid}/frames/{fileName}", async (
    Guid assetId,
    string fileName,
    TymDbContext db,
    IHostEnvironment environment,
    IOptions<TymOptions> options,
    CancellationToken cancellationToken) =>
{
    if (!Regex.IsMatch(fileName, @"^frame_\d{5}\.jpg$", RegexOptions.IgnoreCase))
    {
        return Results.BadRequest(new { error = "Invalid frame file name." });
    }

    var asset = await db.MediaAssets.FindAsync([assetId], cancellationToken);
    if (asset is null)
    {
        return Results.NotFound();
    }

    var mediaRoot = Path.IsPathRooted(options.Value.MediaRoot)
        ? options.Value.MediaRoot
        : Path.Combine(environment.ContentRootPath, options.Value.MediaRoot);

    var framesRoot = Path.GetFullPath(Path.Combine(mediaRoot, "assets", asset.Id.ToString("N"), "frames"));
    var framePath = Path.GetFullPath(Path.Combine(framesRoot, fileName));

    if (!framePath.StartsWith(framesRoot, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(framePath))
    {
        return Results.NotFound();
    }

    return Results.File(framePath, "image/jpeg");
});

app.MapDelete("/events", async (TymDbContext db, CancellationToken cancellationToken) =>
{
    db.YardLinks.RemoveRange(db.YardLinks);
    db.Events.RemoveRange(db.Events);
    db.MediaAssets.RemoveRange(db.MediaAssets);
    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(new { deleted = true });
});

app.MapFallbackToFile("index.html");

app.Run();

static string? ReadString(IFormCollection form, string key)
{
    return form.TryGetValue(key, out var values) && !string.IsNullOrWhiteSpace(values.FirstOrDefault())
        ? values.First()!.Trim()
        : null;
}

static int? ReadInt(IFormCollection form, string key)
{
    return int.TryParse(ReadString(form, key), out var value) ? value : null;
}

static bool? ReadBool(IFormCollection form, string key)
{
    var value = ReadString(form, key);
    return value is null
        ? null
        : bool.TryParse(value, out var parsed)
            ? parsed
            : value is "1" or "yes" or "on";
}

static DateTimeOffset? ReadDateTimeOffset(IFormCollection form, string key)
{
    return DateTimeOffset.TryParse(ReadString(form, key), out var value) ? value : null;
}
