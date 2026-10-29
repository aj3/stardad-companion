namespace StarDexs.Sync.Core;

/// <summary>What one pass did, for the tray's tooltip and notifications.</summary>
public sealed record SyncReport(
    string Message, bool Installed = false, bool NeedsPermission = false, bool NotLinked = false, bool WaitingForGame = false,
    bool UnknownFile = false, int NewBlueprints = 0, int NewCompleted = 0, string? HelperLatest = null,
    bool Completed = false, bool ConfirmationPending = false, bool Prepared = false);

/// <summary>Where a sync is up to, for the app's sync bar and the account page's.</summary>
public enum SyncStage { Reading, Sending, Checking, Downloading, Installing, Finished }

public static class SyncStages
{
    /// <summary>The name stardad.space shows for a stage.</summary>
    public static string Name(this SyncStage stage) => stage.ToString().ToLowerInvariant();

    /// <summary>How full the sync bar is when a stage starts (0 to 1).</summary>
    public static double Share(this SyncStage stage) => (int)stage / (double)SyncStage.Finished;
}

/// <summary>
/// One sync for one linked account: read what's new in the logs and send it, then check whether that
/// account's file is current and install it if the player agreed. Nothing here runs on its own; the app
/// calls it when the player asks (from stardad.space or the tray menu) or at the times they chose on the StarDad website.
/// </summary>
public sealed class SyncEngine(SyncApi api, StateStore store, InstallWriter writer)
{
    public const int MaxBlueprintsPerSend = 4000, MaxCompletedPerSend = 2000;

