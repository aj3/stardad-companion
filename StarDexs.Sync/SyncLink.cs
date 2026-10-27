using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace StarDexs.Sync;

/// <summary>
/// The <c>stardexs-sync:</c> link behind stardexs.com's "Send to my PC" button (<c>sync/</c>) and its
/// "Save schedule" button (<c>schedule/</c>). Registered for the current player only. The only part read is
/// an optional account id (16 hex characters) that picks which account already linked on this PC; anything
/// else is ignored. Any page can open the link, so all it can ever do is start the same sync with stardexs.com
/// that the tray menu's "Sync" does, or ask stardexs.com for the schedule the player saved there.
/// </summary>
internal static partial class SyncLink
{
    public const string Scheme = "stardexs-sync";
    private const string Key = @"Software\Classes\" + Scheme;

    [GeneratedRegex(@"^stardexs-sync://(?:sync|schedule)/(?<account>[0-9a-f]{16})/?$", RegexOptions.CultureInvariant)]
    private static partial Regex WithAccount();

    public static bool IsScheduleRequest(string[] args) =>
        args.Any(arg => arg.StartsWith(Scheme + "://schedule/", StringComparison.OrdinalIgnoreCase));

    public static bool IsSyncRequest(string[] args) =>
        !IsScheduleRequest(args) && args.Any(arg => arg == "--sync" || arg.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase));

    /// <summary>The account id in the link, or null (then the last account used).</summary>
    public static string? Account(string[] args) =>
        args.Select(arg => WithAccount().Match(arg)).FirstOrDefault(match => match.Success)?.Groups["account"].Value;

    /// <summary>An account id fits in one number, which is how it's handed to an app that's already running.</summary>
    public static nuint ToNumber(string? account) => account is null ? 0 : (nuint)Convert.ToUInt64(account, 16);
    public static string? FromNumber(nuint number) => number == 0 ? null : ((ulong)number).ToString("x16");

    /// <summary>Points the link at this copy of the app (run at every start, so a moved app keeps working).</summary>
    public static void Register()
    {
        using var root = Registry.CurrentUser.CreateSubKey(Key);
        root.SetValue("", "URL:StarDexs Companion");
        root.SetValue("URL Protocol", "");
        using var icon = root.CreateSubKey("DefaultIcon");
        icon.SetValue("", $"\"{Environment.ProcessPath}\",0");
        using var command = root.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"\"{Environment.ProcessPath}\" \"%1\"");
    }

    public static void Unregister() => Registry.CurrentUser.DeleteSubKeyTree(Key, throwOnMissingSubKey: false);
}
