namespace Tym.Api.Services;

public interface ITokenSimilarity
{
    double Score(string a, string b);
}

public sealed class TokenSimilarity : ITokenSimilarity
{
    public double Score(string a, string b)
    {
        var left = TextUtil.Keywords(a, 50).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var right = TextUtil.Keywords(b, 50).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        var intersection = left.Intersect(right, StringComparer.OrdinalIgnoreCase).Count();
        var union = left.Union(right, StringComparer.OrdinalIgnoreCase).Count();

        return union == 0 ? 0 : (double)intersection / union;
    }
}
