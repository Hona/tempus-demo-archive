namespace TempusDemoArchive.Jobs;

internal static class PlaytimeCalculator
{
    private static readonly HashSet<string> PlayableClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "soldier",
        "demoman"
    };

    public static bool TryComputeDemoTotals(PlaytimeDemoMeta meta,
        HashSet<int> userIds,
        IReadOnlyList<PlaytimeSpawnEvent> spawns,
        IReadOnlyList<PlaytimeDeathEvent> deaths,
        IReadOnlyList<PlaytimeTeamChangeEvent> teamChanges,
        out PlaytimeTotals totals)
    {
        totals = new PlaytimeTotals();

        if (!meta.IntervalPerTick.HasValue || meta.IntervalPerTick.Value <= 0)
        {
            return false;
        }

        var demoEndTick = meta.HeaderTicks ?? 0;
        if (demoEndTick <= 0)
        {
            demoEndTick = Math.Max(GetMaxTick(spawns), Math.Max(GetMaxTick(deaths), GetMaxTick(teamChanges)));
        }

        if (demoEndTick <= 0)
        {
            return false;
        }

        var hadTime = false;
        foreach (var userId in userIds)
        {
            var events = BuildPlayerEvents(userId, spawns, deaths, teamChanges);
            if (events.Count == 0)
            {
                continue;
            }

            hadTime |= AccumulateEvents(events, demoEndTick, meta.IntervalPerTick.Value, totals);
        }

        return hadTime;
    }

    public static int GetDemoEndTick(PlaytimeDemoMeta meta,
        IReadOnlyList<PlaytimeSpawnEvent> spawns,
        IReadOnlyList<PlaytimeDeathEvent> deaths,
        IReadOnlyList<PlaytimeTeamChangeEvent> teamChanges)
    {
        var demoEndTick = meta.HeaderTicks ?? 0;
        if (demoEndTick > 0)
        {
            return demoEndTick;
        }

        return Math.Max(GetMaxTick(spawns), Math.Max(GetMaxTick(deaths), GetMaxTick(teamChanges)));
    }

    private static List<PlaytimePlayerEvent> BuildPlayerEvents(int userId,
        IReadOnlyList<PlaytimeSpawnEvent> spawns,
        IReadOnlyList<PlaytimeDeathEvent> deaths,
        IReadOnlyList<PlaytimeTeamChangeEvent> teamChanges)
    {
        var events = new List<PlaytimePlayerEvent>();
        foreach (var spawn in spawns)
        {
            if (spawn.UserId == userId)
            {
                events.Add(new PlaytimePlayerEvent(spawn.Tick, PlaytimeEventKind.Spawn, spawn.Class));
            }
        }

        foreach (var death in deaths)
        {
            if (death.UserId == userId)
            {
                events.Add(new PlaytimePlayerEvent(death.Tick, PlaytimeEventKind.Death, null));
            }
        }

        foreach (var change in teamChanges)
        {
            if (change.UserId == userId && IsSpectatorEvent(change))
            {
                events.Add(new PlaytimePlayerEvent(change.Tick, PlaytimeEventKind.Spectator, null));
            }
        }

        events.Sort((left, right) =>
        {
            var tickCompare = left.Tick.CompareTo(right.Tick);
            if (tickCompare != 0)
            {
                return tickCompare;
            }

            return left.Kind.CompareTo(right.Kind);
        });

        return events;
    }

    private static bool AccumulateEvents(IReadOnlyList<PlaytimePlayerEvent> events, int demoEndTick, double intervalPerTick,
        PlaytimeTotals totals)
    {
        var alive = false;
        var currentClass = string.Empty;
        var startTick = 0;
        var hasTime = false;

        foreach (var entry in events)
        {
            if (entry.Kind == PlaytimeEventKind.Spawn)
            {
                if (alive)
                {
                    hasTime |= AddInterval(totals, currentClass, startTick, entry.Tick, intervalPerTick);
                }

                var spawnClass = entry.Class ?? string.Empty;
                if (IsPlayableClass(spawnClass))
                {
                    alive = true;
                    currentClass = spawnClass;
                    startTick = entry.Tick;
                }
                else
                {
                    alive = false;
                }

                continue;
            }

            if (!alive)
            {
                continue;
            }

            hasTime |= AddInterval(totals, currentClass, startTick, entry.Tick, intervalPerTick);
            alive = false;
        }

        if (alive)
        {
            hasTime |= AddInterval(totals, currentClass, startTick, demoEndTick, intervalPerTick);
        }

        return hasTime;
    }

    private static bool AddInterval(PlaytimeTotals totals, string className, int startTick, int endTick,
        double intervalPerTick)
    {
        var tickDelta = endTick - startTick;
        if (tickDelta <= 0)
        {
            return false;
        }

        var seconds = tickDelta * intervalPerTick;
        if (seconds <= 0)
        {
            return false;
        }

        totals.TotalSeconds += seconds;
        if (string.Equals(className, "soldier", StringComparison.OrdinalIgnoreCase))
        {
            totals.SoldierSeconds += seconds;
            return true;
        }

        if (string.Equals(className, "demoman", StringComparison.OrdinalIgnoreCase))
        {
            totals.DemoSeconds += seconds;
            return true;
        }

        return false;
    }

    private static bool IsPlayableClass(string className)
    {
        return !string.IsNullOrWhiteSpace(className) && PlayableClasses.Contains(className);
    }

    private static bool IsSpectatorEvent(PlaytimeTeamChangeEvent change)
    {
        if (change.Disconnect)
        {
            return true;
        }

        return string.Equals(change.Team, "spectator", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetMaxTick(IReadOnlyList<PlaytimeSpawnEvent> events)
    {
        return events.Count == 0 ? 0 : events.Max(entry => entry.Tick);
    }

    private static int GetMaxTick(IReadOnlyList<PlaytimeDeathEvent> events)
    {
        return events.Count == 0 ? 0 : events.Max(entry => entry.Tick);
    }

    private static int GetMaxTick(IReadOnlyList<PlaytimeTeamChangeEvent> events)
    {
        return events.Count == 0 ? 0 : events.Max(entry => entry.Tick);
    }
}
