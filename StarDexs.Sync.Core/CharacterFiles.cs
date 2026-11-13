using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace StarDexs.Sync.Core;

/// <summary>
/// Custom characters: the game's .chf files in LIVE\user\client\0\CustomCharacters. Read to back them up; written
/// only to add a backed-up character that is missing, never to replace or delete one already there.
/// </summary>
public static class CharacterFiles
{
    public const int MaxBytes = 64 * 1024, MaxCharacters = 1000;
    private static readonly Regex NamePattern = new(@"\A(?![. ])[^\\/:*?""<>|\x00-\x1f]{1,120}\.chf\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Folder(string live) => Path.Combine(live, "USER", "Client", "0", "CustomCharacters");
    public static bool ValidName(string? name) => name is not null && NamePattern.IsMatch(name) && !name.EndsWith(' ') && !name.Contains("..");
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static void Validate(string name, byte[] data)
    {
        if (!ValidName(name) || data.Length is 0 or > MaxBytes) throw new InvalidDataException("Only custom character files (.chf) of up to 64 KiB are backed up.");
    }

    /// <summary>The character files in the folder, by name; none when the folder doesn't exist. Links and odd names are left out.</summary>
    public static (IReadOnlyList<string> Names, int Skipped) List(string live)
    {
        var folder = CheckedFolder(live);
        if (!Directory.Exists(folder)) return ([], 0);
        var names = new List<string>(); var skipped = 0;
        foreach (var path in Directory.EnumerateFiles(folder, "*.chf", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            var info = new FileInfo(path);
            if (!ValidName(name) || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length is 0 or > MaxBytes) { skipped++; continue; }
            names.Add(name);
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        if (names.Count > MaxCharacters) { skipped += names.Count - MaxCharacters; names = names.Take(MaxCharacters).ToList(); }
        return (names, skipped);
    }

    public static byte[] Read(string live, string name)
    {
        var path = Target(live, name);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int n;
        while ((n = file.Read(buffer)) > 0) { if (output.Length + n > MaxBytes) throw new InvalidDataException("A character file is larger than 64 KiB."); output.Write(buffer, 0, n); }
        var data = output.ToArray(); Validate(name, data); return data;
    }

    /// <summary>
    /// Adds one backed-up character if no file of that name is there. False when one already is: it is left alone.
    /// Written beside its final name and moved in without overwriting, then checked against its fingerprint.
    /// </summary>
    public static bool AddMissing(string live, string name, byte[] data, string sha256, Func<bool> gameRunning)
    {
        Validate(name, data);
        if (Hash(data) != sha256) throw new InvalidDataException("A downloaded character failed its integrity check.");
        if (gameRunning()) throw new IOException("Close the game before restoring characters.");
        var target = Target(live, name);
        if (File.Exists(target)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        CheckedFolder(live);
        var temporary = target + ".stardad-tmp"; var created = false;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { created = true; file.Write(data); file.Flush(true); }
            try { File.Move(temporary, target, overwrite: false); created = false; }
            catch (IOException) when (File.Exists(target)) { return false; }
        }
        finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
        if (Hash(Read(live, name)) != sha256) throw new IOException($"{name} didn't verify after it was written.");
        return true;
    }

    private static string Target(string live, string name)
    {
        if (!ValidName(name)) throw new InvalidDataException("That isn't a custom character file name.");
        var folder = CheckedFolder(live);
        var target = Path.GetFullPath(Path.Combine(folder, name));
        if (!string.Equals(Path.GetDirectoryName(target), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("That isn't a custom character file name.");
        if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("A character file is a link, so it was left alone.");
        return target;
    }

    // The game's LIVE folder, and no folder on the way to CustomCharacters may be a link.
    private static string CheckedFolder(string live)
    {
        if (!GameFolder.IsLive(live)) throw new InvalidDataException("Choose your game's LIVE folder first.");
        var folder = Path.GetFullPath(Folder(live));
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("The custom characters folder is a link. Choose the installation's real folder instead.");
        return folder;
    }
}
