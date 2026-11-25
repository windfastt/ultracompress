using System.Runtime.CompilerServices;
using UltraCompress.Core;

namespace UltraCompress.Worker.Backends;

internal static class ZstdNativeBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        ZstdNativeInitializer.EnsureInitialized();
    }
}
