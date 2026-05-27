namespace Tym.Api.Domain;

public sealed class YardLink
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FromEventId { get; set; }
    public Guid ToEventId { get; set; }

    // chronological, semantic, causal, supersedes
    public string LinkType { get; set; } = "chronological";

    // Meaning depends on LinkType:
    // chronological = days between events
    // semantic = 1 - token similarity
    // causal = inferred causal hop distance
    // supersedes = 1 for direct replacement
    public double Distance { get; set; }
    public double Confidence { get; set; } = 0.50;
}
