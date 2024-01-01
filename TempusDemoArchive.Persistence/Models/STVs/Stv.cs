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
    
    public virtual ICollection<StvChat> Chats { get; set; }
    public virtual ICollection<StvUser> Users { get; set; }
    public virtual Demo Demo { get; set; }
}

public class StvUser
{
    public ulong DemoId { get; set; }
    
    public string Name { get; set; }
    public int? UserId { get; set; }
    public string SteamId { get; set; }
    public string Team { get; set; }
}
public class StvChat
{
    public ulong DemoId { get; set; }
    
    public string Kind { get; set; }
    public string From { get; set; }
    public string Text { get; set; }
    public int? Tick { get; set; }
    
    // Custom
    public int Index { get; set; }
    
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