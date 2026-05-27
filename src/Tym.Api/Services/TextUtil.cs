using System.Globalization;
using System.Text.RegularExpressions;

namespace Tym.Api.Services;

public static partial class TextUtil
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "has", "have", "had",
        "he", "her", "his", "i", "in", "is", "it", "its", "of", "on", "or", "our", "she", "that",
        "the", "their", "them", "then", "there", "this", "to", "was", "we", "were", "will", "with", "you",
        "your", "after", "before", "into", "about", "over", "under", "again", "still", "now", "new"
    };

    public static IReadOnlyList<string> SplitSentences(string text)
    {
        var normalized = text.Replace("\r", "\n");
        var firstPass = normalized.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var sentences = new List<string>();

        foreach (var line in firstPass)
        {
            foreach (var part in SentenceRegex().Split(line))
            {
                var trimmed = part.Trim(' ', '\t', '-', '*');
                if (trimmed.Length >= 8)
                {
                    sentences.Add(trimmed);
                }
            }
        }

        return sentences;
    }

    public static IReadOnlyList<string> Keywords(string text, int take = 10)
    {
        return WordRegex()
            .Matches(text.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(w => w.Length > 2 && !StopWords.Contains(w))
            .GroupBy(w => w)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Take(take)
            .Select(g => g.Key)
            .ToList();
    }

    public static string MakeSubject(string text)
    {
        var words = Keywords(text, 6);
        if (words.Count == 0)
        {
            return text.Length <= 80 ? text : text[..80];
        }

        return string.Join(" ", words);
    }

    public static string NormalizeSubject(string subject)
    {
        var words = Keywords(subject, 6);
        return string.Join(" ", words).Trim().ToLowerInvariant();
    }

    public static DateTimeOffset? TryParseTime(string text, DateTimeOffset referenceTime)
    {
        var lower = text.ToLowerInvariant();

        var iso = IsoDateRegex().Match(text);
        if (iso.Success && DateTimeOffset.TryParse(iso.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var isoDate))
        {
            return isoDate;
        }

        var monthDate = MonthDateRegex().Match(text);
        if (monthDate.Success && DateTimeOffset.TryParse(monthDate.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var monthParsed))
        {
            return monthParsed;
        }

        if (lower.Contains("today")) return referenceTime.Date;
        if (lower.Contains("yesterday")) return referenceTime.Date.AddDays(-1);
        if (lower.Contains("tomorrow")) return referenceTime.Date.AddDays(1);
        if (lower.Contains("last week")) return referenceTime.Date.AddDays(-7);
        if (lower.Contains("next week")) return referenceTime.Date.AddDays(7);
        if (lower.Contains("last month")) return referenceTime.Date.AddMonths(-1);
        if (lower.Contains("next month")) return referenceTime.Date.AddMonths(1);

        return null;
    }

    public static string InferEventType(string text)
    {
        var lower = text.ToLowerInvariant();

        if (ContainsAny(lower, "because", "caused", "due to", "blocked by", "led to")) return "causal_update";
        if (ContainsAny(lower, "blocked", "blocker", "bug", "issue", "failed", "outage")) return "blocker";
        if (ContainsAny(lower, "delayed", "postponed", "moved", "cancelled", "canceled", "resumed", "paused")) return "status_change";
        if (ContainsAny(lower, "fixed", "resolved", "completed", "done", "approved")) return "resolution";
        if (ContainsAny(lower, "visible", "shown", "appears", "screen", "frame", "scene", "camera", "video")) return "visual_observation";
        if (ContainsAny(lower, "deadline", "launch", "release", "milestone", "due")) return "milestone";
        if (ContainsAny(lower, "planned", "plan", "scheduled", "target")) return "plan";

        return "observation";
    }

    public static string? InferStatusAfter(string text)
    {
        var lower = text.ToLowerInvariant();

        if (ContainsAny(lower, "cancelled", "canceled")) return "cancelled";
        if (ContainsAny(lower, "delayed", "postponed", "moved")) return "delayed";
        if (ContainsAny(lower, "paused", "blocked", "blocker")) return "blocked";
        if (ContainsAny(lower, "resumed", "restarted", "active")) return "active";
        if (ContainsAny(lower, "fixed", "resolved", "completed", "done", "approved")) return "resolved";
        if (ContainsAny(lower, "planned", "scheduled", "target")) return "planned";

        return null;
    }

    public static bool ContainsAny(string text, params string[] terms)
    {
        return terms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
    }


    public static string FormatMediaTimestamp(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0)
        {
            seconds = 0;
        }

        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    public static double? TryParseMediaTimestampSeconds(string text)
    {
        var match = MediaTimestampRegex().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var parts = match.Value.Split(':').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        return parts.Length switch
        {
            2 => (double)(parts[0] * 60 + parts[1]),
            3 => (double)(parts[0] * 3600 + parts[1] * 60 + parts[2]),
            _ => null
        };
    }

    [GeneratedRegex(@"(?<=[\.!?])\s+")]
    private static partial Regex SentenceRegex();

    [GeneratedRegex(@"\b[a-zA-Z][a-zA-Z0-9_\-]{2,}\b")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b")]
    private static partial Regex IsoDateRegex();

    [GeneratedRegex(@"\b(?:Jan|January|Feb|February|Mar|March|Apr|April|May|Jun|June|Jul|July|Aug|August|Sep|Sept|September|Oct|October|Nov|November|Dec|December)\s+\d{1,2}(?:,\s*\d{4})?\b", RegexOptions.IgnoreCase)]
    private static partial Regex MonthDateRegex();

    [GeneratedRegex(@"\b(?:\d{1,2}:)?\d{1,2}:\d{2}\b")]
    private static partial Regex MediaTimestampRegex();
}
