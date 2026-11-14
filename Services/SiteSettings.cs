using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamApp.Services;

public enum LogoMode { LogoAndName, LogoOnly, NameOnly }

/// <summary>What one visitor is shown: the hide settings with the signed-in-administrator exception applied.</summary>
public readonly record struct VisitorView(bool HideTechnical, bool HideHints, bool HideCompat)
{
    public string BodyClasses => string.Join(' ', new[] { HideTechnical ? "hide-tech" : null, HideHints ? "hide-hints" : null, HideCompat ? "hide-compat" : null }.Where(x => x != null));
}

/// <summary>
/// Everything an administrator can change about how the site looks and what it says. Stored as one small JSON file (see <see cref="SiteSettingsService"/>);
/// every value is checked and clamped by <see cref="Normalize"/> before it is used or saved, so a bad value can never break a page.
/// </summary>
public sealed class SiteSettings
{
    public const string DefaultName = "StreamApp";
    public const string DefaultAccent = "#f2a93b", DefaultAccentLight = "#ffc766", DefaultBackground = "#0a0c10", DefaultPanel = "#11141b", DefaultText = "#eceef3";
    public const string DefaultMark = "▶";
    public const string DefaultFooter = "Movies are streamed directly from the configured local servers.";

    // ---- visitor view: what visitors do not get to see
    /// <summary>Playback status line ("Streaming directly…", "Compatibility mode: the server is re-encoding…"), technical error details and messages about what the server is doing.</summary>
    public bool HideTechnicalDetails { get; set; }
    /// <summary>Explanations and tips: how to load subtitles, what the timing controls do, why a menu is empty, "check Dashboard…" notes.</summary>
    public bool HideHints { get; set; }
    /// <summary>The compatibility-mode switch and button. Playback still switches to compatibility mode by itself when a device needs it.</summary>
    public bool HideCompatibilityControls { get; set; }
    /// <summary>Administrators who are signed in keep seeing everything, whatever is hidden for visitors.</summary>
    public bool ShowAllToSignedInAdmin { get; set; }

    // ---- identity
    public string Name { get; set; } = DefaultName;
    public string Tagline { get; set; } = "";
    public LogoMode LogoMode { get; set; } = LogoMode.LogoAndName;
    public int LogoHeight { get; set; } = 32;
    /// <summary>The symbol in the small coloured square used when there is no logo (empty = no square).</summary>
    public string BrandMark { get; set; } = DefaultMark;

    // ---- colours (#rrggbb)
    public string Accent { get; set; } = DefaultAccent;
    public string AccentLight { get; set; } = DefaultAccentLight;
    public string Background { get; set; } = DefaultBackground;
    public string Panel { get; set; } = DefaultPanel;
    public string Text { get; set; } = DefaultText;

    // ---- wording
    public string HeroLabel { get; set; } = "Featured";
    public string SearchPlaceholder { get; set; } = "Search movies";
    /// <summary>Empty = the default sentence.</summary>
    public string FooterText { get; set; } = "";
    public bool ShowTmdbCredit { get; set; } = true;
    public bool ShowAdminLink { get; set; } = true;
    /// <summary>A line shown in a bar at the top of every page (maintenance notice, welcome message). Empty = no bar.</summary>
    public string Announcement { get; set; } = "";

    // ---- advanced
    public string CustomCss { get; set; } = "";

    // ---- uploaded images (managed by the service, not by the form)
    public string? LogoFile { get; set; }
    public string? FaviconFile { get; set; }
    public long Version { get; set; }

    private static readonly Regex Hex = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    /// <summary>Trims, limits lengths, and replaces anything invalid with the default. Returns this instance.</summary>
    public SiteSettings Normalize()
    {
        string Clean(string? v, int max, string fallback = "") { v = (v ?? "").Trim().Replace("\r", " ").Replace("\n", " "); return v.Length == 0 ? fallback : v.Length > max ? v[..max].TrimEnd() : v; }
        string Color(string? v, string fallback) => v != null && Hex.IsMatch(v.Trim()) ? v.Trim().ToLowerInvariant() : fallback;

        Name = Clean(Name, 60, DefaultName);
        Tagline = Clean(Tagline, 200);
        BrandMark = Clean(BrandMark, 3);
        HeroLabel = Clean(HeroLabel, 40);
        SearchPlaceholder = Clean(SearchPlaceholder, 60, "Search movies");
        FooterText = Clean(FooterText, 300);
        Announcement = Clean(Announcement, 300);
        LogoHeight = Math.Clamp(LogoHeight, 16, 80);
        if (!Enum.IsDefined(LogoMode)) LogoMode = LogoMode.LogoAndName;
        Accent = Color(Accent, DefaultAccent); AccentLight = Color(AccentLight, DefaultAccentLight);
        Background = Color(Background, DefaultBackground); Panel = Color(Panel, DefaultPanel); Text = Color(Text, DefaultText);
        var css = (CustomCss ?? "").Replace("\r\n", "\n");
        css = css.Replace("</", "<\\/").Replace("<!--", "");   // can never close the <style> element it is placed in
        CustomCss = css.Length > 20000 ? css[..20000] : css;
        return this;
    }

