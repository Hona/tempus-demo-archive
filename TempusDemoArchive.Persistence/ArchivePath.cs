namespace TempusDemoArchive.Persistence;

public static class ArchivePath
{
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TempusDemoArchive");
    public static readonly string Db = Path.Combine(Root, "tempus-demo-archive.db");
    public static readonly string RawDemoList = Path.Combine(Root, "archived_demos.txt");
    // Release (not working for old demos 2014 era): public static readonly string DemoParserExe = Path.Combine(Root, "parser-x86_64-windows.exe");
    // 👇 My modification (working for old demos onwards) 
    public static readonly string DemoParserExe = Path.Combine(Root, "parse_demo.exe");
    public static readonly string DemosRoot = Path.Combine(Root, "demos");
    
    public static readonly string TempRoot = Path.Combine(Root, "temp");
    
    public static string GetDemoFilePath(ulong id) => Path.Combine(DemosRoot, $"{id}.dem");

    public static void EnsureAllCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DemosRoot);
        Directory.CreateDirectory(TempRoot);
    }
}