using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.Lame;
using SDL2;

namespace TuneLab.Utils;

internal static class MacOSNativeLibraryResolver
{
    static readonly object mSync = new();
    static bool mRegistered;

    public static void Register()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        lock (mSync)
        {
            if (mRegistered)
                return;

            Assembly[] assemblies =
            [
                typeof(App).Assembly,
                typeof(SDL).Assembly,
                typeof(LameMP3FileWriter).Assembly,
            ];

            foreach (var assembly in assemblies.Distinct())
                NativeLibrary.SetDllImportResolver(assembly, Resolve);
            mRegistered = true;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string? bundledName = libraryName switch
        {
            "SDL2" => "libSDL2.dylib",
            "TuneLabVst3Host" => "libTuneLabVst3Host.dylib",
            _ when libraryName.Contains("lame", StringComparison.OrdinalIgnoreCase) => "libmp3lame.dylib",
            _ => null,
        };

        if (bundledName == null)
            return IntPtr.Zero;

        var frameworkFolder = new[]
        {
            Path.Combine(PathManager.ExcutableFolder, "..", "Frameworks"),
            Path.Combine(PathManager.ExcutableFolder, "..", "..", "Frameworks"),
        }.Select(Path.GetFullPath).FirstOrDefault(Directory.Exists);
        if (frameworkFolder == null)
            return IntPtr.Zero;

        var libraryPath = Path.Combine(frameworkFolder, bundledName);
        if (!File.Exists(libraryPath))
            throw new DllNotFoundException($"The macOS app bundle is missing its native library: {libraryPath}");

        return NativeLibrary.Load(libraryPath);
    }
}
