namespace TempusDemoArchive.Jobs;

internal static class DbChunk
{
    public const int DefaultSize = 900;

    public static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> items, int size = DefaultSize)
    {
        if (items.Count == 0)
        {
            yield break;
        }

        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "Chunk size must be positive.");
        }

        for (var i = 0; i < items.Count; i += size)
        {
            var chunk = items.Skip(i).Take(size).ToList();
            if (chunk.Count == 0)
            {
                yield break;
            }

            yield return chunk;
        }
    }
}
