namespace Tym.Api.Domain;

public sealed class MediaAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string StoredPath { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }

    public string Source { get; set; } = "video_upload";
    public string Status { get; set; } = "uploaded";
    public string? LastError { get; set; }

    public double? DurationSeconds { get; set; }
    public int FramesExtracted { get; set; }
    public int EventsCreated { get; set; }

    public DateTimeOffset? CapturedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