    /// <param name="account">The account the player pressed "Send to my PC" in; null: the last one used.</param>
    /// <param name="progress">Told as each stage starts. Each stage (and the result) also goes to stardad.space, so the
    /// player's account page can show the sync as it happens; only the stage name and the result's message.</param>
    public async Task<SyncReport> RunAsync(string? account, CancellationToken cancel, IProgress<SyncStage>? progress = null, bool prepareOnly = false, bool manual = true, string? continuedWorkflow = null)
    {
        var state = store.Load();
        account ??= state.LastAccount;
        var token = store.Token(state, account);
        var runId = Guid.NewGuid().ToString("N");
        string? workflow = continuedWorkflow;
        if (token is not null)
        {
            try { workflow ??= await api.ClaimWorkflowAsync(token, prepareOnly, cancel); }
            catch (Exception e) when (e is HttpRequestException or NotLinkedException) { } // Older sites and tray/scheduled syncs remain usable.
            if (workflow is not null) runId = workflow;
        }
        if (token is not null && account is not null && state.Accounts[account].PendingResult is { } pending)
        {
            if (!await ConfirmAsync(token, pending, cancel))
                return new SyncReport("The previous sync still needs confirmation from StarDad. Try again when the website is reachable.", ConfirmationPending: true);
            state.Accounts[account].PendingResult = null;
            store.Save(state);
        }
        if (!prepareOnly && !manual && state.PendingInstall is { } other && other.Account != account)
            return new SyncReport("Another account has an update queued. Select an account explicitly to replace it.");
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var heartbeat = workflow is null || token is null ? Task.CompletedTask : HeartbeatAsync(token, workflow, heartbeatStop.Token);
        SyncReport report;
        var storageFailed = false;
        try { report = await RunLinkedAsync(state, account, token, progress, cancel, runId, prepareOnly, workflow); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { storageFailed = true; report = new("Couldn't save Companion's local state. No new update is confirmed queued. Check disk space and folder access, then retry."); }
        finally { heartbeatStop.Cancel(); await heartbeat; }
        if (workflow is not null && token is not null)
        {
            var stage = storageFailed ? "failed" : report.Prepared ? "ready" : report.WaitingForGame && state.PendingInstall is not null ? "queued" : report.Completed ? "finished"
                : !GameFolder.IsLive(state.GameFolder) ? "needs_folder" : !state.InstallAgreed || report.NeedsPermission ? "needs_permission" : prepareOnly && state.Logs.Count == 0 ? "needs_logs" : "failed";
            try { await api.WorkflowAsync(token, workflow, stage, cancel, report.Message, state.Logs.Count > 0,
                GameFolder.IsLive(state.GameFolder), state.InstallAgreed, state.PendingInstall?.Settings ?? state.InstalledSettings); }
            catch (HttpRequestException) { }
        }
        progress?.Report(SyncStage.Finished);
        if (storageFailed) return report;
        if (token is not null && account is not null && !report.NotLinked)
        {
            var result = new PendingSyncResult(runId, report.Message, Succeeded(report), report.WaitingForGame && state.PendingInstall is not null ? "queued" : "finished");
            state.Accounts[account].PendingResult = result;
            store.Save(state);
            if (await ConfirmAsync(token, result, cancel)) state.Accounts[account].PendingResult = null;
            else report = report with { ConfirmationPending = true, Message = report.Message + " Website confirmation is pending; it will retry at the next sync." };
            store.Save(state);
        }
        return report;
    }

    // A bounded operation can report health; a stalled worker cannot stay healthy indefinitely.
    private long _checkpoint;
    private void Checkpoint() => Interlocked.Exchange(ref _checkpoint, DateTimeOffset.UtcNow.UtcTicks);
    private async Task HeartbeatAsync(string token, string id, CancellationToken cancel)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancel);
                if (DateTimeOffset.UtcNow.UtcTicks - Interlocked.Read(ref _checkpoint) > TimeSpan.FromSeconds(90).Ticks) continue;
                try { using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel); deadline.CancelAfter(TimeSpan.FromSeconds(5)); await api.WorkflowAsync(token, id, null, deadline.Token); }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or NotLinkedException) { }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<bool> ConfirmAsync(string token, PendingSyncResult result, CancellationToken cancel)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try {
                if (result.Patch is not null && result.Sha256 is not null) await api.InstalledAsync(token, result.Patch, result.Sha256, cancel);
                if (result.Ok && result.Patch is not null) try { await api.WorkflowAsync(token, result.RunId, "finished", cancel, result.Message); } catch (HttpRequestException) { }
                await api.ProgressAsync(token, result.Stage, result.Message, result.Ok, cancel, result.RunId); return true;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or NotLinkedException)
            {
                if (cancel.IsCancellationRequested) return false;
                if (attempt < 2) await Task.Delay(250 * (attempt + 1), cancel);
            }
        }
        return false;
    }

    private async Task<SyncReport> RunLinkedAsync(SyncState state, string? account, string? token, IProgress<SyncStage>? progress, CancellationToken cancel, string runId, bool prepareOnly, string? workflow)
    {
        async Task Stage(SyncStage stage)
        {
            Checkpoint();
            progress?.Report(stage);
            await TellSiteAsync(token!, stage, null, cancel, runId);
            if (workflow is not null) try { await api.WorkflowAsync(token!, workflow, stage.Name(), cancel); } catch (HttpRequestException) { }
        }

        if (token is null)
            return Done(state, new SyncReport(state.Accounts.Count == 0 || account is null
                ? "Not linked to an account yet."
                : "This PC isn't linked to that StarDad account yet.", NotLinked: true));
        if (!GameFolder.IsLive(state.GameFolder)) return Done(state, new SyncReport("Choose your game folder to start."));
        state.LastAccount = account;
        try
        {
            await Stage(SyncStage.Reading);
            var (blueprints, completed) = await SendLogsAsync(state, token, () => Stage(SyncStage.Sending), cancel);
            await Stage(SyncStage.Checking);
            var status = await api.StatusAsync(token, cancel);
            TakeSchedule(state.Accounts[account!], status.Schedule);
            state.Accounts[account!].BackupKeybinds=status.KeybindBackup;
            var keybindNote="";
            if(status.KeybindBackup)
            {
                try
                {
                    foreach(var name in KeybindFiles.Exports(state.GameFolder!))
                    {
                        var data=KeybindFiles.Read(state.GameFolder!,name);var hash=KeybindFiles.Hash(data);var key=Path.Combine(Path.GetFullPath(state.GameFolder!),name);
                        if(state.Accounts[account!].KeybindHashes.GetValueOrDefault(key)==hash)continue;
                        await api.UploadKeybindAsync(token,name,data,status.Patch,cancel);state.Accounts[account!].KeybindHashes[key]=hash;
                    }
                }
                catch(Exception e)when(e is IOException or UnauthorizedAccessException or HttpRequestException)
                {keybindNote=" Keybind backup needs attention. Existing cloud backups are kept; review Keybind backups in Settings.";}
            }
            SyncReport Report(string message, bool installed = false, bool permission = false, bool waiting = false, bool unknown = false) =>
                new(message+keybindNote, installed, permission, false, waiting, unknown, blueprints, completed, status.HelperLatest);

            if (prepareOnly) return Done(state, Report(state.Logs.Count == 0 ? "No readable game logs found. Start the game once, then check again."
                : !state.InstallAgreed ? "Logs synced. Allow installing in Companion to finish setup."
                : "Logs synced. Choose your options on StarDad.") with { Prepared = state.Logs.Count > 0 && state.InstallAgreed, Completed = state.Logs.Count > 0 && state.InstallAgreed });
            if (!status.HasSettings) return Done(state, Report("Logs synced. Finish Customize on the website before installing a file."));

            if (status.Patch is null) return Done(state, Report("StarDad has no file for this patch yet."));
            var current = status.Patch == state.InstalledPatch && status.Settings == state.InstalledSettings && account == state.InstalledAccount
                          && File.Exists(GameFolder.GlobalIniPath(state.GameFolder!))
                          && state.InstalledSha == InstallWriter.Sha256File(GameFolder.GlobalIniPath(state.GameFolder!));
            if (current)
            {
                await api.InstalledAsync(token, state.InstalledPatch!, state.InstalledSha!, cancel);
                return Done(state, Report($"Up to date ({status.Patch}).") with { Completed = true });
            }
            if (!state.InstallAgreed) return Done(state, Report($"A file for {status.Patch} is ready; allow installing to put it in place."));

            await Stage(SyncStage.Downloading);
            var file = await api.FileAsync(token, cancel);
            await Stage(SyncStage.Installing);
            var result = writer.Install(state.GameFolder!, file.Data, file.Patch, state.InstalledSha, state.ReplaceUnknownOnce);
            switch (result.Outcome)
            {
                case WriteOutcome.Written or WriteOutcome.AlreadyCurrent:
                    Installed(state, account!, file.Patch, file.Settings, result.Sha256!);
                    await api.InstalledAsync(token, file.Patch, result.Sha256!, cancel);
                    return Done(state, Report(result.Message, installed: result.Outcome == WriteOutcome.Written) with { Completed = true });
                case WriteOutcome.NeedsPermission: return Done(state, Report(result.Message, permission: true));
                case WriteOutcome.GameRunning:
                    if (!InstallWriter.LooksLikeGlobalIni(file.Data)) return Done(state, Report("The downloaded file was invalid; no update was queued."));
                    state.PendingInstall = new(account!, state.GameFolder!, file.Patch, file.Settings,
                        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file.Data)).ToLowerInvariant(), file.Data, runId);
                    store.Save(state); // Atomic state replacement includes the complete file and its metadata.
                    return Done(state, Report("Update queued on this PC. Close the game, then wait for Installed before starting it again.", waiting: true));
                case WriteOutcome.UnknownFile: return Done(state, Report(result.Message, unknown: true));
                default: return Done(state, Report(result.Message));
            }
        }
        catch (NotLinkedException)
        {
            store.Unlink(state, account!);
            return Done(state, new SyncReport("This PC was unlinked from that account. Link it again to carry on.", NotLinked: true));
        }
        catch (HttpRequestException)
        {
            return Done(state, new SyncReport("Couldn't reach the StarDad website. Try again in a moment."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Done(state, new SyncReport("Couldn't read the installed file. Check folder access and try again."));
        }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
        {
            return Done(state, new SyncReport("The StarDad website took too long to answer. Try again in a moment."));
        }
    }

    public async Task<SyncReport> ResumePendingAsync(CancellationToken cancel)
    {
        var state = store.Load();
        if (state.PendingInstall is not { } pending) return new SyncReport("No update queued.");
        if (InstallWriter.GameIsRunning()) return new SyncReport("Update queued. Close the game and wait for Installed before restarting it.", WaitingForGame: true);
        if (!state.InstallAgreed || state.GameFolder != pending.Folder || store.Token(state, pending.Account) is not { } token)
            return new SyncReport("Queued update needs its original account, game folder and install permission.");
        if (!InstallWriter.LooksLikeGlobalIni(pending.Data) || InstallWriter.Sha256(pending.Data) != pending.Sha256)
            return new SyncReport("Queued file failed verification. Sync again to replace it.");
        var build = GameFolder.Build(pending.Folder);
        if (build is null) return new SyncReport("Cannot verify the game version. Finish the launcher update, then sync again.");
        if (build != GameFolder.PatchBuild(pending.Patch))
        {
            state.PendingInstall = null; store.Save(state);
            return await RunAsync(pending.Account, cancel, continuedWorkflow: pending.RunId);
        }
        var result = writer.Install(pending.Folder, pending.Data, pending.Patch, state.InstalledSha, state.ReplaceUnknownOnce);
        if (result.Outcome is not (WriteOutcome.Written or WriteOutcome.AlreadyCurrent)) return new SyncReport(result.Message,
            NeedsPermission: result.Outcome == WriteOutcome.NeedsPermission, UnknownFile: result.Outcome == WriteOutcome.UnknownFile, WaitingForGame: result.Outcome == WriteOutcome.GameRunning);
        Installed(state, pending.Account, pending.Patch, pending.Settings, result.Sha256!);
        state.PendingInstall = null;
        state.Accounts[pending.Account].PendingResult = new(pending.RunId, result.Message, true, Patch: pending.Patch, Sha256: result.Sha256);
        store.Save(state);
        try
        {
            await api.InstalledAsync(token, pending.Patch, result.Sha256!, cancel);
            await api.WorkflowAsync(token, pending.RunId, "finished", cancel, result.Message, settings: pending.Settings);
            await api.ProgressAsync(token, "finished", result.Message, true, cancel, pending.RunId);
            state.Accounts[pending.Account].PendingResult = null; store.Save(state);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or NotLinkedException) { return new(result.Message + " Website confirmation will retry.", Installed: true, Completed: true, ConfirmationPending: true); }
        return new(result.Message, Installed: true, Completed: true);
    }

    public async Task PulsePendingAsync(CancellationToken cancel)
    {
        var state = store.Load();
        if (state.PendingInstall is not { } pending || store.Token(state, pending.Account) is not { } token) return;
        try { using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel); deadline.CancelAfter(TimeSpan.FromSeconds(5)); await api.WorkflowAsync(token, pending.RunId, null, deadline.Token); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or NotLinkedException) { }
    }

    /// <summary>Puts back the file installed before the current one, which stardad.space keeps (the PC keeps no copies).</summary>
    public async Task<SyncReport> PutBackPreviousAsync(CancellationToken cancel)
    {
        var state = store.Load();
        var account = state.InstalledAccount ?? state.LastAccount;
        if (store.Token(state, account) is not { } token) return new SyncReport("Not linked to an account yet.", NotLinked: true);
        if (!GameFolder.IsLive(state.GameFolder)) return new SyncReport("Choose your game folder first.");
        try
        {
            if (await api.PreviousFileAsync(token, cancel) is not { } previous)
                return new SyncReport("There's no earlier file to go back to yet.");
            var result = writer.Install(state.GameFolder!, previous.Data, previous.Patch, state.InstalledSha);
            if (result.Outcome is not (WriteOutcome.Written or WriteOutcome.AlreadyCurrent))
                return new SyncReport(result.Message, NeedsPermission: result.Outcome == WriteOutcome.NeedsPermission,
                    WaitingForGame: result.Outcome == WriteOutcome.GameRunning, UnknownFile: result.Outcome == WriteOutcome.UnknownFile);
            // Settings unknown: the next "Send to my PC" installs the current file again.
            Installed(state, account!, previous.Patch, null, result.Sha256!);
            await api.InstalledAsync(token, previous.Patch, result.Sha256!, cancel);
            return Done(state, new SyncReport($"Put back your previous file (for {previous.Patch}). Restart the game to see it.", Installed: true));
        }
        catch (NotLinkedException) { return new SyncReport("This PC was unlinked from that account.", NotLinked: true); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new SyncReport("Couldn't reach the StarDad website. Try again in a moment.");
        }
    }

    /// <summary>Takes the installed StarDexs file out of the game (on this PC only; nothing online).</summary>
    public SyncReport Remove()
    {
        var state = store.Load();
        if (!GameFolder.IsLive(state.GameFolder)) return new SyncReport("Choose your game folder first.");
        var result = writer.Remove(state.GameFolder!, state.InstalledSha);
        if (result.Outcome == WriteOutcome.Written)
            state.InstalledAccount = state.InstalledPatch = state.InstalledSettings = state.InstalledSha = null;
        return Done(state, new SyncReport(result.Message, NeedsPermission: result.Outcome == WriteOutcome.NeedsPermission,
            WaitingForGame: result.Outcome == WriteOutcome.GameRunning, UnknownFile: result.Outcome == WriteOutcome.UnknownFile,
            Completed: result.Outcome is WriteOutcome.Written or WriteOutcome.AlreadyCurrent));
    }

    /// <summary>
    /// The game's build when it has moved past the installed file and the player hasn't been told yet;
    /// read from build_manifest.id on this PC, nothing online.
    /// </summary>
    public string? GameUpdated()
    {
        var state = store.Load();
        if (!GameFolder.IsLive(state.GameFolder) || state.InstalledPatch is null) return null;
        var build = GameFolder.Build(state.GameFolder!);
        if (build is null || build == GameFolder.PatchBuild(state.InstalledPatch) || build == state.NoticedBuild) return null;
        state.NoticedBuild = build;
        store.Save(state);
        return build;
    }

    private static void Installed(SyncState state, string account, string patch, string? settings, string sha)
    {
        state.PendingInstall = null;
        state.InstalledAccount = account;
        state.InstalledPatch = patch;
        state.InstalledSettings = settings;
        state.InstalledSha = sha;
        state.ReplaceUnknownOnce = false;
    }

    /// <summary>
    /// Everything the logs hold, sent every time (stardad.space keeps each once), so anything removed from the
    /// account comes back. Only logs that are new or changed since the last sync are read again.
    /// Returns how many the account didn't have yet.
    /// </summary>
    private async Task<(int Blueprints, int Completed)> SendLogsAsync(SyncState state, string token, Func<Task> sending, CancellationToken cancel)
    {
        var findings = new LogFindings();
        var logs = new Dictionary<string, LogFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var log in Logs(state.GameFolder!))
        {
            Checkpoint();
            var file = new FileInfo(log);
            try
            {
                if (!state.Logs.TryGetValue(file.Name, out var known) || !known.Matches(file))
                {
                    var found = new LogFindings();
                    LogReader.ReadFrom(log, 0, found, Checkpoint);
                    file.Refresh();
                    known = LogFile.From(file, found);
                }
                logs[file.Name] = known;
                known.AddTo(findings);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        state.Logs = logs;
        if (findings.IsEmpty) return (0, 0);
        await sending();
        var blueprints = 0;
        foreach (var chunk in findings.Blueprints.Values.OrderBy(b => b.FirstSeen).Chunk(MaxBlueprintsPerSend))
            blueprints += await api.SendBlueprintsAsync(token, chunk, cancel);
        var completed = 0;
        foreach (var chunk in findings.Completed.Chunk(MaxCompletedPerSend))
            completed += await api.SendCompletedAsync(token, chunk.ToDictionary(c => c.Key, c => c.Value), cancel);
        return (blueprints, completed);
    }

    /// <summary>Keeps the schedule the player chose on the StarDad website; a new one counts from now.</summary>
    private static void TakeSchedule(LinkedAccount linked, SyncSchedule? schedule)
    {
        if (linked.Schedule == schedule) return;
        linked.Schedule = schedule;
        linked.ScheduleSince = schedule is null ? null : DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Asks stardad.space only for the schedule the player chose for this PC (after they saved it there); nothing
    /// is read or installed. Returns the schedule, or a message when it couldn't be asked.
    /// </summary>
    public async Task<(SyncSchedule? Schedule, string? Problem)> RefreshScheduleAsync(string? account, CancellationToken cancel)
    {
        var state = store.Load();
        account ??= state.LastAccount;
        if (store.Token(state, account) is not { } token) return (null, "This PC isn't linked to that StarDad account yet.");
        try
        {
            var status = await api.StatusAsync(token, cancel);
            TakeSchedule(state.Accounts[account!], status.Schedule);
            store.Save(state);
            return (status.Schedule, null);
        }
        catch (NotLinkedException) { return (null, "This PC was unlinked from that account."); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return (null, "Couldn't reach the StarDad website. Try again in a moment.");
        }
    }

    /// <summary>Linked accounts whose timed sync is due now, by the schedule each player chose.</summary>
    public IReadOnlyList<string> DueAccounts(DateTimeOffset now)
    {
        var due = new List<string>();
        var state = store.Load();
        if (!GameFolder.IsLive(state.GameFolder)) return due;
        foreach (var (id, linked) in state.Accounts)
        {
            if (linked.Schedule is not { } schedule || linked.ScheduleSince is not { } since) continue;
            var from = linked.LastSync is { } last && last > since ? last : since;
            if (schedule.IsDue(from, now)) due.Add(id);
        }
        return due;
    }

    /// <summary>Linked accounts that asked to sync when the game updates.</summary>
    public IReadOnlyList<string> OnGameUpdateAccounts() =>
        store.Load().Accounts.Where(a => a.Value.Schedule is { OnGameUpdate: true }).Select(a => a.Key).ToList();

    /// <summary>Game.log and every log in logbackups; nothing else (see <see cref="GameFolder.IsReadableLog"/>).</summary>
    public static IEnumerable<string> Logs(string live)
    {
        var current = Path.Combine(live, "Game.log");
        if (File.Exists(current)) yield return current;
        var backups = Path.Combine(live, "logbackups");
        if (!Directory.Exists(backups)) yield break;
        foreach (var log in Directory.EnumerateFiles(backups, "*.log").Where(log => GameFolder.IsReadableLog(live, log))) yield return log;
    }

    /// <summary>Best effort: a stage the website doesn't hear about never stops the sync.</summary>
    private async Task TellSiteAsync(string token, SyncStage stage, SyncReport? result, CancellationToken cancel, string? runId = null)
    {
        try { await api.ProgressAsync(token, stage.Name(), result?.Message, result is not null && Succeeded(result), cancel, runId); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or NotLinkedException) { }
    }

    /// <summary>The sync did what it could: installed, already current, or nothing to install yet.</summary>
    public static bool Succeeded(SyncReport report) =>
        (report.Completed || report.Installed) && !report.ConfirmationPending;

    /// <summary>Unlinks this PC from the account, on the StarDad website too (best effort), so its token stops working.</summary>
    public async Task<string> UnlinkAsync(string account, CancellationToken cancel)
    {
        var state = store.Load();
        var name = state.Accounts.TryGetValue(account, out var linked) ? linked.Name : "that account";
        if (store.Token(state, account) is { } token)
        {
            try { await api.UnlinkAsync(token, cancel); }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or NotLinkedException) { }
        }
        store.Unlink(state, account);
        store.Save(state);
        return $"Unlinked {name} from this PC.";
    }

    private SyncReport Done(SyncState state, SyncReport report)
    {
        state.LastSync = DateTimeOffset.UtcNow;
        if (state.LastAccount is { } account && state.Accounts.TryGetValue(account, out var linked)) linked.LastSync = state.LastSync;
        state.LastMessage = report.Message;
        store.Save(state);
        return report;
    }
}
