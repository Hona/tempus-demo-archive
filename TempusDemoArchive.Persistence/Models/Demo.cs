using TempusDemoArchive.Persistence.Models.STVs;

namespace TempusDemoArchive.Persistence.Models;

public class Demo
{
    public ulong Id { get; set; }
    public required string Url { get; set; }
    public double Date { get; set; }
    public bool StvProcessed { get; set; }
    public bool StvFailed { get; set; }
    public string? StvFailureReason { get; set; }
    
    public virtual Stv? Stv { get; set; }
}
