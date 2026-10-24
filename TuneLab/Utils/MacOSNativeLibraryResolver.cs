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

        var libraryPath = FindLibraryPath(bundledName);
        if (libraryPath == null)
            throw new DllNotFoundException($"The macOS native library was not found: {bundledName}");

        return NativeLibrary.Load(libraryPath);
    }

    static string? FindLibraryPath(string libraryName)
    {
        var executableFolder = Path.GetFullPath(PathManager.ExcutableFolder);
        var bundledFolders = new[]
        {
            Path.Combine(executableFolder, "..", "Frameworks"),
            Path.Combine(executableFolder, "..", "..", "Frameworks"),
            Path.Combine(executableFolder, "Frameworks"),
        };

        foreach (var folder in bundledFolders.Select(Path.GetFullPath))
        {
            var path = Path.Combine(folder, libraryName);
            if (File.Exists(path))
                return path;
        }

        // When running the CLI directly from a source checkout, the native host is
        // built beside TuneLab rather than copied into the CLI output directory.
        // Walk a bounded set of parents so this remains useful for local builds
        // without scanning arbitrary parts of the user's filesystem.
        var parent = Directory.GetParent(executableFolder);
        for (var depth = 0; parent != null && depth < 8; depth++, parent = parent.Parent)
        {
            var binFolder = Path.Combine(parent.FullName, "TuneLab", "bin");
            if (!Directory.Exists(binFolder))
                continue;

            foreach (var frameworkFolder in Directory.EnumerateDirectories(binFolder, "*", SearchOption.TopDirectoryOnly)
                         .Select(configuration => Path.Combine(configuration, "Frameworks")))
            {
                var path = Path.Combine(frameworkFolder, libraryName);
                if (File.Exists(path))
                    return path;
            }
        }

        return null;
    }
}
