namespace StarDexs.Sync.Core;

/// <summary>What one pass did, for the tray's tooltip and notifications.</summary>
public sealed record SyncReport(
    string Message, bool Installed = false, bool NeedsPermission = false, bool NotLinked = false, bool WaitingForGame = false,
    bool UnknownFile = false, int NewBlueprints = 0, int NewCompleted = 0, string? HelperLatest = null);

/// <summary>Where a sync is up to, for the app's sync bar and the account page's.</summary>
public enum SyncStage { Reading, Sending, Checking, Downloading, Installing, Finished }

public static class SyncStages
{
    /// <summary>The name stardexs.com shows for a stage.</summary>
    public static string Name(this SyncStage stage) => stage.ToString().ToLowerInvariant();

    /// <summary>How full the sync bar is when a stage starts (0 to 1).</summary>
    public static double Share(this SyncStage stage) => (int)stage / (double)SyncStage.Finished;
}

/// <summary>
/// One sync for one linked account: read what's new in the logs and send it, then check whether that
/// account's file is current and install it if the player agreed. Nothing here runs on its own; the app
/// calls it when the player asks (from stardexs.com or the tray menu) or at the times they chose on the StarDad website.
/// </summary>
public sealed class SyncEngine(SyncApi api, StateStore store, InstallWriter writer)
{
    public const int MaxBlueprintsPerSend = 4000, MaxCompletedPerSend = 2000;

    /// <param name="account">The account the player pressed "Send to my PC" in; null: the last one used.</param>
    /// <param name="progress">Told as each stage starts. Each stage (and the result) also goes to stardexs.com, so the
    /// player's account page can show the sync as it happens; only the stage name and the result's message.</param>
    public async Task<SyncReport> RunAsync(string? account, CancellationToken cancel, IProgress<SyncStage>? progress = null)
    {
        var state = store.Load();
        account ??= state.LastAccount;
        var token = store.Token(state, account);
        var report = await RunLinkedAsync(state, account, token, progress, cancel);
        progress?.Report(SyncStage.Finished);
        if (token is not null && !report.NotLinked) await TellSiteAsync(token, SyncStage.Finished, report, cancel);
        return report;
    }

    private async Task<SyncReport> RunLinkedAsync(SyncState state, string? account, string? token, IProgress<SyncStage>? progress, CancellationToken cancel)
    {
        async Task Stage(SyncStage stage)
        {
            progress?.Report(stage);
            await TellSiteAsync(token!, stage, null, cancel);
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
            SyncReport Report(string message, bool installed = false, bool permission = false, bool waiting = false, bool unknown = false) =>
                new(message, installed, permission, false, waiting, unknown, blueprints, completed, status.HelperLatest);

            if (status.Patch is null) return Done(state, Report("StarDad has no file for this patch yet."));
            var current = status.Patch == state.InstalledPatch && status.Settings == state.InstalledSettings && account == state.InstalledAccount
                          && File.Exists(GameFolder.GlobalIniPath(state.GameFolder!));
            if (current) return Done(state, Report($"Up to date ({status.Patch})."));
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
                    return Done(state, Report(result.Message, installed: result.Outcome == WriteOutcome.Written));
                case WriteOutcome.NeedsPermission: return Done(state, Report(result.Message, permission: true));
                case WriteOutcome.GameRunning: return Done(state, Report(result.Message, waiting: true));
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
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
        {
            return Done(state, new SyncReport("The StarDad website took too long to answer. Try again in a moment."));
        }
    }

    /// <summary>Puts back the file installed before the current one, which stardexs.com keeps (the PC keeps no copies).</summary>
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
            WaitingForGame: result.Outcome == WriteOutcome.GameRunning, UnknownFile: result.Outcome == WriteOutcome.UnknownFile));
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
        state.InstalledAccount = account;
        state.InstalledPatch = patch;
        state.InstalledSettings = settings;
        state.InstalledSha = sha;
        state.ReplaceUnknownOnce = false;
    }

    /// <summary>
    /// Everything the logs hold, sent every time (stardexs.com keeps each once), so anything removed from the
    /// account comes back. Only logs that are new or changed since the last sync are read again.
    /// Returns how many the account didn't have yet.
    /// </summary>
    private async Task<(int Blueprints, int Completed)> SendLogsAsync(SyncState state, string token, Func<Task> sending, CancellationToken cancel)
    {
        var findings = new LogFindings();
        var logs = new Dictionary<string, LogFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var log in Logs(state.GameFolder!))
        {
            var file = new FileInfo(log);
            try
            {
                if (!state.Logs.TryGetValue(file.Name, out var known) || !known.Matches(file))
                {
                    var found = new LogFindings();
                    LogReader.ReadFrom(log, 0, found);
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
        var blueprints = findings.Blueprints.Count == 0 ? 0
            : await api.SendBlueprintsAsync(token, findings.Blueprints.Values.OrderBy(b => b.FirstSeen).Take(MaxBlueprintsPerSend), cancel);
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
    /// Asks stardexs.com only for the schedule the player chose for this PC (after they saved it there); nothing
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
        foreach (var (id, linked) in store.Load().Accounts)
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
    private async Task TellSiteAsync(string token, SyncStage stage, SyncReport? result, CancellationToken cancel)
    {
        try { await api.ProgressAsync(token, stage.Name(), result?.Message, result is not null && Succeeded(result), cancel); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or NotLinkedException) { }
    }

    /// <summary>The sync did what it could: installed, already current, or nothing to install yet.</summary>
    public static bool Succeeded(SyncReport report) =>
        !report.NotLinked && !report.NeedsPermission && !report.WaitingForGame && !report.UnknownFile
        && !report.Message.StartsWith("Couldn't", StringComparison.Ordinal) && !report.Message.StartsWith("Choose", StringComparison.Ordinal) && !report.Message.Contains("took too long", StringComparison.Ordinal);

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
