using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using StarDexs.Sync.Core;
using static StarDexs.Sync.NativeMethods;

namespace StarDexs.Sync;

/// <summary>
/// The tray icon and everything behind its menu. A sync happens when the player presses "Send to my PC" in
/// their stardexs.com account (the stardexs-sync: link) or "Sync" in the panel, or at the times they chose
/// for this PC in their account (off unless they turn it on), and only for an account linked on this PC.
/// Watching for a game update reads build_manifest.id on this PC; nothing goes online for it.
/// The tray icon is plain Win32 on the UI thread (Avalonia runs the message loop); its panel is themed like
/// the website. Syncing runs on the thread pool, and anything that asks the player a question runs on its own
/// short-lived thread so the icon never freezes. One lock keeps the state file to one writer at a time.
/// </summary>
internal sealed class TrayApp : IPanelActions
{
    private const uint WM_TRAY = WM_APP + 1;
    private const string ClassName = "StarDexsSyncTray", SyncNowMessage = "StarDexsSync.SyncNow", ScheduleMessage = "StarDexsSync.Schedule";
    /// <summary>How often to look (locally, without going online) whether the game has closed.</summary>
    private static readonly TimeSpan GameCheck = TimeSpan.FromSeconds(15);
    private static TrayApp? _app;

    private readonly bool _syncNow, _refreshSchedule, _background;
    private readonly string? _account;
    private readonly string _version = typeof(TrayApp).Assembly.GetName().Version?.ToString(3) ?? "0";
    private readonly string _dataFolder = DataFolder();
    private readonly StateStore _store;
    private readonly SyncApi _api;
    private readonly SyncEngine _engine;
    private readonly Updater _updater;
    /// <summary>A newer signed release, once a sync or "Check for updates" found one.</summary>
    private Release? _release;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _quit = new();
    private TrayPanel? _panel;
    private FileSystemWatcher? _manifest;
    /// <summary>Looks once a minute (on this PC) whether a sync the player scheduled is due.</summary>
    /// <summary>Made in Start: a DispatcherTimer made earlier (before Avalonia's Windows backend is set up) stops the app starting.</summary>
    private DispatcherTimer? _clock;
    private uint _taskbarCreated, _syncNowMessage, _scheduleMessage;
    private nint _window, _icon;
    private string _status = "Ready.";
    private bool _needsAccess, _toldAccess, _toldUpdate, _busy;
    private int _asking, _waitingForGame;
    // The sync bar: how it looks, how far it has got, and the last result.
    private SyncBar.Look _look = SyncBar.Look.Idle;
    private double _share;
    private string? _lastSync;

    public TrayApp(bool syncNow, string? account, bool refreshSchedule = false, bool background = false)
    {
        _syncNow = syncNow;
        _account = account;
        _refreshSchedule = refreshSchedule;
        _background = background;
        _store = new StateStore(_dataFolder, new DpapiProtector());
        _api = new SyncApi(SyncApi.CreateClient(Site(), _version));
        _engine = new SyncEngine(_api, _store, new InstallWriter(_dataFolder));
        _updater = new Updater(SyncApi.CreateClient(Site(), _version));
    }

    /// <summary>
    /// %LOCALAPPDATA%\StarDad\Companion: linked accounts, the game folder, what the player agreed to. A copy kept
    /// under the app's old name (StarDexs) moves here the first time; if it can't move, it's used where it is.
    /// </summary>
    private static string DataFolder()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string folder = Path.Combine(local, "StarDad", "Companion"), old = Path.Combine(local, "StarDexs", "Companion");
        if (Directory.Exists(folder) || !Directory.Exists(old)) return folder;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
            Directory.Move(old, folder);
            if (Directory.Exists(Path.GetDirectoryName(old)!) && !Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(old)!).Any())
                Directory.Delete(Path.GetDirectoryName(old)!);
            return folder;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return old; }
    }

    // Release builds only ever talk to stardexs.com. Debug builds can point at a local copy of the site.
    private static Uri Site()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("STARDEXS_SITE") is { Length: > 0 } site) return new Uri(site);