    /// <summary>What a visitor sees. A signed-in administrator sees everything when <see cref="ShowAllToSignedInAdmin"/> is on.</summary>
    public VisitorView ViewFor(bool signedInAdmin) => signedInAdmin && ShowAllToSignedInAdmin
        ? new VisitorView(false, false, false)
        : new VisitorView(HideTechnicalDetails, HideHints, HideCompatibilityControls);

    public SiteSettings Clone() => (SiteSettings)MemberwiseClone();

    // ---- derived values used by the layout

    [System.Text.Json.Serialization.JsonIgnore] public bool HasLogo => LogoFile != null;
    [System.Text.Json.Serialization.JsonIgnore] public bool HasFavicon => FaviconFile != null || LogoFile != null;
    [System.Text.Json.Serialization.JsonIgnore] public string DisplayFooter => string.IsNullOrEmpty(FooterText) ? DefaultFooter : FooterText;

    private static (int R, int G, int B) Rgb(string hex) => (int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber), int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber), int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber));
    private static string Mix(string a, string b, double t)
    {
        var (r1, g1, b1) = Rgb(a); var (r2, g2, b2) = Rgb(b);
        int M(int x, int y) => (int)Math.Round(x + (y - x) * t);
        return $"#{M(r1, r2):x2}{M(g1, g2):x2}{M(b1, b2):x2}";
    }
    /// <summary>Perceived brightness 0 (black) to 1 (white).</summary>
    public static double Luma(string hex) { var (r, g, b) = Rgb(hex); return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0; }

    /// <summary>The CSS custom properties for the chosen colours (the rest of the stylesheet only uses these). Empty when nothing differs from the default theme.</summary>
    public string ThemeCss()
    {
        var (br, bg, bb) = Rgb(Background);
        var onAccent = Luma(Accent) > 0.55 ? "#1a1100" : "#ffffff";
        var sb = new StringBuilder(":root{");
        sb.Append($"--bg:{Background};--bg-rgb:{br},{bg},{bb};--bg-2:{Panel};--bg-3:{Mix(Panel, Text, 0.08)};--line:{Mix(Panel, Text, 0.16)};");
        sb.Append($"--text:{Text};--muted:{Mix(Text, Background, 0.42)};--accent:{Accent};--accent-2:{AccentLight};--on-accent:{onAccent};");
        sb.Append($"--logo-h:{LogoHeight}px;");
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>The favicon when none was uploaded: a rounded square in the accent colour with the brand mark.</summary>
    public string DefaultFaviconSvg()
    {
        var mark = System.Net.WebUtility.HtmlEncode(BrandMark.Length > 0 ? BrandMark : Name[..1].ToUpperInvariant());
        return $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 64 64'><rect width='64' height='64' rx='14' fill='{Accent}'/><text x='32' y='44' font-size='34' text-anchor='middle' font-family='Segoe UI,Arial,sans-serif' fill='{(Luma(Accent) > 0.55 ? "#1a1100" : "#ffffff")}'>{mark}</text></svg>";
    }
}

/// <summary>The kinds of image an administrator can upload.</summary>
public enum BrandImage { Logo, Favicon }

public sealed record BrandAsset(byte[] Bytes, string ContentType);

/// <summary>
/// Keeps the site settings in <c>site.json</c> and the uploaded logo/favicon next to it, in one folder (Site:Directory, default "site-data" inside the project).
/// A file is used on purpose: the database is not touched, so an existing installation keeps working after an update, and backing up or moving the site
/// is copying two things (the database and this folder). The current settings are held in memory; a page reads them without touching the disk.
/// </summary>
public sealed class SiteSettingsService
{
    public const long MaxLogoBytes = 2 * 1024 * 1024, MaxFaviconBytes = 512 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private readonly string _dir;
    private readonly ILogger<SiteSettingsService> _log;
    private readonly object _gate = new();
    private volatile SiteSettings _current;

    public SiteSettingsService(string directory, string? defaultName, ILogger<SiteSettingsService> log)
    {
        _dir = Path.GetFullPath(directory);
        _log = log;
        _current = Load(defaultName);
    }

    public string DirectoryPath => _dir;
    /// <summary>The settings in force. Treat as read-only; change them through <see cref="Update"/>.</summary>
    public SiteSettings Current => _current;

    private string SettingsPath => Path.Combine(_dir, "site.json");

