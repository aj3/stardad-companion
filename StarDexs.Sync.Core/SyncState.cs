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

/// <summary>One stardexs.com account linked on this PC. A PC can hold several (players who switch accounts).</summary>
public sealed class LinkedAccount
{
    /// <summary>The device token, protected with <see cref="ISecretProtector"/>.</summary>
    public string ProtectedToken { get; set; } = "";
    /// <summary>The account's name as stardexs.com shows it, for the menu.</summary>
    public string Name { get; set; } = "";
    /// <summary>How far each log has been read for this account, by file name.</summary>
    public Dictionary<string, long> LogOffsets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Where the helper is up to: who it's linked to, what it read and installed, what the player agreed to.</summary>
public sealed class SyncState
{
    /// <summary>2 since accounts became a list. Nothing earlier was released, so an older file just starts fresh.</summary>
    public const int CurrentVersion = 2;
    public int Version { get; set; } = CurrentVersion;
    /// <summary>Linked accounts by their public id (not secret: it only picks which token to use).</summary>
    public Dictionary<string, LinkedAccount> Accounts { get; set; } = new(StringComparer.Ordinal);
    /// <summary>The account the last sync was for; "Sync now" in the tray uses it.</summary>
    public string? LastAccount { get; set; }
    public string? GameFolder { get; set; }
    /// <summary>The player agreed to let StarDexs Companion install files into <see cref="GameFolder"/>.</summary>
    public bool InstallAgreed { get; set; }
    /// <summary>What's in the game folder now: which account's file, for which patch and settings, and its hash.</summary>
    public string? InstalledAccount { get; set; }
    public string? InstalledPatch { get; set; }
    public string? InstalledSettings { get; set; }
    public string? InstalledSha { get; set; }
    /// <summary>The player said once to replace a global.ini StarDexs Companion didn't put there.</summary>
    public bool ReplaceUnknownOnce { get; set; }
    /// <summary>The game build already told about, so a game update is announced once.</summary>
    public string? NoticedBuild { get; set; }
    public DateTimeOffset? LastSync { get; set; }
    public string? LastMessage { get; set; }
}

/// <summary>Reads and writes <see cref="SyncState"/> as JSON in the app's own folder; a damaged or older file starts fresh.</summary>
public sealed class StateStore(string folder, ISecretProtector protector)
{
    public string Path => System.IO.Path.Combine(folder, "state.json");

    public SyncState Load()
    {
        try
        {
            if (File.Exists(Path) && JsonSerializer.Deserialize(File.ReadAllText(Path), StateJson.Default.SyncState) is { Version: SyncState.CurrentVersion } state)
            {
                state.Accounts = new Dictionary<string, LinkedAccount>(state.Accounts, StringComparer.Ordinal);
                foreach (var account in state.Accounts.Values)
                    account.LogOffsets = new Dictionary<string, long>(account.LogOffsets, StringComparer.OrdinalIgnoreCase);
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
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, StateJson.Default.SyncState));
        File.Move(temporary, Path, overwrite: true);
    }

    public string? Token(SyncState state, string? account) =>
        account is not null && state.Accounts.TryGetValue(account, out var linked) ? protector.Unprotect(linked.ProtectedToken) : null;

    public void Link(SyncState state, string account, string name, string token)
    {
        var linked = state.Accounts.TryGetValue(account, out var known) ? known : new LinkedAccount();
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
