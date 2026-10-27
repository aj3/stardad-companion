using System.Security.Principal;
using Avalonia;
using Avalonia.Controls;
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

        // "--render-preview <png>": draws the panel (with sample text) into an image and exits. For checking a
        // published build's look without opening anything on screen; it reads and writes nothing else.
        if (args is ["--render-preview", var image])
        {
            AppBuilder.Configure(() => new CompanionApp(null)).UseWin32().UseSkia().SetupWithoutStarting();
            PanelPreview.Save(image);
            return 0;
        }

        // Run by the uninstaller: stop starting with Windows.
        if (args is [AutoStart.Forget])
        {
            AutoStart.Set(false);
            return 0;
        }

        var syncNow = SyncLink.IsSyncRequest(args);
        var schedule = SyncLink.IsScheduleRequest(args);
        var account = SyncLink.Account(args);
        // Started by an update: the old copy is closing, so wait for it (a few seconds at most), then tidy up after it.
        var updated = args.Contains("--updated");
        using var single = new Mutex(initiallyOwned: false, @"Local\StarDexsSync");
        bool first;
        try { first = single.WaitOne(updated ? TimeSpan.FromSeconds(15) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { first = true; }
        if (updated && Environment.ProcessPath is { } self) Updater.TidyUp(self);
        if (!first)
        {
            // Already running: pass a "Send to my PC" or "Save schedule" press on to it.
            if (syncNow) TrayApp.AskRunningCopyToSync(account);
            else if (schedule) TrayApp.AskRunningCopyForSchedule(account);
            else if (args.Contains(AutoStart.Background)) { }
            else Dialogs.Tell("StarDexs Companion is already running. Look for its icon by the clock.");
            return 0;
        }
        // Avalonia runs the message loop (for the tray icon too) until Quit; only its Windows backend is used.
        var tray = new TrayApp(syncNow, account, schedule, args.Contains(AutoStart.Background));
        return AppBuilder.Configure(() => new CompanionApp(tray))
            .UseWin32()
            .UseSkia()
            .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
    }
}
