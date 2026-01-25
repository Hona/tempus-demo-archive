namespace TempusDemoArchive.Persistence.Models.STVs;

public class Stv
{
    public ulong DemoId { get; set; }
    public StvHeader Header { get; set; } = new();

    public int? StartTick { get; set; }
    public double? IntervalPerTick { get; set; }
    
    // Custom
    public long DownloadSize { get; set; }
    public long ExtractedFileSize { get; set; }
    public string? ParserVersion { get; set; }
    public DateTime? ParsedAtUtc { get; set; }
    
    public virtual ICollection<StvChat> Chats { get; set; } = new List<StvChat>();
    public virtual ICollection<StvUser> Users { get; set; } = new List<StvUser>();
    public virtual ICollection<StvSpawn> Spawns { get; set; } = new List<StvSpawn>();
    public virtual ICollection<StvTeamChange> TeamChanges { get; set; } = new List<StvTeamChange>();
    public virtual ICollection<StvDeath> Deaths { get; set; } = new List<StvDeath>();
    public virtual ICollection<StvPause> Pauses { get; set; } = new List<StvPause>();
    public virtual Demo? Demo { get; set; }
}

public class StvUser
{
    public ulong DemoId { get; set; }
    
    public string Name { get; set; } = string.Empty;
    public int? UserId { get; set; }
    public string SteamId { get; set; } = string.Empty;
    public string? SteamIdClean { get; set; }
    public long? SteamId64 { get; set; }
    public string? SteamIdKind { get; set; }
    public bool? IsBot { get; set; }
    public int? EntityId { get; set; }
    public string Team { get; set; } = string.Empty;
}
public class StvChat
{
    public ulong DemoId { get; set; }
    
    public string Kind { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public int? Tick { get; set; }
    public int? ClientEntityId { get; set; }
    public int? FromUserId { get; set; }
    
    // Custom
    public int Index { get; set; }
    
    public virtual Stv? Stv { get; set; }
}

public class StvSpawn
{
    public ulong DemoId { get; set; }
    public int Index { get; set; }
    public int Tick { get; set; }
    public int UserId { get; set; }
    public string Class { get; set; } = string.Empty;
    public string Team { get; set; } = string.Empty;
    public virtual Stv? Stv { get; set; }
}

public class StvTeamChange
{
    public ulong DemoId { get; set; }
    public int Index { get; set; }
    public int Tick { get; set; }
    public int UserId { get; set; }
    public string Team { get; set; } = string.Empty;
    public string OldTeam { get; set; } = string.Empty;
    public bool Disconnect { get; set; }
    public bool AutoTeam { get; set; }
    public bool Silent { get; set; }
    public string Name { get; set; } = string.Empty;
    public virtual Stv? Stv { get; set; }
}

public class StvDeath
{
    public ulong DemoId { get; set; }
    public int Index { get; set; }
    public int Tick { get; set; }
    public string Weapon { get; set; } = string.Empty;
    public int VictimUserId { get; set; }
    public int KillerUserId { get; set; }
    public int? AssisterUserId { get; set; }
    public virtual Stv? Stv { get; set; }
}

public class StvPause
{
    public ulong DemoId { get; set; }
    public int Index { get; set; }
    public int FromTick { get; set; }
    public int ToTick { get; set; }
    public virtual Stv? Stv { get; set; }
}


// Owned entity
public class StvHeader
{
    public string DemoType { get; set; } = string.Empty;
    public int? Version { get; set; }
    public int? Protocol { get; set; }
    public string Server { get; set; } = string.Empty;
    public string Nick { get; set; } = string.Empty;
    public string Map { get; set; } = string.Empty;
    public string Game { get; set; } = string.Empty;
    public double? Duration { get; set; }
    public int? Ticks { get; set; }
    public int? Frames { get; set; }
    public int? Signon { get; set; }
}
