using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StarDexs.Sync.Core;

/// <summary>A blueprint the player received, and when the log first showed it.</summary>
public sealed record Blueprint(string Name, DateTimeOffset FirstSeen);

/// <summary>What one pass over the logs found: blueprints by name, completed contracts by mission id.</summary>
public sealed class LogFindings
{
    public Dictionary<string, Blueprint> Blueprints { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Completed { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsEmpty => Blueprints.Count == 0 && Completed.Count == 0;

    public void Merge(LogFindings other)
    {
        foreach (var (name, found) in other.Blueprints) AddBlueprint(found);
        foreach (var (id, title) in other.Completed) Completed[id] = title;
    }

    public void AddBlueprint(Blueprint found)
    {
        if (!Blueprints.TryGetValue(found.Name, out var known) || found.FirstSeen < known.FirstSeen) Blueprints[found.Name] = found;
    }
}

/// <summary>
/// Reads the game's logs for the two things StarDexs uses, and nothing else: the HUD's "Received
/// Blueprint" notices (from 1 March 2026, when blueprints arrived) and "Contract Complete" notices with
/// their mission id. The same rules as the website's in-browser reader. Log text never leaves the PC.
/// </summary>
public static partial class LogReader
{
    public const int MaxNameLength = 160, MaxTitleLength = 200;
    private const int MaxLineBytes = 1024 * 1024;
    private static readonly DateTimeOffset Epoch = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [GeneratedRegex("""^<(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z)>.*?<SHUDEvent_OnNotification> Added notification "Received Blueprint: (.*?): " \[""")]
    private static partial Regex BlueprintLine();

    [GeneratedRegex("""Added notification "Contract Complete:\s*(.*?):\s*" \[\d+\].*?MissionId: \[([0-9a-fA-F-]{36})\]""")]
    private static partial Regex CompletedLine();

    /// <summary>Looks at one line; adds what it finds.</summary>
    public static void ReadLine(string line, LogFindings into)
    {
        if (line.Contains("Contract Complete", StringComparison.Ordinal) && CompletedLine().Match(line) is { Success: true } done)
        {
            var title = done.Groups[1].Value.Trim();
            if (title.Length is > 0 and <= MaxTitleLength) into.Completed[done.Groups[2].Value.ToLowerInvariant()] = title;
        }
        if (!line.Contains("Received Blueprint:", StringComparison.Ordinal) || BlueprintLine().Match(line) is not { Success: true } found) return;
        if (!DateTimeOffset.TryParse(found.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) || when < Epoch) return;
        var name = found.Groups[2].Value.Trim();
        if (name.Length is 0 or > MaxNameLength) return;
        into.AddBlueprint(new Blueprint(name, when.ToUniversalTime()));
    }

    /// <summary>
    /// Reads a log from <paramref name="offset"/> to its current end and returns the new offset. A partial
    /// last line is left for next time. If the file got shorter (the game started a new log), it starts over.
    /// </summary>
    public static long ReadFrom(string path, long offset, LogFindings into, Action? checkpoint = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset > stream.Length) offset = 0;
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        var pending = new MemoryStream();
        long consumed = offset;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            checkpoint?.Invoke();
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                pending.Write(buffer, start, i - start);
                if (pending.Length <= MaxLineBytes) ReadLine(Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length).TrimEnd('\r'), into);
                consumed += pending.Length + 1;
                pending.SetLength(0);
                start = i + 1;
            }
            if (start < read) pending.Write(buffer, start, read - start);
            if (pending.Length > MaxLineBytes)
            {
                consumed += pending.Length;
                pending.SetLength(0);
            }
        }
        return consumed;
    }
}
