namespace TempusDemoArchive.Persistence.Models;

public class Demo
{
    public ulong Id { get; set; }
    public required string Url { get; set; }
    public double Date { get; set; }
    public bool StvProcessed { get; set; }
}