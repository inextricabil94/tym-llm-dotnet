using Microsoft.EntityFrameworkCore;
using Tym.Api.Data;
using Tym.Api.Domain;

namespace Tym.Api.Services;

public interface IYardService
{
    Task RebuildAsync(DateTimeOffset referenceTime, CancellationToken cancellationToken);
}

public sealed class YardService : IYardService
{
    private readonly TymDbContext _db;
    private readonly ITokenSimilarity _similarity;

    public YardService(TymDbContext db, ITokenSimilarity similarity)
    {
        _db = db;
        _similarity = similarity;
    }

    public async Task RebuildAsync(DateTimeOffset referenceTime, CancellationToken cancellationToken)
    {
        var events = await _db.Events.ToListAsync(cancellationToken);
        var ordered = events
            .OrderBy(e => e.TimestampStart ?? e.CreatedAt)
            .ThenBy(e => e.CreatedAt)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            var eventTime = e.TimestampStart ?? e.CreatedAt;
            e.SequenceYards = i;
            e.FreshnessYards = Math.Abs((referenceTime - eventTime).TotalDays);
            e.IsSuperseded = false;
            e.SupersededByEventId = null;
        }

        MarkSupersededEvents(ordered);

        _db.YardLinks.RemoveRange(_db.YardLinks);
        await _db.YardLinks.AddRangeAsync(BuildLinks(ordered), cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private void MarkSupersededEvents(IReadOnlyList<TimelineEvent> ordered)
    {
        var statusEvents = ordered
            .Where(e => !string.IsNullOrWhiteSpace(e.StatusAfter))
            .GroupBy(e => TextUtil.NormalizeSubject(e.Subject))
            .Where(g => !string.IsNullOrWhiteSpace(g.Key));

        foreach (var group in statusEvents)
        {
            var latest = group
                .OrderByDescending(e => e.TimestampStart ?? e.CreatedAt)
                .First();

            foreach (var older in group.Where(e => e.Id != latest.Id))
            {
                older.IsSuperseded = true;
                older.SupersededByEventId = latest.Id;
            }
        }
    }

    private IEnumerable<YardLink> BuildLinks(IReadOnlyList<TimelineEvent> ordered)
    {
        for (var i = 0; i < ordered.Count - 1; i++)
        {
            var from = ordered[i];
            var to = ordered[i + 1];
            var days = Math.Abs(((to.TimestampStart ?? to.CreatedAt) - (from.TimestampStart ?? from.CreatedAt)).TotalDays);

            yield return new YardLink
            {
                FromEventId = from.Id,
                ToEventId = to.Id,
                LinkType = "chronological",
                Distance = days,
                Confidence = Math.Min(from.TimeConfidence, to.TimeConfidence)
            };
        }

        foreach (var from in ordered.Where(e => e.IsSuperseded && e.SupersededByEventId.HasValue))
        {
            yield return new YardLink
            {
                FromEventId = from.Id,
                ToEventId = from.SupersededByEventId!.Value,
                LinkType = "supersedes",
                Distance = 1,
                Confidence = 0.85
            };
        }

        foreach (var mediaGroup in ordered
            .Where(e => e.MediaAssetId.HasValue && e.MediaStartSeconds.HasValue)
            .GroupBy(e => e.MediaAssetId!.Value))
        {
            var mediaEvents = mediaGroup
                .OrderBy(e => e.MediaStartSeconds)
                .ThenBy(e => e.SegmentIndex ?? int.MaxValue)
                .ToList();

            for (var i = 0; i < mediaEvents.Count - 1; i++)
            {
                var from = mediaEvents[i];
                var to = mediaEvents[i + 1];
                yield return new YardLink
                {
                    FromEventId = from.Id,
                    ToEventId = to.Id,
                    LinkType = "media_chronological",
                    Distance = Math.Abs((to.MediaStartSeconds ?? 0) - (from.MediaStartSeconds ?? 0)),
                    Confidence = Math.Min(from.Confidence, to.Confidence)
                };
            }
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            for (var j = i + 1; j < ordered.Count && j <= i + 5; j++)
            {
                var from = ordered[i];
                var to = ordered[j];
                var semantic = _similarity.Score(TextForSimilarity(from), TextForSimilarity(to));
                var fromTime = from.TimestampStart ?? from.CreatedAt;
                var toTime = to.TimestampStart ?? to.CreatedAt;
                var days = Math.Abs((toTime - fromTime).TotalDays);

                if (semantic >= 0.18)
                {
                    yield return new YardLink
                    {
                        FromEventId = from.Id,
                        ToEventId = to.Id,
                        LinkType = "semantic",
                        Distance = 1 - semantic,
                        Confidence = semantic
                    };
                }

                if (LooksCausal(from.Description) && days <= 45)
                {
                    yield return new YardLink
                    {
                        FromEventId = from.Id,
                        ToEventId = to.Id,
                        LinkType = "causal",
                        Distance = 1,
                        Confidence = Math.Max(0.50, semantic)
                    };
                }
            }
        }
    }

    private static bool LooksCausal(string text)
    {
        return TextUtil.ContainsAny(text, "because", "caused", "due to", "blocked", "therefore", "led to", "as a result");
    }

    private static string TextForSimilarity(TimelineEvent e)
    {
        var media = e.MediaStartSeconds.HasValue ? TextUtil.FormatMediaTimestamp(e.MediaStartSeconds.Value) : string.Empty;
        return $"{e.Subject} {e.Description} {e.RelatedEntitiesJson} {e.EventType} {e.StatusAfter} {e.Modality} {media}";
    }
}
