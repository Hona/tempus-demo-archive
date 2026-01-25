namespace TempusDemoArchive.Persistence.Models.STVs;

public class Stv
{
    public ulong DemoId { get; set; }
    public StvHeader Header { get; set; }

    public int? StartTick { get; set; }
    public double? IntervalPerTick { get; set; }
    
    // Custom
    public long DownloadSize { get; set; }
    public long ExtractedFileSize { get; set; }
    public string? ParserVersion { get; set; }
    public DateTime? ParsedAtUtc { get; set; }
    
    public virtual ICollection<StvChat> Chats { get; set; }
    public virtual ICollection<StvUser> Users { get; set; }
    public virtual ICollection<StvSpawn> Spawns { get; set; }
    public virtual ICollection<StvTeamChange> TeamChanges { get; set; }
    public virtual ICollection<StvDeath> Deaths { get; set; }
    public virtual ICollection<StvPause> Pauses { get; set; }
    public virtual Demo Demo { get; set; }
}

public class StvUser
{
    public ulong DemoId { get; set; }
    
    public string Name { get; set; }
    public int? UserId { get; set; }
    public string SteamId { get; set; }
    public string? SteamIdClean { get; set; }
    public long? SteamId64 { get; set; }
    public string? SteamIdKind { get; set; }
    public bool? IsBot { get; set; }
    public int? EntityId { get; set; }
    public string Team { get; set; }
}
public class StvChat
{
    public ulong DemoId { get; set; }
    
    public string Kind { get; set; }
    public string From { get; set; }
    public string Text { get; set; }
    public int? Tick { get; set; }
    public int? ClientEntityId { get; set; }
    public int? FromUserId { get; set; }
    
    // Custom
    public int Index { get; set; }
    
    public virtual Stv Stv { get; set; }
}

public class StvSpawn
{
    public ulong DemoId { get; set; }
    public int Index { get; set; }
    public int Tick { get; set; }
    public int UserId { get; set; }
    public string Class { get; set; }
    public string Team { get; set; }
    public virtual Stv Stv { get; set; }
}

public class StvTeamChange
{
    public ulong DemoId { get; set; }
    public int Index { get; set; }
    public int Tick { get; set; }
    public int UserId { get; set; }
    public string Team { get; set; }
    public string OldTeam { get; set; }
    public bool Disconnect { get; set; }
    public bool AutoTeam { get; set; }
    public bool Silent { get; set; }
    public string Name { get; set; }
    public virtual Stv Stv { get; set; }
}

public class StvDeath
{
    public ulong DemoId { get; set; }
    public int Index { get; set; }
    public int Tick { get; set; }
    public string Weapon { get; set; }
    public int VictimUserId { get; set; }
    public int KillerUserId { get; set; }
    public int? AssisterUserId { get; set; }
    public virtual Stv Stv { get; set; }
}

public class StvPause
{
    public ulong DemoId { get; set; }
    public int Index { get; set; }
    public int FromTick { get; set; }
    public int ToTick { get; set; }
    public virtual Stv Stv { get; set; }
}


// Owned entity
public class StvHeader
{
    public string DemoType { get; set; }
    public int? Version { get; set; }
    public int? Protocol { get; set; }
    public string Server { get; set; }
    public string Nick { get; set; }
    public string Map { get; set; }
    public string Game { get; set; }
    public double? Duration { get; set; }
    public int? Ticks { get; set; }
    public int? Frames { get; set; }
    public int? Signon { get; set; }
}
