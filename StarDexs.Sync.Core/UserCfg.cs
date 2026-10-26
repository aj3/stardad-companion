using System.Text.RegularExpressions;

namespace StarDexs.Sync.Core;

/// <summary>
/// The two lines StarDexs needs in the game's user.cfg. Only these lines are added or changed; every other
/// line, comment and blank stays exactly as the player wrote it.
/// </summary>
public static partial class UserCfg
{
    public static readonly (string Name, string Value)[] Required = [("g_language", "english"), ("g_languageAudio", "english")];

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z_][A-Za-z0-9_.]*)\s*=", RegexOptions.CultureInvariant)]
    private static partial Regex Setting();

    /// <summary>The file's text with the required lines set; null when it already has them.</summary>
    public static string? WithLanguage(string? current)
    {
        var newline = current is not null && current.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = (current ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var changed = false;
        foreach (var (name, value) in Required)
        {
            var wanted = $"{name} = {value}";
            var at = lines.FindIndex(line => Setting().Match(line) is { Success: true } m && m.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                lines.Add(wanted);
                changed = true;
            }
            else if (!Value(lines[at]).Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                lines[at] = wanted;
                changed = true;
            }
        }
        return changed ? string.Join(newline, lines) + newline : null;
    }

    // The value after "=", without a trailing ";" or "--" comment.
    private static string Value(string line)
    {
        var value = line[(line.IndexOf('=') + 1)..];
        foreach (var marker in new[] { ";", "--" })
        {
            var at = value.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0) value = value[..at];
        }
        return value.Trim();
    }
}