    private SiteSettings Load(string? defaultName)
    {
        var s = new SiteSettings();
        if (!string.IsNullOrWhiteSpace(defaultName)) s.Name = defaultName;
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<SiteSettings>(File.ReadAllText(SettingsPath), Json);
                if (loaded != null) s = loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not read {Path} ({Message}); the default site settings are used.", SettingsPath, ex.Message);
        }
        s.Normalize();
        // an image that was deleted by hand must not leave a broken logo behind
        if (s.LogoFile != null && !File.Exists(Path.Combine(_dir, s.LogoFile))) s.LogoFile = null;
        if (s.FaviconFile != null && !File.Exists(Path.Combine(_dir, s.FaviconFile))) s.FaviconFile = null;
        return s;
    }

    private void Save(SiteSettings s)
    {
        System.IO.Directory.CreateDirectory(_dir);
        s.Version = DateTime.UtcNow.Ticks;
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
        File.Move(tmp, SettingsPath, overwrite: true);   // never leaves a half-written file behind
        _current = s;
    }

    /// <summary>Replaces the form-controlled values; the uploaded images stay as they are.</summary>
    public void Update(SiteSettings edited)
    {
        lock (_gate)
        {
            var next = edited.Clone();
            next.LogoFile = _current.LogoFile; next.FaviconFile = _current.FaviconFile;
            Save(next.Normalize());
        }
    }

    /// <summary>Changes one setting through <paramref name="change"/> and saves (used by the one-click buttons).</summary>
    public void Change(Action<SiteSettings> change)
    {
        lock (_gate) { var next = _current.Clone(); change(next); Save(next.Normalize()); }
    }

    /// <summary>Back to the default look and wording. Uploaded images are removed too.</summary>
    public void Reset(string? defaultName)
    {
        lock (_gate)
        {
            DeleteImage(_current.LogoFile); DeleteImage(_current.FaviconFile);
            var s = new SiteSettings(); if (!string.IsNullOrWhiteSpace(defaultName)) s.Name = defaultName;
            Save(s.Normalize());
        }
    }

    // ------------------------------------------------------------------ images

    /// <summary>Works out what an uploaded file really is from its first bytes (the file name and the browser's claim are not trusted). Null = not an allowed image.</summary>
    public static (string Extension, string ContentType)? Sniff(ReadOnlySpan<byte> b, bool allowIco)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return (".png", "image/png");
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return (".jpg", "image/jpeg");
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8') return (".gif", "image/gif");
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return (".webp", "image/webp");
        if (allowIco && b.Length >= 6 && b[0] == 0 && b[1] == 0 && b[2] == 1 && b[3] == 0) return (".ico", "image/x-icon");
        var head = Encoding.UTF8.GetString(b[..Math.Min(b.Length, 4096)]).TrimStart('﻿', ' ', '\t', '\r', '\n');
        if ((head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)) && head.Contains("<svg", StringComparison.OrdinalIgnoreCase))
        {
            // Shown only through <img> (scripts do not run there) and served with a sandboxing header, but active content is refused as well.
            var all = Encoding.UTF8.GetString(b).ToLowerInvariant();
            if (all.Contains("<script") || all.Contains("<foreignobject") || all.Contains("javascript:") || Regex.IsMatch(all, @"\son[a-z]+\s*=")) return null;
            return (".svg", "image/svg+xml");
        }
        return null;
    }

    /// <summary>Stores an uploaded logo or favicon. Returns null when it worked, otherwise a sentence for the administrator.</summary>
    public string? SetImage(BrandImage kind, byte[] bytes)
    {
        var max = kind == BrandImage.Logo ? MaxLogoBytes : MaxFaviconBytes;
        if (bytes.Length == 0) return "That file is empty.";
        if (bytes.Length > max) return $"That image is too large ({bytes.Length / 1024} KB). The {(kind == BrandImage.Logo ? "logo" : "favicon")} limit is {max / 1024} KB.";
        var type = Sniff(bytes, allowIco: kind == BrandImage.Favicon);
        if (type == null) return "That is not a usable image. Use PNG, JPG, GIF, WebP or SVG" + (kind == BrandImage.Favicon ? " (or ICO)" : "") + ".";
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(_dir);
            var name = (kind == BrandImage.Logo ? "logo" : "favicon") + type.Value.Extension;
            var old = kind == BrandImage.Logo ? _current.LogoFile : _current.FaviconFile;
            File.WriteAllBytes(Path.Combine(_dir, name), bytes);
            if (old != null && old != name) DeleteImage(old);
            var next = _current.Clone();
            if (kind == BrandImage.Logo) next.LogoFile = name; else next.FaviconFile = name;
            Save(next);
        }
        return null;
    }

    public void RemoveImage(BrandImage kind)
    {
        lock (_gate)
        {
            var next = _current.Clone();
            if (kind == BrandImage.Logo) { DeleteImage(next.LogoFile); next.LogoFile = null; } else { DeleteImage(next.FaviconFile); next.FaviconFile = null; }
            Save(next);
        }
    }

    private void DeleteImage(string? name)
    {
        if (name == null) return;
        try { File.Delete(Path.Combine(_dir, Path.GetFileName(name))); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The uploaded image, or null when there is none.</summary>
    public BrandAsset? ReadImage(BrandImage kind)
    {
        var name = kind == BrandImage.Logo ? _current.LogoFile : _current.FaviconFile ?? _current.LogoFile;
        if (name == null) return null;
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(_dir, Path.GetFileName(name)));
            var type = Sniff(bytes, allowIco: true);
            return type == null ? null : new BrandAsset(bytes, type.Value.ContentType);
        }
        catch (IOException) { return null; }
    }
}
