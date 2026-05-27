using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAI.Chat;
using Tym.Api.Contracts;
using Tym.Api.Domain;

namespace Tym.Api.Services;

public interface IEventExtractor
{
    Task<IReadOnlyList<TimelineEvent>> ExtractAsync(IngestRequest request, DateTimeOffset referenceTime, CancellationToken cancellationToken);
}

public sealed class HybridEventExtractor : IEventExtractor
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<HybridEventExtractor> _logger;

    public HybridEventExtractor(IConfiguration configuration, ILogger<HybridEventExtractor> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TimelineEvent>> ExtractAsync(
        IngestRequest request,
        DateTimeOffset referenceTime,
        CancellationToken cancellationToken)
    {
        var apiKey = _configuration["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var model = _configuration["OpenAI:Model"] ?? Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                var extractor = new OpenAiEventExtractor(apiKey, model);
                var events = await extractor.ExtractAsync(request, referenceTime, cancellationToken);
                if (events.Count > 0)
                {
                    return events;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenAI event extraction failed; using heuristic fallback.");
            }
        }

        return HeuristicEventExtractor.Extract(request, referenceTime);
    }
}

internal sealed class OpenAiEventExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ChatClient _client;

    public OpenAiEventExtractor(string apiKey, string model)
    {
        _client = new ChatClient(model: model, apiKey: apiKey);
    }

    public async Task<IReadOnlyList<TimelineEvent>> ExtractAsync(
        IngestRequest request,
        DateTimeOffset referenceTime,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage($$"""
You are the event extraction component for TYM, a Time Yards Model.
Reference time: {{referenceTime:O}}

Extract event-centric timeline facts from the user's text.
Rules:
- Split the text into meaningful events, not every clause.
- Normalize dates to ISO-8601 when possible.
- Use null for unknown timestamps.
- Identify status changes and whether a status after the event is active, blocked, delayed, cancelled, resolved, planned, proposed, paused, or unknown.
- Preserve uncertainty with confidence numbers between 0 and 1.
- Do not invent facts not found in the text.
"""),
            new UserChatMessage(request.Text)
        };

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "tym_event_extraction",
                jsonSchema: BinaryData.FromBytes(EventExtractionSchemaUtf8()),
                jsonSchemaIsStrict: true)
        };

        ChatCompletion completion = await _client.CompleteChatAsync(messages, options, cancellationToken);
        var json = completion.Content[0].Text;
        var envelope = JsonSerializer.Deserialize<ExtractionEnvelope>(json, JsonOptions);

        var source = string.IsNullOrWhiteSpace(request.Source) ? "user_input" : request.Source.Trim();
        var results = new List<TimelineEvent>();

        foreach (var item in envelope?.Events ?? new List<ExtractedEvent>())
        {
            var description = string.IsNullOrWhiteSpace(item.Description) ? request.Text : item.Description.Trim();
            var subject = string.IsNullOrWhiteSpace(item.Subject) ? TextUtil.MakeSubject(description) : item.Subject.Trim();

            results.Add(new TimelineEvent
            {
                Subject = subject,
                EventType = string.IsNullOrWhiteSpace(item.EventType) ? TextUtil.InferEventType(description) : item.EventType.Trim(),
                Description = description,
                TimestampStart = ParseDate(item.TimestampStart),
                TimestampEnd = ParseDate(item.TimestampEnd),
                TimeConfidence = Clamp01(item.TimeConfidence),
                Source = source,
                Actor = NullIfBlank(item.Actor),
                StatusBefore = NullIfBlank(item.StatusBefore),
                StatusAfter = NullIfBlank(item.StatusAfter),
                RelatedEntitiesJson = JsonSerializer.Serialize(item.RelatedEntities ?? new List<string>(), JsonOptions),
                RawText = request.Text,
                Modality = "text",
                Confidence = Clamp01(item.Confidence),
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        return results;
    }

    private static byte[] EventExtractionSchemaUtf8()
    {
        return """
{
  "type": "object",
  "properties": {
    "events": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "subject": { "type": "string" },
          "event_type": { "type": "string" },
          "description": { "type": "string" },
          "timestamp_start": { "type": ["string", "null"] },
          "timestamp_end": { "type": ["string", "null"] },
          "time_confidence": { "type": "number" },
          "actor": { "type": ["string", "null"] },
          "status_before": { "type": ["string", "null"] },
          "status_after": { "type": ["string", "null"] },
          "related_entities": {
            "type": "array",
            "items": { "type": "string" }
          },
          "confidence": { "type": "number" }
        },
        "required": [
          "subject",
          "event_type",
          "description",
          "timestamp_start",
          "timestamp_end",
          "time_confidence",
          "actor",
          "status_before",
          "status_after",
          "related_entities",
          "confidence"
        ],
        "additionalProperties": false
      }
    }
  },
  "required": ["events"],
  "additionalProperties": false
}
"""u8.ToArray();
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string? NullIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) || value.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim();
    }

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value)) return 0.5;
        return Math.Max(0, Math.Min(1, value));
    }

    private sealed record ExtractionEnvelope(
        [property: JsonPropertyName("events")] List<ExtractedEvent> Events);

    private sealed record ExtractedEvent(
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("event_type")] string EventType,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("timestamp_start")] string? TimestampStart,
        [property: JsonPropertyName("timestamp_end")] string? TimestampEnd,
        [property: JsonPropertyName("time_confidence")] double TimeConfidence,
        [property: JsonPropertyName("actor")] string? Actor,
        [property: JsonPropertyName("status_before")] string? StatusBefore,
        [property: JsonPropertyName("status_after")] string? StatusAfter,
        [property: JsonPropertyName("related_entities")] List<string>? RelatedEntities,
        [property: JsonPropertyName("confidence")] double Confidence);
}

