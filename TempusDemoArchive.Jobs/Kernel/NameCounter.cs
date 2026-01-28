namespace TempusDemoArchive.Jobs;

internal sealed class NameCounter
{
    private readonly Dictionary<string, int> _counts;

    public NameCounter(IEqualityComparer<string>? comparer = null)
    {
        _counts = new Dictionary<string, int>(comparer ?? StringComparer.OrdinalIgnoreCase);
    }

    public void Track(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _counts.TryGetValue(name, out var current);
        _counts[name] = current + 1;
    }

    public string MostCommonOr(string fallback)
    {
        return _counts
            .OrderByDescending(entry => entry.Value)
            .Select(entry => entry.Key)
            .FirstOrDefault() ?? fallback;
    }
}
