using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace StarDexs.Sync.Core;

/// <summary>Why a write didn't happen.</summary>
public enum WriteOutcome { Written, AlreadyCurrent, GameRunning, NeedsPermission, UnknownFile, Failed }

/// <param name="Sha256">The hash of the global.ini now in place, when there is one.</param>
public sealed record WriteResult(WriteOutcome Outcome, string Message, string? Sha256 = null);

/// <summary>
/// The only code that writes into the game folder, and only two files (see <see cref="GameFolder.CheckWritable"/>):
/// global.ini under data\Localization\english and user.cfg's two language lines. Every write waits for the
/// game to be closed, writes a temporary file beside global.ini and swaps it in, and adds a line to a short
/// journal. Backups live on stardexs.com, not on the PC. A global.ini StarDexs Companion didn't put there is
/// never replaced without the player's say-so, and a file it can't write is reported, never forced.
/// </summary>
public sealed class InstallWriter(string dataFolder, Func<bool>? gameRunning = null, TimeProvider? clock = null)
{
    /// <summary>Far above any real global.ini; anything bigger isn't one.</summary>
    public const int MaxFileBytes = 64 * 1024 * 1024;
    public const int JournalLinesKept = 200;
    private readonly Func<bool> _gameRunning = gameRunning ?? GameIsRunning;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string JournalPath => Path.Combine(dataFolder, "journal.log");

    /// <summary>The game itself (not the launcher) is running.</summary>
    public static bool GameIsRunning()
    {
        var running = Process.GetProcessesByName("StarCitizen");
        foreach (var process in running) process.Dispose();
        return running.Length > 0;
    }

    public static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <param name="ours">The hash of the global.ini StarDexs Companion last installed, if any.</param>
    /// <param name="replaceUnknown">The player agreed to replace a global.ini that isn't <paramref name="ours"/>.</param>
    public WriteResult Install(string live, byte[] globalIni, string patch, string? ours, bool replaceUnknown = false)
    {
        if (!GameFolder.IsLive(live)) return new(WriteOutcome.Failed, "That folder isn't the game's LIVE folder (it has no Data.p4k).");
        if (!LooksLikeGlobalIni(globalIni)) return new(WriteOutcome.Failed, "The file from stardexs.com didn't look like game text, so it wasn't installed.");
        if (_gameRunning()) return new(WriteOutcome.GameRunning, "Waiting for the game to close.");
        var iniPath = GameFolder.GlobalIniPath(live);
        var cfgPath = GameFolder.UserCfgPath(live);
        var sha = Sha256(globalIni);
        try
        {
            var current = File.Exists(iniPath) ? Sha256(File.ReadAllBytes(iniPath)) : null;
            if (current is not null && current != sha && current != ours && !replaceUnknown)
                return new(WriteOutcome.UnknownFile, "There's a global.ini in the game folder that StarDexs Companion didn't put there.");
            var oldCfg = File.Exists(cfgPath) ? File.ReadAllText(cfgPath, Encoding.UTF8) : null;
            var newCfg = UserCfg.WithLanguage(oldCfg);
            if (current == sha && newCfg is null) return new(WriteOutcome.AlreadyCurrent, $"Your file for {patch} is already in place.", sha);

            if (current != sha)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iniPath)!);
                ReplaceAtomically(live, iniPath, globalIni);
            }
            if (newCfg is not null) WriteInPlace(live, cfgPath, Encoding.UTF8.GetBytes(newCfg));
        }
        catch (OutsideGameFilesException exception) { return new(WriteOutcome.Failed, exception.Message); }
        catch (UnauthorizedAccessException) { return new(WriteOutcome.NeedsPermission, "Windows won't let StarDexs Companion write to the game folder yet."); }
        catch (IOException exception) { return new(WriteOutcome.Failed, $"Couldn't write the file: {exception.Message}"); }
        Journal($"installed {patch} sha256={sha} into {live}");
        return new(WriteOutcome.Written, $"Installed your file for {patch}. Restart the game to see it.", sha);
    }

    /// <summary>
    /// Takes out the global.ini StarDexs Companion installed, and only that one. user.cfg's language lines stay:
    /// english is the game's default anyway.
    /// </summary>
    public WriteResult Remove(string live, string? ours)
    {
        if (_gameRunning()) return new(WriteOutcome.GameRunning, "Close the game first.");
        var iniPath = GameFolder.GlobalIniPath(live);
        if (!File.Exists(iniPath)) return new(WriteOutcome.AlreadyCurrent, "There's no StarDexs file in the game folder.");
        try
        {
            if (Sha256(File.ReadAllBytes(iniPath)) != ours)
                return new(WriteOutcome.UnknownFile, "The global.ini in the game folder isn't the one StarDexs Companion installed, so it was left alone.");
            GameFolder.CheckWritable(live, iniPath);
            File.Delete(iniPath);
        }
        catch (OutsideGameFilesException exception) { return new(WriteOutcome.Failed, exception.Message); }
        catch (UnauthorizedAccessException) { return new(WriteOutcome.NeedsPermission, "Windows won't let StarDexs Companion change the game folder yet."); }
        catch (IOException exception) { return new(WriteOutcome.Failed, $"Couldn't remove the file: {exception.Message}"); }
        Journal($"removed sha256={ours} from {live}");
        return new(WriteOutcome.Written, "Took your StarDexs file out of the game. Restart the game to see its own text again.");
    }

    /// <summary>
    /// Whatever stardexs.com sends must be what the game reads: UTF-8 text of "key=value" lines, nothing
    /// binary. So even a compromised server could only ever put text in the one file.
    /// </summary>
    public static bool LooksLikeGlobalIni(byte[] content)
    {
        if (content.Length is 0 or > MaxFileBytes || content.AsSpan().Contains((byte)0)) return false;
        string text;
        try { text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(content); }
        catch (DecoderFallbackException) { return false; }
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            var trimmed = line.TrimStart('﻿').Trim();
            if (trimmed.IsEmpty || trimmed[0] == ';') continue;
            if (trimmed.IndexOf('=') <= 0) return false;
        }
        return true;
    }

    // A temporary file in the same folder, flushed, then swapped in: the game never sees half a file.
    private static void ReplaceAtomically(string live, string target, byte[] content)
    {
        var temporary = GameFolder.GlobalIniTempPath(live);
        GameFolder.CheckWritable(live, target);
        GameFolder.CheckWritable(live, temporary);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        if (File.Exists(target)) File.Replace(temporary, target, destinationBackupFileName: null);
        else File.Move(temporary, target);
    }

    // user.cfg is a few lines; it's rewritten where it is, so nothing new is ever created in LIVE itself.
    private static void WriteInPlace(string live, string target, byte[] content)
    {
        GameFolder.CheckWritable(live, target);
        using var stream = new FileStream(target, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        stream.SetLength(0);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    // A short record of what was written, newest last; only the last few hundred lines are kept.
    private void Journal(string line)
    {
        Directory.CreateDirectory(dataFolder);
        var lines = File.Exists(JournalPath) ? File.ReadAllLines(JournalPath).TakeLast(JournalLinesKept - 1) : [];
        File.WriteAllLines(JournalPath, [.. lines, $"{_clock.GetUtcNow():O} {line}"]);
    }
}
