namespace TempusDemoArchive.Jobs;

internal sealed record PlaytimeDemoMeta(ulong DemoId, string Map, string Server, double? IntervalPerTick, int? HeaderTicks);

internal sealed record PlaytimeSpawnEvent(ulong DemoId, int UserId, int Tick, string Class, string Team);

internal sealed record PlaytimeDeathEvent(ulong DemoId, int UserId, int Tick);

internal sealed record PlaytimeTeamChangeEvent(ulong DemoId, int UserId, int Tick, string Team, bool Disconnect);

internal sealed record PlaytimePlayerEvent(int Tick, PlaytimeEventKind Kind, string? Class);

internal enum PlaytimeEventKind
{
    Spectator = 0,
    Death = 1,
    Spawn = 2
}

internal sealed class PlaytimeTotals
{
    public double SoldierSeconds { get; set; }
    public double DemoSeconds { get; set; }
    public double TotalSeconds { get; set; }
}
