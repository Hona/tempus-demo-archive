using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TempusDemoArchive.Jobs.StvProcessor;

public static partial class StvParser
{
    private const string DllName = "tf_demo_parser";

    static StvParser()
    {
        NativeLibrary.SetDllImportResolver(typeof(StvParser).Assembly, ResolveLibrary);
    }

    public static StvParserResponse ExtractStvData(string fileName)
    {
        var resultPtr = analyze_demo(fileName);

        try
        {
            if (resultPtr == IntPtr.Zero)
            {
                throw new InvalidOperationException("STV was invalid, parsing failed.");
            }

            var result = Marshal.PtrToStringUTF8(resultPtr);
            if (result is null)
            {
                throw new InvalidOperationException("STV was invalid, parsing failed.");
            }

            return JsonSerializer.Deserialize<StvParserResponse>(result)
                   ?? throw new InvalidOperationException("STV was invalid, parsing failed.");
        }
        finally
        {
            free_string(resultPtr);
        }
    }

    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, DllName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        var baseDirectory = AppContext.BaseDirectory;
        var fileName = GetLibraryFileName();
        var runtimeId = GetRuntimeId();

        var candidatePaths = new[]
        {
            Path.Combine(baseDirectory, fileName),
            Path.Combine(baseDirectory, "runtimes", runtimeId, "native", fileName)
        };

        foreach (var path in candidatePaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            if (NativeLibrary.TryLoad(path, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    private static string GetLibraryFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "tf_demo_parser.dll";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "libtf_demo_parser.dylib";
        }

        return "libtf_demo_parser.so";
    }

    private static string GetRuntimeId()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "win"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "osx"
                : "linux";

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            Architecture.X86 => "x86",
            _ => "x64"
        };

        return $"{os}-{arch}";
    }

    [LibraryImport(DllName, EntryPoint = "analyze_demo", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr analyze_demo(string path);

    [LibraryImport(DllName, EntryPoint = "free_string", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void free_string(IntPtr ptr);
}
