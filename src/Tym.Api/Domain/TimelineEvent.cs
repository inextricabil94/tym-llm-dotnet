namespace Tym.Api.Domain;

public sealed class TimelineEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Subject { get; set; } = string.Empty;
    public string EventType { get; set; } = "observation";
    public string Description { get; set; } = string.Empty;

    public DateTimeOffset? TimestampStart { get; set; }
    public DateTimeOffset? TimestampEnd { get; set; }
    public double TimeConfidence { get; set; } = 0.50;

    public string Source { get; set; } = "user_input";
    public string? Actor { get; set; }

    public string? StatusBefore { get; set; }
    public string? StatusAfter { get; set; }

    public string RelatedEntitiesJson { get; set; } = "[]";
    public string RawText { get; set; } = string.Empty;

    // text, video_frame, video_segment, audio_transcript, or other future modalities.
    public string Modality { get; set; } = "text";

    // Optional media provenance. Video events use these fields to preserve both real/ingest time
    // and relative media time, so the TYM graph can answer "what happened at 00:01:20?".
    public Guid? MediaAssetId { get; set; }
    public double? MediaStartSeconds { get; set; }
    public double? MediaEndSeconds { get; set; }
    public int? SegmentIndex { get; set; }
    public string? MediaUri { get; set; }
    public string? ThumbnailUri { get; set; }

    // One chronological yard = one day in text mode.
    // For video events, media yards are kept in MediaStartSeconds/MediaEndSeconds.
    public double FreshnessYards { get; set; }

    // Position of the event in the current reconstructed timeline.
    public int SequenceYards { get; set; }

    public double Confidence { get; set; } = 0.50;

    public bool IsSuperseded { get; set; }
    public Guid? SupersededByEventId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
