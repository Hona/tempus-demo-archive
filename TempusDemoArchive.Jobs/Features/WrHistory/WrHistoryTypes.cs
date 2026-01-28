namespace TempusDemoArchive.Jobs;

public record WrHistoryEntry(string Player, string Class, string Map, string RecordType, string Source,
    string RecordTime, string? RunTime, string? Split, string? Improvement, bool Inferred, DateTime? Date = null,
    ulong? DemoId = null, long? SteamId64 = null, string? SteamId = null, string? SteamCandidates = null,
    bool IsLookup = false, int ChatIndex = 0, int? ChatTick = null);

public record UserIdentity(long? SteamId64, string? SteamId);

internal sealed record ResolvedIdentity(UserIdentity? Identity, string? SteamCandidates);

internal sealed record UserIdentityCandidate(string Name, long? SteamId64, string? SteamId);

internal sealed class UserIdentityCandidateComparer : IEqualityComparer<UserIdentityCandidate>
{
    public static readonly UserIdentityCandidateComparer Instance = new();

    public bool Equals(UserIdentityCandidate? x, UserIdentityCandidate? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null)
        {
            return false;
        }

        return string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase)
               && x.SteamId64 == y.SteamId64
               && string.Equals(x.SteamId, y.SteamId, StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode(UserIdentityCandidate obj)
    {
        return HashCode.Combine(obj.Name.ToUpperInvariant(), obj.SteamId64,
            obj.SteamId?.ToUpperInvariant());
    }
}
