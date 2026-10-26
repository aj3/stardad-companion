using System.Text.RegularExpressions;

namespace StarDexs.Sync.Core;

/// <summary>
/// Finding the game's LIVE folder: from the RSI Launcher's own log (which names the install it launches),
/// then the usual places on every fixed drive. The player always confirms the folder before anything is
/// written, and can pick another.
/// </summary>
public static partial class GameFolder
{
    public const string LocalizationFolder = @"data\Localization\english";
    public const string GlobalIni = "global.ini";
    public const string UserCfgName = "user.cfg";

    [GeneratedRegex(@"[A-Za-z]:\\[^""'<>|*?\r\n]*?\\StarCitizen\\LIVE", RegexOptions.IgnoreCase)]
    private static partial Regex LivePath();

    /// <summary>A folder is the game's LIVE folder when it holds Data.p4k.</summary>
    public static bool IsLive(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, "Data.p4k"));

    public static string GlobalIniPath(string live) => Path.Combine(live, LocalizationFolder, GlobalIni);
    public static string UserCfgPath(string live) => Path.Combine(live, UserCfgName);
    /// <summary>The temporary file a new global.ini is written to before it's swapped in (same folder).</summary>
    public static string GlobalIniTempPath(string live) => GlobalIniPath(live) + ".stardexs-tmp";

    /// <summary>
    /// The only files StarDexs Companion may write: global.ini (and its temporary file) in the english language
    /// folder, and user.cfg. Anything else is refused, and so is a language folder that's really a link to
    /// somewhere else on the PC.
    /// </summary>
    public static void CheckWritable(string live, string path)
    {
        var full = Path.GetFullPath(path);
        var allowed = new[] { GlobalIniPath(live), GlobalIniTempPath(live), UserCfgPath(live) }.Select(Path.GetFullPath);
        if (!allowed.Contains(full, StringComparer.OrdinalIgnoreCase))
            throw new OutsideGameFilesException($"StarDexs Companion only writes global.ini and user.cfg, not {Path.GetFileName(full)}.");
        if (!full.Equals(Path.GetFullPath(UserCfgPath(live)), StringComparison.OrdinalIgnoreCase))
        {
            for (var folder = new DirectoryInfo(Path.GetDirectoryName(full)!);
                 folder is not null && !folder.FullName.Equals(Path.GetFullPath(live).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                 folder = folder.Parent)
                if (folder.Exists && folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new OutsideGameFilesException($"{folder.FullName} points somewhere else on this PC, so StarDexs Companion won't write there.");
        }
    }

    public static string ManifestPath(string live) => Path.Combine(live, "build_manifest.id");

    /// <summary>
    /// The game's build number from build_manifest.id (its RequestedP4ChangeNum), or null. Read on this PC
    /// only, so a game update can be noticed without asking anything online.
    /// </summary>
    public static string? Build(string live)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ManifestPath(live)));
            return document.RootElement.TryGetProperty("Data", out var data)
                   && data.TryGetProperty("RequestedP4ChangeNum", out var change)
                   && change.GetString() is { Length: > 0 and <= 20 } build && build.All(char.IsAsciiDigit) ? build : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>The build number stardexs.com puts at the end of a patch name, like 12913470 in "4.10.2-live.12913470".</summary>
    public static string? PatchBuild(string? patch) =>
        patch?[(patch.LastIndexOf('.') + 1)..] is { Length: > 0 } build && build.All(char.IsAsciiDigit) ? build : null;

    /// <summary>The only files StarDexs Companion reads: Game.log, the logs in logbackups, and build_manifest.id.</summary>
    public static bool IsReadableLog(string live, string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Equals(Path.GetFullPath(Path.Combine(live, "Game.log")), StringComparison.OrdinalIgnoreCase)) return true;
        if (full.Equals(Path.GetFullPath(ManifestPath(live)), StringComparison.OrdinalIgnoreCase)) return true;
        return Path.GetDirectoryName(full)!.Equals(Path.GetFullPath(Path.Combine(live, "logbackups")), StringComparison.OrdinalIgnoreCase)
               && full.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>LIVE folders named in launcher log text, newest mention first.</summary>
    public static IEnumerable<string> FromLauncherLog(string text) =>
        LivePath().Matches(text).Select(m => m.Value).Reverse().Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The LIVE folder for whatever the player picked: LIVE itself, or StarCitizen, or Roberts Space Industries
    /// (or RSI) above it. Null when there's no Data.p4k in any of those.
    /// </summary>
    public static string? ResolveLive(string? picked, Func<string, bool>? isLive = null)
    {
        isLive ??= IsLive;
        if (string.IsNullOrWhiteSpace(picked)) return null;
        string[][] below = [[], ["LIVE"], ["StarCitizen", "LIVE"], ["Roberts Space Industries", "StarCitizen", "LIVE"], ["RSI", "StarCitizen", "LIVE"]];
        return below.Select(parts => Path.Combine([picked, .. parts])).FirstOrDefault(isLive);
    }

    /// <summary>The first LIVE folder that exists: the launcher's log first, then common places.</summary>
    public static string? Find(Func<string, bool>? isLive = null)
    {
        isLive ??= IsLive;
        var launcherLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rsilauncher", "logs", "log.log");
        var candidates = new List<string>();
        try
        {
            if (File.Exists(launcherLog))
            {
                using var stream = new FileStream(launcherLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                candidates.AddRange(FromLauncherLog(reader.ReadToEnd()));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var root = drive.RootDirectory.FullName;
            candidates.Add(Path.Combine(root, @"Program Files\Roberts Space Industries\StarCitizen\LIVE"));
            candidates.Add(Path.Combine(root, @"Roberts Space Industries\StarCitizen\LIVE"));
            candidates.Add(Path.Combine(root, @"RSI\StarCitizen\LIVE"));
            candidates.Add(Path.Combine(root, @"StarCitizen\LIVE"));
            candidates.Add(Path.Combine(root, @"Games\StarCitizen\LIVE"));
        }
        return candidates.FirstOrDefault(isLive);
    }
}

/// <summary>A write StarDexs Companion refuses to make: not one of its two files, or a folder that leads elsewhere.</summary>
public sealed class OutsideGameFilesException(string message) : UnauthorizedAccessException(message);
