using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UltraCompress.Worker.Backends;

/// <summary>
/// ZstdNet P/Invokes <c>libzstd</c>; on Linux distros the SONAME is often <c>libzstd.so.1</c>.
/// Windows builds rely on ZstdNet's bundled <c>libzstd.dll</c>.
/// </summary>
internal static class ZstdNativeBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // P/Invoke lives in ZstdNet.dll, not the worker assembly.
        NativeLibrary.SetDllImportResolver(typeof(ZstdNet.Compressor).Assembly, Resolve);
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