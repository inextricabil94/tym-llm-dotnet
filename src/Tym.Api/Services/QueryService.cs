using Microsoft.EntityFrameworkCore;
using OpenAI.Chat;
using Tym.Api.Contracts;
using Tym.Api.Data;
using Tym.Api.Domain;

namespace Tym.Api.Services;

public interface IQueryService
{
    Task<QueryResponse> AnswerAsync(QueryRequest request, CancellationToken cancellationToken);
}

public sealed class QueryService : IQueryService
{
    private readonly TymDbContext _db;
    private readonly ITokenSimilarity _similarity;
    private readonly IConfiguration _configuration;
    private readonly ILogger<QueryService> _logger;

    public QueryService(
        TymDbContext db,
        ITokenSimilarity similarity,
        IConfiguration configuration,
        ILogger<QueryService> logger)
    {
        _db = db;
        _similarity = similarity;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<QueryResponse> AnswerAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            return new QueryResponse("Please provide a question.", Array.Empty<ScoredEventDto>(), Array.Empty<string>());
        }

        var referenceTime = request.ReferenceTime ?? DateTimeOffset.UtcNow;
        var events = await _db.Events.ToListAsync(cancellationToken);
        if (events.Count == 0)
        {
            return new QueryResponse(
                "No TYM events have been ingested yet. POST text to /ingest first.",
                Array.Empty<ScoredEventDto>(),
                new[] { "The timeline database is empty." });
        }

        var maxEvents = Math.Clamp(request.MaxEvents, 1, 20);
        var scored = ScoreEvents(request.Question, events, referenceTime)
            .OrderByDescending(e => e.Score)
            .ThenBy(e => e.Event.MediaStartSeconds ?? double.MaxValue)
            .ThenBy(e => e.Event.TimestampStart ?? e.Event.CreatedAt)
            .Take(maxEvents)
            .ToList();

        var evidence = scored.Select(s => new ScoredEventDto(
            EventDto.FromEntity(s.Event),
            Math.Round(s.Score, 4),
            Math.Round(s.SemanticScore, 4),
            Math.Round(s.FreshnessScore, 4),
            s.WhySelected)).ToList();

        var notes = BuildNotes(request.Question, scored);
        var answer = await TryGenerateLlmAnswerAsync(request.Question, scored, notes, cancellationToken)
            ?? BuildDeterministicAnswer(request.Question, scored, notes);

