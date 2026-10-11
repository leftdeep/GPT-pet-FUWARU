using System;
using System.IO;
using System.Reflection;

namespace GptSpritePet;

internal static class ProjectPaths
{
    internal static string Version => typeof(ProjectPaths).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "未知";
    internal static string Root { get; } = FindRoot();
    internal static string DefaultSkin => Path.Combine(Root, "assets", "dragon-girl-0.40");
    internal static string Decoder => Path.Combine(Root, "runtime", "ffmpeg.exe");

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "pet-release.json")) ||
                File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) ||
                File.Exists(Path.Combine(directory.FullName, "build.ps1"))) return directory.FullName;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    }
}
