using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StarDexs.Sync.Core;

/// <summary>Keeps the device token readable only by this Windows account on this PC.</summary>
public interface ISecretProtector
{
    string Protect(string secret);
    string? Unprotect(string protectedSecret);
}

/// <summary>Windows DPAPI, scoped to the current user.</summary>
public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("StarDexs.Sync.device-token");

    public string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    public string? Unprotect(string protectedSecret)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception exception) when (exception is CryptographicException or FormatException) { return null; }
    }
}

/// <summary>One stardad.space account linked on this PC. A PC can hold several (players who switch accounts).</summary>
public sealed class LinkedAccount
{
    public PendingSyncResult? PendingResult { get; set; }
    /// <summary>The device token, protected with <see cref="ISecretProtector"/>.</summary>
    public string ProtectedToken { get; set; } = "";
    /// <summary>The account's name as stardad.space shows it, for the menu.</summary>
    public string Name { get; set; } = "";
    /// <summary>The automatic syncs the player chose for this PC on the StarDad website; null: none.</summary>
    public SyncSchedule? Schedule { get; set; }
    /// <summary>When that schedule arrived; the first timed sync comes after it.</summary>
    public DateTimeOffset? ScheduleSince { get; set; }
    /// <summary>This account's last sync, whoever started it; timed syncs count from it.</summary>
    public DateTimeOffset? LastSync { get; set; }
}

/// <summary>
/// What one log held, kept so a log is read once: the next sync reads it again only if its size or time
/// changed. Every sync still sends everything found, so the account gets back anything removed there.
/// </summary>
public sealed class LogFile
{
    public long Size { get; set; }
    public DateTime Written { get; set; }
    public List<Blueprint> Blueprints { get; set; } = [];
    public Dictionary<string, string> Completed { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static LogFile From(FileInfo file, LogFindings found) => new()
    {
        Size = file.Length, Written = file.LastWriteTimeUtc,
        Blueprints = [.. found.Blueprints.Values], Completed = new(found.Completed, StringComparer.OrdinalIgnoreCase),
    };

    public bool Matches(FileInfo file) => Size == file.Length && Written == file.LastWriteTimeUtc;

    public void AddTo(LogFindings findings)
    {
        foreach (var blueprint in Blueprints) findings.AddBlueprint(blueprint);
        foreach (var (id, title) in Completed) findings.Completed[id] = title;
    }
}

/// <summary>Where the helper is up to: who it's linked to, what it read and installed, what the player agreed to.</summary>
public sealed class SyncState
{
    public PendingInstall? PendingInstall { get; set; }
    /// <summary>Silent by default; optional notifications never affect website reporting.</summary>
    public string SyncNotifications { get; set; } = "silent";
    /// <summary>
    /// 3 since logs are kept as what they held (not how far they were read). A version 2 file carries over:
    /// its read positions are dropped and every log is read once more. Anything older starts fresh.
    /// </summary>
    public const int CurrentVersion = 3;
    public int Version { get; set; } = CurrentVersion;
    /// <summary>Linked accounts by their public id (not secret: it only picks which token to use).</summary>
    public Dictionary<string, LinkedAccount> Accounts { get; set; } = new(StringComparer.Ordinal);
    /// <summary>The account the last sync was for; "Sync now" in the tray uses it.</summary>
    public string? LastAccount { get; set; }
    public string? GameFolder { get; set; }
    /// <summary>The player agreed to let StarDad Companion install files into <see cref="GameFolder"/>.</summary>
    public bool InstallAgreed { get; set; }
    /// <summary>What's in the game folder now: which account's file, for which patch and settings, and its hash.</summary>
    public string? InstalledAccount { get; set; }
    public string? InstalledPatch { get; set; }
    public string? InstalledSettings { get; set; }
    public string? InstalledSha { get; set; }
    /// <summary>The player said once to replace a global.ini StarDad Companion didn't put there.</summary>
    public bool ReplaceUnknownOnce { get; set; }
    /// <summary>The game build already told about, so a game update is announced once.</summary>
    public string? NoticedBuild { get; set; }
    public DateTimeOffset? LastSync { get; set; }
    public string? LastMessage { get; set; }
    /// <summary>What each log in the game folder held, by file name.</summary>
    public Dictionary<string, LogFile> Logs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record PendingSyncResult(string RunId, string Message, bool Ok, string Stage = "finished", string? Patch = null, string? Sha256 = null);
public sealed record PendingInstall(string Account, string Folder, string Patch, string Settings, string Sha256, byte[] Data, string RunId);

/// <summary>Reads and writes <see cref="SyncState"/> as JSON in the app's own folder; a damaged or older file starts fresh.</summary>
public sealed class StateStore(string folder, ISecretProtector protector)
{
    public string Path => System.IO.Path.Combine(folder, "state.json");

    public SyncState Load()
    {
        try
        {
            if (File.Exists(Path) && JsonSerializer.Deserialize(File.ReadAllText(Path), StateJson.Default.SyncState) is { Version: 2 or SyncState.CurrentVersion } state)
            {
                state.Version = SyncState.CurrentVersion;
                state.Accounts = new Dictionary<string, LinkedAccount>(state.Accounts, StringComparer.Ordinal);
                state.Logs = new Dictionary<string, LogFile>(state.Logs, StringComparer.OrdinalIgnoreCase);
                foreach (var log in state.Logs.Values) log.Completed = new Dictionary<string, string>(log.Completed, StringComparer.OrdinalIgnoreCase);
                return state;
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException) { }
        return new SyncState();
    }

    public void Save(SyncState state)
    {
        Directory.CreateDirectory(folder);
        var temporary = Path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, state, StateJson.Default.SyncState);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, Path, overwrite: true);
    }

    public string? Token(SyncState state, string? account) =>
        account is not null && state.Accounts.TryGetValue(account, out var linked) ? protector.Unprotect(linked.ProtectedToken) : null;

    public void Link(SyncState state, string account, string name, string token)
    {
        var linked = state.Accounts.TryGetValue(account, out var known) ? known
            : new LinkedAccount { Schedule = SyncSchedule.Default, ScheduleSince = DateTimeOffset.UtcNow };
        linked.ProtectedToken = protector.Protect(token);
        linked.Name = name;
        state.Accounts[account] = linked;
        state.LastAccount = account;
    }

    public void Unlink(SyncState state, string account)
    {
        state.Accounts.Remove(account);
        if (state.LastAccount == account) state.LastAccount = state.Accounts.Keys.FirstOrDefault();
    }
}

[JsonSerializable(typeof(SyncState))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class StateJson : JsonSerializerContext;