#endif
        return SyncApi.DefaultSite;
    }

    /// <summary>Sets up the tray icon and the panel; Avalonia's message loop does the rest.</summary>
    public unsafe void Start()
    {
        _app = this;
        _panel = new TrayPanel(this);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _syncNowMessage = RegisterWindowMessageW(SyncNowMessage);
        _scheduleMessage = RegisterWindowMessageW(ScheduleMessage);
        try { SyncLink.Register(); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        var instance = GetModuleHandleW(null);
        fixed (char* className = ClassName)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &WindowProc, hInstance = instance, lpszClassName = className,
            };
            if (RegisterClassExW(&windowClass) == 0) return;
        }
        // A window that's never shown: it only receives the icon's clicks and the menu's commands.
        _window = CreateWindowExW(0, ClassName, "StarDad Companion", 0, 0, 0, 0, 0, 0, 0, instance, 0);
        if (_window == 0) return;
        _icon = LoadImageW(instance, IDI_APPLICATION, IMAGE_ICON, GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), 0);
        if (_icon == 0) _icon = LoadImageW(0, IDI_APPLICATION, IMAGE_ICON, 0, 0, LR_SHARED | LR_DEFAULTSIZE);
        AddIcon();

        var state = _store.Load();
        if (state.Accounts.Count == 0 || !GameFolder.IsLive(state.GameFolder)) Ask(Setup);
        else if (_syncNow) _ = SyncAsync(_account, manual: true);
        else if (_refreshSchedule) _ = RefreshScheduleAsync(_account);
        else if (!_background) Balloon("StarDad Companion is ready", "Press \"Send to my PC\" in your StarDad account when you want your file.");
        WatchForGameUpdates();
        ApplyAutoStart();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _clock.Tick += (_, _) => _ = RunDueAsync();
        _clock.Start();
    }

    /// <summary>Called by a second copy started from the website's link: hand the request (and its account) to this one.</summary>
    public static void AskRunningCopyToSync(string? account)
    {
        var window = FindWindowW(ClassName, null);
        if (window != 0) PostMessageW(window, RegisterWindowMessageW(SyncNowMessage), SyncLink.ToNumber(account), 0);
    }

    /// <summary>Called by a second copy started from the website's "Save schedule": this one asks for the new schedule.</summary>
    public static void AskRunningCopyForSchedule(string? account)
    {
        var window = FindWindowW(ClassName, null);
        if (window != 0) PostMessageW(window, RegisterWindowMessageW(ScheduleMessage), SyncLink.ToNumber(account), 0);
    }

    // ---- automatic syncs, at the times the player chose on the StarDad website --------------------------------------

    /// <summary>Syncs each account whose scheduled time has come, one after another.</summary>
    private async Task RunDueAsync()
    {
        if (_busy || !GameFolder.IsLive(_store.Load().GameFolder)) return;
        foreach (var account in _engine.DueAccounts(DateTimeOffset.UtcNow)) await SyncAsync(account, manual: false);
    }

    /// <summary>Asks stardexs.com for the schedule the player just saved for this PC; nothing is read or installed.</summary>
    private async Task RefreshScheduleAsync(string? account)
    {
        // The page opens the link as it saves; give the save a moment to land first.
        try { await Task.Delay(TimeSpan.FromSeconds(3), _quit.Token); }
        catch (OperationCanceledException) { return; }
        var (schedule, problem) = await Task.Run(() => _engine.RefreshScheduleAsync(account, _quit.Token));
        Post(() =>
        {
            ApplyAutoStart();
            _status = problem ?? (schedule is null ? "Automatic sync is off." : $"Automatic sync: {schedule.Describe()}.");
            Refresh();
            Balloon("StarDad Companion", problem ?? (schedule is null
                ? "Automatic sync is off. Syncs happen when you ask."
                : $"Automatic sync: {schedule.Describe()}. StarDad Companion starts with Windows so it can."), warning: problem is not null);
        });
    }

    /// <summary>Starts with Windows only while some linked account has an automatic sync on.</summary>
    private void ApplyAutoStart() => AutoStart.Set(_store.Load().Accounts.Values.Any(account => account.Schedule is { IsOn: true }));

    // ---- noticing a game update, on this PC only ------------------------------------------------------------

    private void WatchForGameUpdates()
    {
        _manifest?.Dispose();
        _manifest = null;
        CheckGameUpdate();
        if (_store.Load().GameFolder is not { } live || !GameFolder.IsLive(live)) return;
        _manifest = new FileSystemWatcher(live, Path.GetFileName(GameFolder.ManifestPath(live)))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        _manifest.Changed += (_, _) => Post(CheckGameUpdate);
        _manifest.Created += (_, _) => Post(CheckGameUpdate);
        _manifest.EnableRaisingEvents = true;
    }

    private async void CheckGameUpdate()
    {
        if (_engine.GameUpdated() is null) return;
        var accounts = _engine.OnGameUpdateAccounts();
        _status = "The game updated; your file is for the last patch.";
        UpdateTip();
        if (accounts.Count == 0)
        {
            Balloon("The game updated", "Your StarDad file is for the last patch. Open your StarDad account and press \"Send to my PC\" when the new one is ready.");
            return;
        }
        // The player asked for a sync when the game updates. If stardexs.com hasn't built files for the new
        // patch yet, this sends the logs and says so; a timed sync or "Send to my PC" fetches the file later.
        foreach (var account in accounts) await SyncAsync(account, manual: false);
    }

    // ---- syncing -------------------------------------------------------------------------------------------

    /// <summary>
    /// The player asked for their file while the game was open. Wait for it to close (checking this PC only,
    /// nothing online), then finish what they asked for.
    /// </summary>
    private async Task FinishAfterGameClosesAsync(string? account)
    {
        if (Interlocked.Exchange(ref _waitingForGame, 1) == 1) return;
        try
        {
            while (InstallWriter.GameIsRunning()) await Task.Delay(GameCheck, _quit.Token);
            await SyncAsync(account, manual: true);
        }
        catch (OperationCanceledException) { }
        finally { Volatile.Write(ref _waitingForGame, 0); }
    }

    private async Task SyncAsync(string? account, bool manual)
    {
        var stages = new StageReporter(stage => Post(() => Stage(stage)));
        await RunAsync(() => _engine.RunAsync(account, _quit.Token, stages), report => Show(report, account, manual));
    }

    private void Stage(SyncStage stage)
    {
        if (stage == SyncStage.Finished) return;
        _look = SyncBar.Look.Running;
        _share = Math.Max(0.06, stage.Share());
        _status = stage switch
        {
            SyncStage.Reading => "Reading your game logs…",
            SyncStage.Sending => "Sending blueprints and contracts…",
            SyncStage.Checking => "Checking your file…",
            SyncStage.Downloading => "Downloading your file…",
            _ => "Installing your file…",
        };
        Refresh();
    }

    private async Task RunAsync(Func<Task<SyncReport>> work, Action<SyncReport> show)
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            _busy = true;
            _look = SyncBar.Look.Running;
            _share = 0.04;
            SetStatus("Working…");
            var report = await Task.Run(work);
            Post(() => show(report));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Post(() => Finish(false, $"Something went wrong: {exception.Message}")); }
        finally
        {
            _gate.Release();
            Post(() => { _busy = false; Refresh(); });
        }
    }

    /// <summary>The bar lands on the target, or stops short, and the result stays until the next sync.</summary>
    private void Finish(bool ok, string message)
    {
        _look = ok ? SyncBar.Look.Hit : SyncBar.Look.Missed;
        _share = ok ? 1 : _share;
        _status = message;
        _lastSync = "Last synced " + DateTime.Now.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
        Refresh();
    }

    private void Show(SyncReport report, string? account, bool manual)
    {
        _needsAccess = report.NeedsPermission;
        ApplyAutoStart(); // the sync brought this PC's schedule along
        Finish(SyncEngine.Succeeded(report), report.Message);
        if (report.Installed) Balloon("Your StarDad file is installed", report.Message);
        else if (report.WaitingForGame)
        {
            Balloon("StarDad Companion", "Your file goes in as soon as you close the game.");
            _ = FinishAfterGameClosesAsync(account);
        }
        else if (report.UnknownFile) Ask(() => ReplaceUnknown(account));
        else if (report.NotLinked && account is not null && manual) Ask(() => { if (Link(account)) Post(() => _ = SyncAsync(account, manual: true)); });
        else if (report.NeedsPermission && manual) Ask(AllowAccess);
        else if (report.NeedsPermission && !_toldAccess)
        {
            _toldAccess = true;
            Balloon("StarDad Companion needs your permission", "Right-click its icon and choose \"Allow access to the game folder\".", warning: true);
        }
        else if (manual)
        {
            var sent = report.NewBlueprints + report.NewCompleted > 0
                ? $" Added {report.NewBlueprints} blueprints and {report.NewCompleted} finished contracts to your account."
                : "";
            Balloon("StarDad Companion", report.Message + sent);
        }
        if (!_toldUpdate && report.HelperLatest is { } latest && Version.TryParse(latest, out var newest)
            && Version.TryParse(_version, out var mine) && newest > mine)
        {
            _toldUpdate = true;
            _ = FindUpdateAsync(quiet: true);
        }
    }

    // ---- questions for the player (each on its own thread, one at a time) ----------------------------------

    private void Ask(Action questions)
    {
        if (Interlocked.Exchange(ref _asking, 1) == 1) return;
        var thread = new Thread(() =>
        {
            try { questions(); }
            catch (Exception exception) { SetStatus($"Something went wrong: {exception.Message}"); }
            finally { Volatile.Write(ref _asking, 0); }
        }) { IsBackground = true, Name = "StarDad questions" };
        thread.SetApartmentState(ApartmentState.STA); // the folder picker needs it
        thread.Start();
    }

    private void Setup()
    {
        var firstRun = _store.Load().Accounts.Count == 0;
        if (firstRun && !Link(_account)) return;
        if (!GameFolder.IsLive(_store.Load().GameFolder) && !ChooseFolder(GameFolder.Find())) return;
        if (firstRun) AllowInstalling();
        Post(() => { WatchForGameUpdates(); _ = SyncAsync(_account, manual: true); });
    }

    /// <param name="expected">The account the website asked for, if any; linking a different one is allowed but said.</param>
    private bool Link(string? expected)
    {
        var device = Environment.MachineName;
        var intro = expected is not null && _store.Load().Accounts.Count > 0
            ? "You pressed \"Send to my PC\" in a StarDad account this PC isn't linked to yet. Link it now?\n\n"
            : "Link this PC to your StarDad account.\n\n";
        if (!Dialogs.Confirm(intro +
                "Your browser will open a page with a code on it. Sign in to the account there and approve the code.\n\n" +
                $"This PC will be listed on the account as \"{device}\". You can link more than one account."))
            return false;
        LinkStart start;
        try { start = _api.StartLinkAsync(device, _quit.Token).GetAwaiter().GetResult(); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Dialogs.Tell("Couldn't reach the StarDad website. Check your connection, then choose \"Link an account\" from the tray menu.", warning: true);
            return false;
        }
        Open(start.LinkPage);
        SetStatus($"Waiting for you to approve code {start.Code} on the StarDad website…");
        while (!_quit.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(3)))
        {
            LinkReady? ready;
            try { ready = _api.PollLinkAsync(start.Poll, _quit.Token).GetAwaiter().GetResult(); }
            catch (TimeoutException)
            {
                SetStatus("Not linked.");
                Dialogs.Tell("The code ran out before it was approved. Choose \"Link an account\" from the tray menu to try again.", warning: true);
                return false;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { continue; }
            if (ready is null) continue;
            Update(state => _store.Link(state, ready.Account, ready.Name, ready.Token));
            SetStatus($"Linked to {ready.Name}.");
            if (expected is not null && ready.Account != expected)
                Dialogs.Tell($"Linked to {ready.Name}, which isn't the account you pressed \"Send to my PC\" in. Press it again in the account you want.");
            return ready.Account == expected || expected is null;
        }
        return false;
    }

    /// <summary>
    /// The game folder: the one found on this PC if the player confirms it, otherwise one they pick (any folder
    /// from Roberts Space Industries down to LIVE; LIVE is found inside it). Not for the main thread.
    /// </summary>
    private bool ChooseFolder(string? found)
    {
        var current = _store.Load().GameFolder;
        string? folder = null;
        if (found is not null && !string.Equals(found, current, StringComparison.OrdinalIgnoreCase))
            folder = Dialogs.Ask($"Found your game here:\n\n{found}\n\nUse this folder?") ? found : null;
        else if (found is not null && !Dialogs.Ask($"StarDad Companion is using your game at:\n\n{found}\n\nPick a different folder?"))
            return false;
        while (folder is null)
        {
            var picked = PickFolder();
            if (picked is null) return false;
            folder = GameFolder.ResolveLive(picked);
            if (folder is null)
                Dialogs.Tell("There's no game in that folder. Pick your StarCitizen folder (or the LIVE folder inside it).", warning: true);
        }
        Update(state =>
        {
            if (string.Equals(state.GameFolder, folder, StringComparison.OrdinalIgnoreCase)) return;
            // A different install: its logs and files start from scratch.
            state.GameFolder = folder;
            state.InstalledAccount = state.InstalledPatch = state.InstalledSettings = state.InstalledSha = state.NoticedBuild = null;
            state.Logs.Clear();
        });
        return true;
    }

    private void AllowInstalling()
    {
        var agreed = Dialogs.Ask(
            "Can StarDad Companion put your StarDad file into the game folder?\n\n" +
            "It changes only:\n" +
            "  •  data\\Localization\\english\\global.ini (your file)\n" +
            "  •  the language lines in user.cfg\n\n" +
            "It waits while the game is running. your StarDad account keeps your last two files, so you can put the " +
            "previous one back, or take the file out, from the tray menu at any time.\n\n" +
            "Choose No to only send your logs. You can allow this later from the tray menu.");
        Update(state => state.InstallAgreed = agreed);
    }

    private void ReplaceUnknown(string? account)
    {
        if (!Dialogs.Ask(
                "There's a global.ini in your game folder that StarDad Companion didn't put there " +
                "(maybe from another tool, or changed by hand).\n\n" +
                "Replace it with your StarDad file? The old one isn't kept."))
        {
            SetStatus("Left the other global.ini in place.");
            return;
        }
        Update(state => state.ReplaceUnknownOnce = true);
        Post(() => _ = SyncAsync(account, manual: true));
    }

    private void AllowAccess()
    {
        var live = _store.Load().GameFolder;
        if (!GameFolder.IsLive(live)) return;
        if (!Dialogs.Confirm(
                "Windows doesn't let StarDad Companion write to your game folder yet.\n\n" +
                "Next, Windows asks for administrator permission once. StarDad Companion then lets your Windows account change only:\n" +
                "  •  the data\\Localization\\english folder (where your file goes)\n" +
                "  •  user.cfg (the language lines)\n\n" +
                "Nothing else in the game folder changes."))
            return;
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = $"--grant \"{Path.TrimEndingDirectorySeparator(live!)}\" {WindowsIdentity.GetCurrent().User!.Value}",
        };
        try
        {
            using var grant = Process.Start(start);
            grant?.WaitForExit();
            if (grant?.ExitCode != 0) return;
        }
        catch (Win32Exception) { return; } // the player said no to Windows' prompt
        Post(() => _ = SyncAsync(null, manual: true));
    }

    private void PutBackPrevious()
    {
        if (!Dialogs.Confirm("Put back the file you had installed before this one? your StarDad account keeps it for you.\n\nThe next \"Send to my PC\" installs your current file again."))
            return;
        Post(() => _ = RunAsync(() => _engine.PutBackPreviousAsync(_quit.Token), report => Show(report, null, manual: true)));
    }

    private void RemoveFile()
    {
        if (!Dialogs.Confirm("Take your StarDad file out of the game? The game shows its own text again.\n\nOnly the global.ini StarDad Companion installed is removed. \"Send to my PC\" puts it back."))
            return;
        Post(() => _ = RunAsync(() => Task.FromResult(_engine.Remove()), report => Show(report, null, manual: true)));
    }

    private void UnlinkAccount(string account)
    {
        var name = _store.Load().Accounts.TryGetValue(account, out var linked) ? linked.Name : "this account";
        if (!Dialogs.Ask($"Unlink {name} from this PC?\n\n\"Send to my PC\" in that account won't reach this PC until you link it again. Your installed file stays as it is."))
            return;
        SetStatus($"Unlinking {name}…");
        _gate.Wait();
        string said;
        try { said = _engine.UnlinkAsync(account, _quit.Token).GetAwaiter().GetResult(); }
        finally { _gate.Release(); }
        Post(() =>
        {
            ApplyAutoStart();
            _status = said;
            _look = SyncBar.Look.Idle;
            _share = 0;
            Refresh();
            Balloon("StarDad Companion", said);
        });
    }

    /// <summary>Changes the saved state while no sync is running. Not for the main thread.</summary>
    private void Update(Action<SyncState> change)
    {
        _gate.Wait();
        try
        {
            var state = _store.Load();
            change(state);
            _store.Save(state);
        }
        finally { _gate.Release(); }
    }

    // ---- the panel -------------------------------------------------------------------------------------------

    private PanelView View()
    {
        var state = _store.Load();
        var accounts = state.Accounts.OrderBy(a => a.Value.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => (a.Key, a.Value.Name, a.Value.Schedule is { IsOn: true } schedule ? "Auto: " + schedule.Describe() : (string?)null)).ToList();
        return new PanelView(_status, _look, _share, _lastSync, accounts, GameFolder.IsLive(state.GameFolder),
            state.InstallAgreed, _needsAccess, state.InstalledSha is not null, _busy, _release?.Version.ToString(3));
    }

    private unsafe void ShowPanel()
    {
        POINT point;
        GetCursorPos(&point);
        _panel?.ShowNear(new PixelPoint(point.X, point.Y), View());
    }

    /// <summary>The tooltip and, if it's open, the panel, after anything changes.</summary>
    private void Refresh()
    {
        UpdateTip();
        if (_panel is { IsVisible: true }) _panel.Show(View());
    }

    // What the panel's buttons do.
    void IPanelActions.Sync(string account) => _ = SyncAsync(account, manual: true);
    void IPanelActions.Unlink(string account) => Ask(() => UnlinkAccount(account));
    void IPanelActions.Link()
    {
        if (_store.Load().Accounts.Count == 0) Ask(Setup);
        else Ask(() => Link(null));
    }
    void IPanelActions.AllowInstalling() => Ask(() => { AllowInstalling(); Post(() => _ = SyncAsync(null, manual: true)); });
    void IPanelActions.AllowAccess() => Ask(AllowAccess);
    void IPanelActions.OpenSite() => Open(SyncApi.DefaultSite);
    void IPanelActions.ChangeFolder() => Ask(() => { if (ChooseFolder(GameFolder.Find())) Post(WatchForGameUpdates); });

    /// <summary>Windows' own (modern) folder picker, opened from the UI thread; waits here for the answer.</summary>
    private string? PickFolder()
    {
        if (_panel is null) return null;
        var picked = Dispatcher.UIThread.InvokeAsync(() => _panel.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Choose your StarCitizen folder", AllowMultiple = false,
        })).GetAwaiter().GetResult();
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }
    void IPanelActions.PutBackPrevious() => Ask(PutBackPrevious);
    void IPanelActions.RemoveFile() => Ask(RemoveFile);
    void IPanelActions.CheckForUpdates() => _ = FindUpdateAsync(quiet: false);
    void IPanelActions.Update() => _ = UpdateAsync();

    // ---- updating, only when the player asks --------------------------------------------------------------

    /// <summary>Asks stardexs.com for the newest signed release; offers it in the panel if it's newer.</summary>
    private async Task FindUpdateAsync(bool quiet)
    {
        if (!quiet) SetStatus("Checking for updates…");
        Release? found = null;
        try { found = await _updater.NewestAsync(Version.Parse(_version), _quit.Token); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { if (!quiet) SetStatus("Couldn't reach the StarDad website. Try again in a moment."); return; }
        Post(() =>
        {
            _release = found;
            if (found is not null)
            {
                Balloon("A new StarDad Companion is ready", $"Version {found.Version.ToString(3)} is signed and ready. Click the tray icon and press Update.");
            }
            else if (!quiet) _status = $"You have the newest StarDad Companion ({_version}).";
            Refresh();
        });
    }

    /// <summary>Downloads the signed release, swaps it in, and starts it; this copy then closes.</summary>
    private async Task UpdateAsync()
    {
        if (_release is not { } release || Environment.ProcessPath is not { } exe || _busy) return;
        _busy = true;
        SetStatus($"Updating to {release.Version.ToString(3)}…");
        var problem = await _updater.InstallAsync(release, exe, _quit.Token);
        if (problem is not null)
        {
            Post(() => { _busy = false; _status = problem; Refresh(); Balloon("StarDad Companion", problem, warning: true); });
            return;
        }
        Post(() =>
        {
            try { Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false })?.Dispose(); }
            catch (Win32Exception) { _busy = false; _status = "Updated. Start StarDad Companion again to use it."; Refresh(); return; }
            ((IPanelActions)this).Quit();
        });
    }

    void IPanelActions.Quit()
    {
        _quit.Cancel();
        _clock?.Stop();
        _manifest?.Dispose();
        RemoveIcon();
        DestroyWindow(_window);
        _window = 0;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    /// <summary>Reports stages as they happen (Progress&lt;T&gt; would need a synchronization context; this posts itself).</summary>
    private sealed class StageReporter(Action<SyncStage> report) : IProgress<SyncStage>
    {
        public void Report(SyncStage value) => report(value);
    }

    // ---- the icon --------------------------------------------------------------------------------------------

    private unsafe NOTIFYICONDATAW IconData(uint flags) => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = _window, uID = 1, uFlags = flags, uCallbackMessage = WM_TRAY, hIcon = _icon,
    };

    private unsafe void AddIcon()
    {
        var data = IconData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Copy(data.szTip, 128, Tip());
        Shell_NotifyIconW(NIM_ADD, &data);
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &data);
    }

    private unsafe void RemoveIcon()
    {
        var data = IconData(0);
        Shell_NotifyIconW(NIM_DELETE, &data);
    }

    private unsafe void UpdateTip()
    {
        var data = IconData(NIF_TIP | NIF_SHOWTIP);
        Copy(data.szTip, 128, Tip());
        Shell_NotifyIconW(NIM_MODIFY, &data);
    }

    private unsafe void Balloon(string title, string text, bool warning = false)
    {
        var data = IconData(NIF_INFO);
        Copy(data.szInfoTitle, 64, title);
        Copy(data.szInfo, 256, text);
        data.dwInfoFlags = warning ? NIIF_WARNING : NIIF_INFO;
        Shell_NotifyIconW(NIM_MODIFY, &data);
    }

    private string Tip() => Shorten((_busy ? "StarDad Companion is syncing: " : "StarDad Companion: ") + _status, 127);

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private unsafe static void Copy(char* target, int size, string text)
    {
        var buffer = new Span<char>(target, size);
        buffer.Clear();
        text.AsSpan(0, Math.Min(text.Length, size - 1)).CopyTo(buffer);
    }

    // ---- plumbing --------------------------------------------------------------------------------------------

    /// <summary>Runs <paramref name="action"/> on the main (UI) thread.</summary>
    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    private void SetStatus(string status) => Post(() => { _status = status; Refresh(); });

    private static void Open(Uri page) => Open(page.AbsoluteUri);

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception) { }
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        var app = _app;
        if (app is null || app._window == 0) return DefWindowProcW(window, message, wParam, lParam);
        try
        {
            switch (message)
            {
                case WM_TRAY:
                    // Version 4 icons put the event in the low word of lParam.
                    var trayEvent = (uint)(lParam & 0xFFFF);
                    if (trayEvent is WM_CONTEXTMENU or WM_RBUTTONUP or NIN_SELECT or NIN_KEYSELECT) app.ShowPanel();
                    else if (trayEvent == NIN_BALLOONUSERCLICK) Open(SyncApi.DefaultSite);
                    return 0;
            }
            if (message == app._syncNowMessage && message != 0)
            {
                // From a second copy started by "Send to my PC": wParam carries the account id, if the link had one.
                _ = app.SyncAsync(SyncLink.FromNumber(wParam), manual: true);
                return 0;
            }
            if (message == app._scheduleMessage && message != 0)
            {
                // From a second copy started by "Save schedule" on the StarDad website.
                _ = app.RefreshScheduleAsync(SyncLink.FromNumber(wParam));
                return 0;
            }
            if (message == app._taskbarCreated && message != 0)
            {
                app.AddIcon(); // Explorer restarted: put the icon back
                return 0;
            }
        }
        catch (Exception exception)
        {
            app._status = $"Something went wrong: {exception.Message}";
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }
}
