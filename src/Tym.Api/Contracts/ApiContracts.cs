using System.Text.Json;
using Tym.Api.Domain;

namespace Tym.Api.Contracts;

public sealed record IngestRequest(
    string Text,
    string? Source = null,
    DateTimeOffset? ReferenceTime = null);

public sealed record IngestResponse(
    int EventsCreated,
    IReadOnlyList<EventDto> Events);

public sealed record QueryRequest(
    string Question,
    DateTimeOffset? ReferenceTime = null,
    int MaxEvents = 8);

public sealed record QueryResponse(
    string Answer,
    IReadOnlyList<ScoredEventDto> Evidence,
    IReadOnlyList<string> Notes);

public sealed record VideoIngestOptions(
    string? Source = null,
    DateTimeOffset? ReferenceTime = null,
    DateTimeOffset? CapturedAt = null,
    int SecondsPerFrame = 5,
    int MaxFrames = 36,
    bool UseVision = true,
    bool UseSceneDetection = false);

public sealed record VideoIngestResponse(
    MediaAssetDto Asset,
    int EventsCreated,
    IReadOnlyList<EventDto> Events,
    IReadOnlyList<string> Notes);

public sealed record MediaAssetDto(
    Guid Id,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    string Source,
    string Status,
    string? LastError,
    double? DurationSeconds,
    int FramesExtracted,
    int EventsCreated,
    DateTimeOffset? CapturedAt,
    DateTimeOffset CreatedAt)
{
    public static MediaAssetDto FromEntity(MediaAsset asset) => new(
        asset.Id,
        asset.OriginalFileName,
        asset.ContentType,
        asset.SizeBytes,
        asset.Source,
        asset.Status,
        asset.LastError,
        asset.DurationSeconds,
        asset.FramesExtracted,
        asset.EventsCreated,
        asset.CapturedAt,
        asset.CreatedAt);
}

public sealed record EventDto(
    Guid Id,
    string Subject,
    string EventType,
    string Description,
    DateTimeOffset? TimestampStart,
    DateTimeOffset? TimestampEnd,
    double TimeConfidence,
    string Source,
    string? Actor,
    string? StatusBefore,
    string? StatusAfter,
    IReadOnlyList<string> RelatedEntities,
    string Modality,
    Guid? MediaAssetId,
    double? MediaStartSeconds,
    double? MediaEndSeconds,
    int? SegmentIndex,
    string? MediaUri,
    string? ThumbnailUri,
    double FreshnessYards,
    int SequenceYards,
    double Confidence,
    bool IsSuperseded,
    Guid? SupersededByEventId)
{
    public static EventDto FromEntity(TimelineEvent e)
    {
        List<string>? related;
        try
        {
            related = JsonSerializer.Deserialize<List<string>>(e.RelatedEntitiesJson);
        }
        catch
        {
            related = new List<string>();
        }

        return new EventDto(
            e.Id,
            e.Subject,
            e.EventType,
            e.Description,
            e.TimestampStart,
            e.TimestampEnd,
            e.TimeConfidence,
            e.Source,
            e.Actor,
            e.StatusBefore,
            e.StatusAfter,
            related ?? new List<string>(),
            e.Modality,
            e.MediaAssetId,
            e.MediaStartSeconds,
            e.MediaEndSeconds,
            e.SegmentIndex,
            e.MediaUri,
            e.ThumbnailUri,
            e.FreshnessYards,
            e.SequenceYards,
            e.Confidence,
            e.IsSuperseded,
            e.SupersededByEventId);
    }
}

public sealed record ScoredEventDto(
    EventDto Event,
    double Score,
    double SemanticScore,
    double FreshnessScore,
    string WhySelected);
