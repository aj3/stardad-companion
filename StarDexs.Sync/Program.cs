using System.Security.Principal;
using StarDexs.Sync.Core;

namespace StarDexs.Sync;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // "--grant <LIVE folder> <user SID>": the one administrator step, started (with Windows' own prompt) by
        // the tray app after the player agrees. It changes permissions on the places the file goes and exits.
        if (args is ["--grant", var live, var sid])
        {
            try
            {
                FolderAccess.Grant(live, new SecurityIdentifier(sid));
                return 0;
            }
            catch (Exception exception)
            {
                Dialogs.Tell($"Couldn't change the game folder's permissions: {exception.Message}", warning: true);
                return 1;
            }
        }

        var syncNow = SyncLink.IsSyncRequest(args);
        var account = SyncLink.Account(args);
        using var single = new Mutex(initiallyOwned: true, @"Local\StarDexsSync", out var first);
        if (!first)
        {
            // Already running: pass a "Send to my PC" press on to it.
            if (syncNow) TrayApp.AskRunningCopyToSync(account);
            else Dialogs.Tell("StarDexs Sync is already running. Look for its icon by the clock.");
            return 0;
        }
        return new TrayApp(syncNow, account).Run();
    }
}