        return new QueryResponse(answer, evidence, notes);
    }

    private IEnumerable<ScoredEvent> ScoreEvents(string question, IReadOnlyList<TimelineEvent> events, DateTimeOffset referenceTime)
    {
        var asksCurrentState = TextUtil.ContainsAny(question, "current", "now", "latest", "still", "status", "today");
        var asksWhy = TextUtil.ContainsAny(question, "why", "because", "cause", "caused", "reason", "blocked");
        var asksHistory = TextUtil.ContainsAny(question, "timeline", "history", "what happened", "sequence", "first", "before", "after");
        var asksVideo = TextUtil.ContainsAny(question, "video", "frame", "scene", "visible", "shown", "footage", "camera", "screen", "watch", "timestamp");
        var requestedMediaSeconds = TextUtil.TryParseMediaTimestampSeconds(question);

        foreach (var e in events)
        {
            var semantic = _similarity.Score(question, TextForSimilarity(e));
            var eventTime = e.TimestampStart ?? e.CreatedAt;
            var distanceDays = Math.Abs((referenceTime - eventTime).TotalDays);
            var freshness = 1.0 / (1.0 + distanceDays / 30.0);
            var activeBonus = e.IsSuperseded ? -0.20 : 0.08;
            var statusBonus = string.IsNullOrWhiteSpace(e.StatusAfter) ? 0 : 0.07;
            var causalBonus = asksWhy && TextUtil.ContainsAny(e.EventType, "causal", "blocker") ? 0.16 : 0;
            var historyBonus = asksHistory ? 0.05 : 0;
            var videoBonus = asksVideo && e.Modality.StartsWith("video", StringComparison.OrdinalIgnoreCase) ? 0.18 : 0;
            var mediaTimestampBonus = 0.0;

            if (requestedMediaSeconds.HasValue && e.MediaStartSeconds.HasValue)
            {
                var mediaDistance = Math.Abs(requestedMediaSeconds.Value - e.MediaStartSeconds.Value);
                mediaTimestampBonus = 0.25 / (1.0 + mediaDistance / 10.0);
            }

            var score = asksCurrentState
                ? (0.44 * semantic) + (0.36 * freshness) + activeBonus + statusBonus + causalBonus + videoBonus + mediaTimestampBonus
                : (0.58 * semantic) + (0.18 * freshness) + activeBonus + statusBonus + causalBonus + historyBonus + videoBonus + mediaTimestampBonus;

            if (semantic == 0 && !asksCurrentState && !asksHistory && !asksWhy && !asksVideo)
            {
                score *= 0.30;
            }

            yield return new ScoredEvent(
                e,
                score,
                semantic,
                freshness,
                ExplainSelection(e, semantic, freshness));
        }
    }

    private async Task<string?> TryGenerateLlmAnswerAsync(
        string question,
        IReadOnlyList<ScoredEvent> scoredEvents,
        IReadOnlyList<string> notes,
        CancellationToken cancellationToken)
    {
        var apiKey = _configuration["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var model = _configuration["OpenAI:Model"] ?? Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        try
        {
            var client = new ChatClient(model: model, apiKey: apiKey);
            var context = string.Join("\n", scoredEvents.Select((s, i) =>
                $"[{i + 1}] id={s.Event.Id}; time={(s.Event.TimestampStart?.ToString("O") ?? "unknown")}; " +
                $"sequence_yards={s.Event.SequenceYards}; freshness_yards={s.Event.FreshnessYards:F2}; " +
                $"modality={s.Event.Modality}; media_time={(s.Event.MediaStartSeconds.HasValue ? TextUtil.FormatMediaTimestamp(s.Event.MediaStartSeconds.Value) : "n/a")}; " +
                $"media_uri={s.Event.MediaUri ?? "n/a"}; superseded={s.Event.IsSuperseded}; type={s.Event.EventType}; subject={s.Event.Subject}; " +
                $"status_after={s.Event.StatusAfter ?? "unknown"}; description={s.Event.Description}"));

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage("""
You are TYM, a Time Yards Model assistant.
Answer only from the supplied event context.
Prefer the latest non-superseded event for current-status questions.
For why/cause questions, explain the closest causal or blocker event, then earlier timeline causes if relevant.
Mention stale or superseded facts when they matter.
For video questions, cite media timestamps such as 00:15 and describe the closest visual events.
Be explicit about uncertainty if dates, video captions, or event order are missing.
"""),
                new UserChatMessage($$"""
Question:
{{question}}

Event context:
{{context}}

Notes:
{{string.Join("\n", notes)}}
""")
            };

            ChatCompletion completion = await client.CompleteChatAsync(messages, cancellationToken: cancellationToken);
            return completion.Content.Count > 0 ? completion.Content[0].Text : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAI answer generation failed; using deterministic answer.");
            return null;
        }
    }

    private static string BuildDeterministicAnswer(string question, IReadOnlyList<ScoredEvent> scoredEvents, IReadOnlyList<string> notes)
    {
        var latestActive = scoredEvents
            .Where(s => !s.Event.IsSuperseded)
            .OrderByDescending(s => s.Event.TimestampStart ?? s.Event.CreatedAt)
            .FirstOrDefault();

        var timeline = scoredEvents
            .OrderBy(s => s.Event.TimestampStart ?? s.Event.CreatedAt)
            .Select(s =>
            {
                var date = s.Event.TimestampStart?.ToString("yyyy-MM-dd") ?? "unknown date";
                var media = s.Event.MediaStartSeconds.HasValue ? $" [video {TextUtil.FormatMediaTimestamp(s.Event.MediaStartSeconds.Value)}]" : "";
                var status = string.IsNullOrWhiteSpace(s.Event.StatusAfter) ? "" : $" Status: {s.Event.StatusAfter}.";
                var stale = s.Event.IsSuperseded ? " Superseded by a later event." : "";
                return $"- {date}{media}: {s.Event.Description}{status}{stale}";
            });

        var answer = latestActive is null
            ? "I found relevant events, but all selected events appear to be superseded or stale."
            : $"The strongest current TYM signal is: {latestActive.Event.Description}";

        return $$"""
{{answer}}

Timeline evidence:
{{string.Join("\n", timeline)}}

TYM notes:
{{string.Join("\n", notes.Select(n => "- " + n))}}
""";
    }

    private static IReadOnlyList<string> BuildNotes(string question, IReadOnlyList<ScoredEvent> scoredEvents)
    {
        var notes = new List<string>();

        if (scoredEvents.Any(s => s.Event.IsSuperseded))
        {
            notes.Add("Some retrieved events are marked superseded, so they should be treated as history rather than current state.");
        }

        if (scoredEvents.Any(s => !s.Event.TimestampStart.HasValue))
        {
            notes.Add("Some events have unknown timestamps; their sequence yards may rely on ingestion order.");
        }

        if (TextUtil.ContainsAny(question, "current", "now", "latest", "still", "status"))
        {
            notes.Add("Current-state mode: freshness yards and non-superseded status changes were weighted more heavily.");
        }

        if (TextUtil.ContainsAny(question, "why", "cause", "because", "blocked", "reason"))
        {
            notes.Add("Causal mode: blocker and causal-update events received an additional score bonus.");
        }

        if (scoredEvents.Any(s => s.Event.Modality.StartsWith("video", StringComparison.OrdinalIgnoreCase)))
        {
            notes.Add("Video mode: selected evidence may include frame-level events with media timestamps and frame URIs.");
        }

        return notes;
    }

    private static string ExplainSelection(TimelineEvent e, double semantic, double freshness)
    {
        var parts = new List<string>
        {
            $"semantic={semantic:F2}",
            $"freshness={freshness:F2}",
            $"sequence_yards={e.SequenceYards}",
            $"freshness_yards={e.FreshnessYards:F1}",
            $"modality={e.Modality}"
        };

        if (e.MediaStartSeconds.HasValue) parts.Add($"media_time={TextUtil.FormatMediaTimestamp(e.MediaStartSeconds.Value)}");
        if (e.IsSuperseded) parts.Add("superseded");
        if (!string.IsNullOrWhiteSpace(e.StatusAfter)) parts.Add($"status_after={e.StatusAfter}");

        return string.Join(", ", parts);
    }

    private static string TextForSimilarity(TimelineEvent e)
    {
        var media = e.MediaStartSeconds.HasValue ? TextUtil.FormatMediaTimestamp(e.MediaStartSeconds.Value) : string.Empty;
        return $"{e.Subject} {e.Description} {e.RelatedEntitiesJson} {e.EventType} {e.StatusBefore} {e.StatusAfter} {e.Modality} {media}";
    }

    private sealed record ScoredEvent(
        TimelineEvent Event,
        double Score,
        double SemanticScore,
        double FreshnessScore,
        string WhySelected);
}
