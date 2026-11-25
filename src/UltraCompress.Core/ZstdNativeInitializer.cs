using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UltraCompress.Core;

/// <summary>
/// Ensures ZstdNet resolves <c>libzstd</c> across Windows (<c>libzstd.dll</c>) and Linux (<c>libzstd.so.1</c>)
/// without throwing if initialized from multiple assemblies.
/// </summary>
public static class ZstdNativeInitializer
{
    private static int _initialized;

    public static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(ZstdNet.Compressor).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // Another module already registered a DllImportResolver for ZstdNet.
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals("libzstd", StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        string[] candidates = OperatingSystem.IsWindows()
            ? ["libzstd", "libzstd.dll"]
            : ["libzstd", "libzstd.so.1", "libzstd.so"];

        foreach (var name in candidates)
        {
            if (NativeLibrary.TryLoad(name, assembly, searchPath, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }
}