internal static class HeuristicEventExtractor
{
    public static IReadOnlyList<TimelineEvent> Extract(IngestRequest request, DateTimeOffset referenceTime)
    {
        var source = string.IsNullOrWhiteSpace(request.Source) ? "user_input" : request.Source.Trim();
        var events = new List<TimelineEvent>();

        foreach (var sentence in TextUtil.SplitSentences(request.Text))
        {
            var keywords = TextUtil.Keywords(sentence, 8);
            var subject = TextUtil.MakeSubject(sentence);

            events.Add(new TimelineEvent
            {
                Subject = subject,
                EventType = TextUtil.InferEventType(sentence),
                Description = sentence,
                TimestampStart = TextUtil.TryParseTime(sentence, referenceTime),
                TimestampEnd = null,
                TimeConfidence = TextUtil.TryParseTime(sentence, referenceTime).HasValue ? 0.70 : 0.25,
                Source = source,
                Actor = null,
                StatusBefore = null,
                StatusAfter = TextUtil.InferStatusAfter(sentence),
                RelatedEntitiesJson = JsonSerializer.Serialize(keywords),
                RawText = request.Text,
                Modality = "text",
                Confidence = 0.45,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        if (events.Count == 0 && !string.IsNullOrWhiteSpace(request.Text))
        {
            var text = request.Text.Trim();
            events.Add(new TimelineEvent
            {
                Subject = TextUtil.MakeSubject(text),
                EventType = TextUtil.InferEventType(text),
                Description = text,
                TimestampStart = TextUtil.TryParseTime(text, referenceTime),
                TimeConfidence = TextUtil.TryParseTime(text, referenceTime).HasValue ? 0.70 : 0.25,
                Source = source,
                StatusAfter = TextUtil.InferStatusAfter(text),
                RelatedEntitiesJson = JsonSerializer.Serialize(TextUtil.Keywords(text, 8)),
                RawText = text,
                Modality = "text",
                Confidence = 0.35,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        return events;
    }
}
