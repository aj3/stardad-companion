using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using StarDexs.Sync.Core;
using static StarDexs.Sync.NativeMethods;

namespace StarDexs.Sync;

/// <summary>
/// The tray icon and everything behind its menu. It never checks in on its own: a sync happens only when the
/// player presses "Send to my PC" in their stardexs.com account (the stardexs-sync: link) or "Sync" in the
/// menu, and only for an account the player linked on this PC. Watching for a game update reads
/// build_manifest.id on this PC; nothing goes online for it.
/// Windows messages arrive on the main thread; syncing runs on the thread pool, and anything that asks the
/// player a question runs on its own short-lived thread so the icon never freezes. One lock keeps the state
/// file to one writer at a time.
/// </summary>
internal sealed class TrayApp
{
    private const uint WM_TRAY = WM_APP + 1, WM_POSTED = WM_APP + 2;
    private const nuint IdStatus = 1, IdSite = 3, IdFolder = 4, IdPrevious = 7, IdRemove = 8, IdLink = 9, IdQuit = 11,
        IdAllow = 12, IdAccess = 13, IdSyncAccount = 100, IdUnlinkAccount = 200;
    private const string ClassName = "StarDexsSyncTray", SyncNowMessage = "StarDexsSync.SyncNow";
    /// <summary>How often to look (locally, without going online) whether the game has closed.</summary>
    private static readonly TimeSpan GameCheck = TimeSpan.FromSeconds(15);
    private static TrayApp? _app;

    private readonly bool _syncNow;
    private readonly string? _account;
    private readonly string _version = typeof(TrayApp).Assembly.GetName().Version?.ToString(3) ?? "0";
    private readonly string _dataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StarDexs", "Sync");
    private readonly StateStore _store;
    private readonly SyncApi _api;
    private readonly SyncEngine _engine;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<Action> _posted = new();
    private readonly CancellationTokenSource _quit = new();
    private FileSystemWatcher? _manifest;
    private uint _taskbarCreated, _syncNowMessage;
    private nint _window, _icon;
    private string _status = "Ready.";
    private bool _needsAccess, _toldAccess, _toldUpdate;
    private int _asking, _waitingForGame;
    /// <summary>The menu's account items, in order: ids <see cref="IdSyncAccount"/>+i and <see cref="IdUnlinkAccount"/>+i.</summary>
    private string[] _menuAccounts = [];

    public TrayApp(bool syncNow, string? account)
    {
        _syncNow = syncNow;
        _account = account;
        _store = new StateStore(_dataFolder, new DpapiProtector());
        _api = new SyncApi(SyncApi.CreateClient(Site(), _version));
        _engine = new SyncEngine(_api, _store, new InstallWriter(_dataFolder));
    }

    // Release builds only ever talk to stardexs.com. Debug builds can point at a local copy of the site.
    private static Uri Site()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("STARDEXS_SITE") is { Length: > 0 } site) return new Uri(site);
