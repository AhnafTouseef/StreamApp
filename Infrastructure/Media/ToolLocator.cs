using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MovieHub.Configuration;

namespace MovieHub.Infrastructure.Media;

public sealed record ToolInfo(string Command, string? Version);

/// <summary>The folders that may hold ffmpeg / ffprobe inside the project (the first one is where the downloader writes).</summary>
public sealed class ToolDirectories
{
    public ToolDirectories(IHostEnvironment env, IOptions<MediaOptions> opt)
    {
        var configured = string.IsNullOrWhiteSpace(opt.Value.ToolsDirectory) ? "tools" : opt.Value.ToolsDirectory.Trim();
        Primary = Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured));
        // A published build runs from its own folder; the tools folder is copied next to it by the build.
        Alternates = new[] { Path.Combine(AppContext.BaseDirectory, "tools") }
            .Select(Path.GetFullPath).Where(d => !string.Equals(d, Primary, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public ToolDirectories(string primary) { Primary = Path.GetFullPath(primary); Alternates = Array.Empty<string>(); }

    public string Primary { get; }
    public IReadOnlyList<string> Alternates { get; }
    public IEnumerable<string> All => new[] { Primary }.Concat(Alternates);
    public static ToolDirectories Default() => new(Path.Combine(AppContext.BaseDirectory, "tools"));
}

/// <summary>
/// Finds ffmpeg / ffprobe. A path configured by the administrator is used exclusively. Otherwise: the project's tools folder first
/// (so copying the project folder to a new computer carries the tools along), then next to the other tool, then PATH, then the usual install folders.
/// </summary>
public static class ToolLocator
{
    public static string ExeName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    public static ToolInfo? Find(string name, string? configured, ToolDirectories? dirs = null, IEnumerable<string?>? siblingHints = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var v = Run(configured.Trim());
            return v == null ? null : new ToolInfo(configured.Trim(), v);
        }
        foreach (var candidate in Candidates(name, dirs, siblingHints))
        {
            var version = Run(candidate);
            if (version != null) return new ToolInfo(candidate, version);
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string name, ToolDirectories? dirs, IEnumerable<string?>? siblingHints)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> Gen()
        {
            var exe = ExeName(name);
            foreach (var d in (dirs ?? ToolDirectories.Default()).All)
            {
                if (!Directory.Exists(d)) continue;
                yield return Path.Combine(d, exe);
                yield return Path.Combine(d, "bin", exe);                         // an extracted FFmpeg build copied in as-is
                string[] subs;
                try { subs = Directory.GetDirectories(d); } catch (Exception) { subs = Array.Empty<string>(); }
                foreach (var sub in subs) yield return Path.Combine(sub, "bin", exe);
            }

            foreach (var hint in siblingHints ?? Array.Empty<string?>())
            {
                if (string.IsNullOrWhiteSpace(hint)) continue;
                var dir = Path.GetDirectoryName(hint.Trim());
                if (!string.IsNullOrEmpty(dir)) yield return Path.Combine(dir, exe);
            }

            yield return name; // PATH

            if (OperatingSystem.IsWindows())
            {
                var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                var programData = Environment.GetEnvironmentVariable("ProgramData");
                var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
                var profile = Environment.GetEnvironmentVariable("USERPROFILE");
                if (!string.IsNullOrEmpty(local)) yield return Path.Combine(local, "Microsoft", "WinGet", "Links", exe);      // winget shim
                if (!string.IsNullOrEmpty(programData)) yield return Path.Combine(programData, "chocolatey", "bin", exe);      // Chocolatey
                if (!string.IsNullOrEmpty(programFiles)) yield return Path.Combine(programFiles, "ffmpeg", "bin", exe);
                yield return Path.Combine(@"C:\ffmpeg\bin", exe);
                if (!string.IsNullOrEmpty(profile)) yield return Path.Combine(profile, "scoop", "shims", exe);                 // Scoop
            }
            else if (OperatingSystem.IsMacOS())
            {
                yield return "/opt/homebrew/bin/" + name;
                yield return "/usr/local/bin/" + name;
            }
        }
        foreach (var c in Gen()) if (seen.Add(c)) yield return c;
    }

    /// <summary>Runs "&lt;command&gt; -version"; returns its first output line, or null when the command cannot be run.</summary>
    public static string? Run(string command)
    {
        try
        {
            var psi = new ProcessStartInfo(command)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi);
            if (p == null) return null;
            var first = p.StandardOutput.ReadLineAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(8000)) { try { p.Kill(true); } catch { /* gone */ } return null; }
            return p.ExitCode == 0 ? (first.Wait(2000) ? first.Result : null) ?? "(version unknown)" : null;
        }
        catch (Exception)
        {
            return null; // not installed / not executable
        }
    }
}
