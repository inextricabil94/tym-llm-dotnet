using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Tym.Api.Contracts;
using Tym.Api.Data;
using Tym.Api.Domain;
using Tym.Api.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Token similarity ranks related text above unrelated text", TokenSimilarityRanksRelatedText),
    ("Yard rebuild marks older status events as superseded", YardRebuildMarksOlderStatusEventsSuperseded),
    ("Query current-state mode prefers latest non-superseded status", QueryCurrentStatePrefersLatestStatus)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.Message}");
        Console.Error.WriteLine($"FAIL {test.Name}");
        Console.Error.WriteLine(ex);
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Test failures:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }

    return 1;
}

Console.WriteLine($"{tests.Length} tests passed.");
return 0;

static Task TokenSimilarityRanksRelatedText()
{
    var similarity = new TokenSimilarity();
    var related = similarity.Score("payment bugs blocked beta release", "beta release was blocked by payment bugs");
    var unrelated = similarity.Score("payment bugs blocked beta release", "camera shows an empty office hallway");

    AssertTrue(related > 0.30, $"Expected related score > 0.30, got {related:0.###}.");
    AssertTrue(related > unrelated, $"Expected related score {related:0.###} > unrelated score {unrelated:0.###}.");
    return Task.CompletedTask;
}

static async Task YardRebuildMarksOlderStatusEventsSuperseded()
{
    await WithDatabaseAsync(async db =>
    {
        var planned = Event(
            subject: "Beta launch",
            eventType: "status_update",
            description: "The beta launch was planned for March 20.",
            timestamp: new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero),
            statusAfter: "planned");

        var approved = Event(
            subject: "Beta launch",
            eventType: "status_update",
            description: "The beta launch was approved for release.",
            timestamp: new DateTimeOffset(2026, 4, 4, 9, 0, 0, TimeSpan.Zero),
            statusAfter: "approved");

        await db.Events.AddRangeAsync(planned, approved);
        await db.SaveChangesAsync();

        var yards = new YardService(db, new TokenSimilarity());
        await yards.RebuildAsync(new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero), CancellationToken.None);

        AssertTrue(planned.IsSuperseded, "Expected older status event to be marked superseded.");
        AssertEqual(approved.Id, planned.SupersededByEventId, "Expected older status event to point at latest status.");
        AssertTrue(!approved.IsSuperseded, "Expected latest status event to remain active.");
        AssertTrue(planned.FreshnessYards > approved.FreshnessYards, "Expected newer event to have fewer freshness yards.");

        var supersedes = await db.YardLinks.AnyAsync(link =>
            link.LinkType == "supersedes"
            && link.FromEventId == planned.Id
            && link.ToEventId == approved.Id);
        AssertTrue(supersedes, "Expected a supersedes yard link from old status to latest status.");
    });
}

static async Task QueryCurrentStatePrefersLatestStatus()
{
    await WithDatabaseAsync(async db =>
    {
        await db.Events.AddRangeAsync(
            Event(
                subject: "Beta launch",
                eventType: "status_update",
                description: "QA found payment bugs that blocked release testing.",
                timestamp: new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero),
                statusAfter: "blocked"),
            Event(
                subject: "Beta launch",
                eventType: "status_update",
                description: "The payment bugs were fixed.",
                timestamp: new DateTimeOffset(2026, 4, 2, 9, 0, 0, TimeSpan.Zero),
                statusAfter: "fixed"),
            Event(
                subject: "Beta launch",
                eventType: "status_update",
                description: "The beta was approved for release.",
                timestamp: new DateTimeOffset(2026, 4, 4, 9, 0, 0, TimeSpan.Zero),
                statusAfter: "approved"));
        await db.SaveChangesAsync();

        var referenceTime = new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero);
        var yards = new YardService(db, new TokenSimilarity());
        await yards.RebuildAsync(referenceTime, CancellationToken.None);

        var query = new QueryService(
            db,
            new TokenSimilarity(),
            new ConfigurationBuilder().Build(),
            NullLogger<QueryService>.Instance);

        var response = await query.AnswerAsync(
            new QueryRequest("What is the current status of the beta launch?", referenceTime, MaxEvents: 5),
            CancellationToken.None);

        AssertTrue(response.Evidence.Count > 0, "Expected query evidence.");
        AssertTrue(
            response.Evidence.Any(e => e.Event.StatusAfter == "approved" && !e.Event.IsSuperseded),
            "Expected approved status to be retrieved as active evidence.");
        AssertTrue(
            response.Notes.Any(note => note.Contains("Current-state mode", StringComparison.OrdinalIgnoreCase)),
            "Expected current-state weighting note.");
    });
}

static async Task WithDatabaseAsync(Func<TymDbContext, Task> test)
{
    await using var connection = new SqliteConnection("Data Source=:memory:");
    await connection.OpenAsync();

    var options = new DbContextOptionsBuilder<TymDbContext>()
        .UseSqlite(connection)
        .Options;

    await using var db = new TymDbContext(options);
    await db.Database.EnsureCreatedAsync();
    await test(db);
}

static TimelineEvent Event(string subject, string eventType, string description, DateTimeOffset timestamp, string statusAfter)
{
    return new TimelineEvent
    {
        Subject = subject,
        EventType = eventType,
        Description = description,
        TimestampStart = timestamp,
        TimestampEnd = timestamp.AddHours(1),
        TimeConfidence = 0.9,
        Source = "test",
        StatusAfter = statusAfter,
        RelatedEntitiesJson = """["beta","release","payment","bugs"]""",
        RawText = description,
        Modality = "text",
        Confidence = 0.8,
        CreatedAt = timestamp
    };
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected={expected}; Actual={actual}.");
    }
}