#endif
        return SyncApi.DefaultSite;
    }

    public unsafe int Run()
    {
        _app = this;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _syncNowMessage = RegisterWindowMessageW(SyncNowMessage);
        try { SyncLink.Register(); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        var instance = GetModuleHandleW(null);
        fixed (char* className = ClassName)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &WindowProc, hInstance = instance, lpszClassName = className,
            };
            if (RegisterClassExW(&windowClass) == 0) return 1;
        }
        // A window that's never shown: it only receives the icon's clicks and the menu's commands.
        _window = CreateWindowExW(0, ClassName, "StarDexs Sync", 0, 0, 0, 0, 0, 0, 0, instance, 0);
        if (_window == 0) return 1;
        _icon = LoadImageW(instance, IDI_APPLICATION, IMAGE_ICON, GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), 0);
        if (_icon == 0) _icon = LoadImageW(0, IDI_APPLICATION, IMAGE_ICON, 0, 0, LR_SHARED | LR_DEFAULTSIZE);
        AddIcon();

        var state = _store.Load();
        if (state.Accounts.Count == 0 || !GameFolder.IsLive(state.GameFolder)) Ask(Setup);
        else if (_syncNow) _ = SyncAsync(_account, manual: true);
        else Balloon("StarDexs Sync is ready", "Press \"Send to my PC\" in your stardexs.com account when you want your file.");
        WatchForGameUpdates();

        MSG message;
        while (GetMessageW(&message, 0, 0, 0) > 0)
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        _quit.Cancel();
        _manifest?.Dispose();
        RemoveIcon();
        return 0;
    }

    /// <summary>Called by a second copy started from the website's link: hand the request (and its account) to this one.</summary>
    public static void AskRunningCopyToSync(string? account)
    {
        var window = FindWindowW(ClassName, null);
        if (window != 0) PostMessageW(window, RegisterWindowMessageW(SyncNowMessage), SyncLink.ToNumber(account), 0);
    }

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

    private void CheckGameUpdate()
    {
        if (_engine.GameUpdated() is null) return;
        _status = "The game updated; your file is for the last patch.";
        UpdateTip();
        Balloon("The game updated", "Your StarDexs file is for the last patch. Open your stardexs.com account and press \"Send to my PC\" when the new one is ready.");
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

    private async Task SyncAsync(string? account, bool manual) =>
        await RunAsync(() => _engine.RunAsync(account, _quit.Token), report => Show(report, account, manual));

    private async Task RunAsync(Func<Task<SyncReport>> work, Action<SyncReport> show)
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            SetStatus("Working…");
            var report = await Task.Run(work);
            Post(() => show(report));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { SetStatus($"Something went wrong: {exception.Message}"); }
        finally { _gate.Release(); }
    }

    private void Show(SyncReport report, string? account, bool manual)
    {
        _status = report.Message;
        _needsAccess = report.NeedsPermission;
        UpdateTip();
        if (report.Installed) Balloon("Your StarDexs file is installed", report.Message);
        else if (report.WaitingForGame)
        {
            Balloon("StarDexs Sync", "Your file goes in as soon as you close the game.");
            _ = FinishAfterGameClosesAsync(account);
        }
        else if (report.UnknownFile) Ask(() => ReplaceUnknown(account));
        else if (report.NotLinked && account is not null && manual) Ask(() => { if (Link(account)) Post(() => _ = SyncAsync(account, manual: true)); });
        else if (report.NeedsPermission && manual) Ask(AllowAccess);
        else if (report.NeedsPermission && !_toldAccess)
        {
            _toldAccess = true;
            Balloon("StarDexs Sync needs your permission", "Right-click its icon and choose \"Allow access to the game folder\".", warning: true);
        }
        else if (manual)
        {
            var sent = report.NewBlueprints + report.NewCompleted > 0
                ? $" Sent {report.NewBlueprints} new blueprints and {report.NewCompleted} finished contracts."
                : "";
            Balloon("StarDexs Sync", report.Message + sent);
        }
        if (!_toldUpdate && report.HelperLatest is { } latest && Version.TryParse(latest, out var newest)
            && Version.TryParse(_version, out var mine) && newest > mine)
        {
            _toldUpdate = true;
            Balloon("A new StarDexs Sync is available", $"Version {latest} is on stardexs.com. Download it there when you're ready.");
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
        }) { IsBackground = true, Name = "StarDexs questions" };
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
            ? "You pressed \"Send to my PC\" in a stardexs.com account this PC isn't linked to yet. Link it now?\n\n"
            : "Link this PC to your stardexs.com account.\n\n";
        if (!Dialogs.Confirm(intro +
                "Your browser will open a page with a code on it. Sign in to the account there and approve the code.\n\n" +
                $"This PC will be listed on the account as \"{device}\". You can link more than one account."))
            return false;
        LinkStart start;
        try { start = _api.StartLinkAsync(device, _quit.Token).GetAwaiter().GetResult(); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Dialogs.Tell("Couldn't reach stardexs.com. Check your connection, then choose \"Link an account\" from the tray menu.", warning: true);
            return false;
        }
        Open(start.LinkPage);
        SetStatus($"Waiting for you to approve code {start.Code} on stardexs.com…");
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

    private bool ChooseFolder(string? found)
    {
        var folder = found is not null && Dialogs.Ask($"Is this your game's LIVE folder?\n\n{found}") ? found : null;
        while (folder is null)
        {
            var picked = Dialogs.PickFolder("Choose your game's LIVE folder (the one with Data.p4k in it).");
            if (picked is null) return false;
            if (GameFolder.IsLive(picked)) folder = picked;
            else Dialogs.Tell("That folder has no Data.p4k in it. Choose the LIVE folder, for example ...\\StarCitizen\\LIVE.", warning: true);
        }
        Update(state =>
        {
            if (string.Equals(state.GameFolder, folder, StringComparison.OrdinalIgnoreCase)) return;
            // A different install: its logs and files start from scratch.
            state.GameFolder = folder;
            state.InstalledAccount = state.InstalledPatch = state.InstalledSettings = state.InstalledSha = state.NoticedBuild = null;
            foreach (var account in state.Accounts.Values) account.LogOffsets.Clear();
        });
        return true;
    }

    private void AllowInstalling()
    {
        var agreed = Dialogs.Ask(
            "Can StarDexs Sync put your StarDexs file into the game folder?\n\n" +
            "It changes only:\n" +
            "  •  data\\Localization\\english\\global.ini (your file)\n" +
            "  •  the language lines in user.cfg\n\n" +
            "It waits while the game is running. stardexs.com keeps your last two files, so you can put the " +
            "previous one back, or take the file out, from the tray menu at any time.\n\n" +
            "Choose No to only send your logs. You can allow this later from the tray menu.");
        Update(state => state.InstallAgreed = agreed);
    }

    private void ReplaceUnknown(string? account)
    {
        if (!Dialogs.Ask(
                "There's a global.ini in your game folder that StarDexs Sync didn't put there " +
                "(maybe from another tool, or changed by hand).\n\n" +
                "Replace it with your StarDexs file? The old one isn't kept."))
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
                "Windows doesn't let StarDexs Sync write to your game folder yet.\n\n" +
                "Next, Windows asks for administrator permission once. StarDexs Sync then lets your Windows account change only:\n" +
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
        if (!Dialogs.Confirm("Put back the file you had installed before this one? stardexs.com keeps it for you.\n\nThe next \"Send to my PC\" installs your current file again."))
            return;
        Post(() => _ = RunAsync(() => _engine.PutBackPreviousAsync(_quit.Token), report => Show(report, null, manual: true)));
    }

    private void RemoveFile()
    {
        if (!Dialogs.Confirm("Take your StarDexs file out of the game? The game shows its own text again.\n\nOnly the global.ini StarDexs Sync installed is removed. \"Send to my PC\" puts it back."))
            return;
        Post(() => _ = RunAsync(() => Task.FromResult(_engine.Remove()), report => Show(report, null, manual: true)));
    }

    private void Unlink(string account)
    {
        var name = _store.Load().Accounts.TryGetValue(account, out var linked) ? linked.Name : "this account";
        if (!Dialogs.Ask($"Unlink {name} from this PC?\n\n\"Send to my PC\" in that account won't reach this PC until you link it again. Your installed file stays as it is. You can also remove this PC from the account page."))
            return;
        Update(state => _store.Unlink(state, account));
        SetStatus($"Unlinked {name}.");
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

    // ---- the menu --------------------------------------------------------------------------------------------

    private unsafe void ShowMenu()
    {
        var state = _store.Load();
        var hasFolder = GameFolder.IsLive(state.GameFolder);
        var menu = CreatePopupMenu();
        try
        {
            Item(menu, IdStatus, Shorten(_status, 60), grayed: true);
            Separator(menu);
            _menuAccounts = [.. state.Accounts.Keys.Order(StringComparer.Ordinal)];
            for (var i = 0; i < _menuAccounts.Length && i < 50; i++)
                Item(menu, IdSyncAccount + (nuint)i, $"Sync {state.Accounts[_menuAccounts[i]].Name}", grayed: !hasFolder);
            Item(menu, IdLink, state.Accounts.Count == 0 ? "Link your account…" : "Link another account…");
            if (state.Accounts.Count > 0 && hasFolder && !state.InstallAgreed) Item(menu, IdAllow, "Allow installing files…");
            if (_needsAccess) Item(menu, IdAccess, "Allow access to the game folder…");
            Item(menu, IdSite, "Open stardexs.com");
            Separator(menu);
            Item(menu, IdFolder, "Change game folder…");
            Item(menu, IdPrevious, "Put previous file back…", grayed: !hasFolder || state.InstalledSha is null);
            Item(menu, IdRemove, "Remove StarDexs from the game…", grayed: !hasFolder || state.InstalledSha is null);
            for (var i = 0; i < _menuAccounts.Length && i < 50; i++)
                Item(menu, IdUnlinkAccount + (nuint)i, $"Unlink {state.Accounts[_menuAccounts[i]].Name}…");
            Separator(menu);
            Item(menu, IdQuit, "Quit");

            POINT point;
            GetCursorPos(&point);
            SetForegroundWindow(_window); // so the menu closes when the player clicks elsewhere
            TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, point.X, point.Y, 0, _window, 0);
            PostMessageW(_window, WM_NULL, 0, 0);
        }
        finally { DestroyMenu(menu); }
    }

    private void Command(nuint id)
    {
        if (id >= IdSyncAccount && id < IdSyncAccount + (nuint)_menuAccounts.Length)
        {
            _ = SyncAsync(_menuAccounts[(int)(id - IdSyncAccount)], manual: true);
            return;
        }
        if (id >= IdUnlinkAccount && id < IdUnlinkAccount + (nuint)_menuAccounts.Length)
        {
            var account = _menuAccounts[(int)(id - IdUnlinkAccount)];
            Ask(() => Unlink(account));
            return;
        }
        switch (id)
        {
            case IdLink:
                if (_store.Load().Accounts.Count == 0) Ask(Setup);
                else Ask(() => Link(null));
                break;
            case IdAllow: Ask(() => { AllowInstalling(); Post(() => _ = SyncAsync(null, manual: true)); }); break;
            case IdAccess: Ask(AllowAccess); break;
            case IdSite: Open(SyncApi.DefaultSite); break;
            case IdFolder: Ask(() => { if (ChooseFolder(null)) Post(WatchForGameUpdates); }); break;
            case IdPrevious: Ask(PutBackPrevious); break;
            case IdRemove: Ask(RemoveFile); break;
            case IdQuit: DestroyWindow(_window); break;
        }
    }

    private static void Item(nint menu, nuint id, string text, bool grayed = false) =>
        AppendMenuW(menu, MF_STRING | (grayed ? MF_GRAYED : 0), id, text);

    private static void Separator(nint menu) => AppendMenuW(menu, MF_SEPARATOR, 0, null);

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

    private string Tip() => Shorten("StarDexs Sync: " + _status, 127);

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private unsafe static void Copy(char* target, int size, string text)
    {
        var buffer = new Span<char>(target, size);
        buffer.Clear();
        text.AsSpan(0, Math.Min(text.Length, size - 1)).CopyTo(buffer);
    }

    // ---- plumbing --------------------------------------------------------------------------------------------

    /// <summary>Runs <paramref name="action"/> on the main thread.</summary>
    private void Post(Action action)
    {
        _posted.Enqueue(action);
        PostMessageW(_window, WM_POSTED, 0, 0);
    }

    private void SetStatus(string status) => Post(() => { _status = status; UpdateTip(); });

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
                    if (trayEvent is WM_CONTEXTMENU or WM_RBUTTONUP or NIN_SELECT or NIN_KEYSELECT) app.ShowMenu();
                    else if (trayEvent == NIN_BALLOONUSERCLICK) Open(SyncApi.DefaultSite);
                    return 0;
                case WM_COMMAND:
                    app.Command(wParam & 0xFFFF);
                    return 0;
                case WM_POSTED:
                    while (app._posted.TryDequeue(out var action)) action();
                    return 0;
                case WM_DESTROY:
                    PostQuitMessage(0);
                    return 0;
            }
            if (message == app._syncNowMessage && message != 0)
            {
                // From a second copy started by "Send to my PC": wParam carries the account id, if the link had one.
                _ = app.SyncAsync(SyncLink.FromNumber(wParam), manual: true);
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
