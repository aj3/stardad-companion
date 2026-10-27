namespace StarDexs.Sync.Core;

public static class SyncNotifications
{
    public static bool ShouldNotify(string mode, SyncReport report) => mode == "all" || (mode == "attention" && !SyncEngine.Succeeded(report));
}
