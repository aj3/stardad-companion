using System.Text;
using StarDexs.Sync.Core;
using Xunit;

namespace StarDexs.Sync.Tests;

public class SyncEngineTests
{
    [Fact]
    public async Task Completion_is_retried_and_kept_until_the_website_confirms_it()
    {
        using var setup = new Setup();
        setup.Site.ConfirmationFailures = 3;
        var report = await setup.Engine.RunAsync(One, CancellationToken.None);
        Assert.True(report.Installed);
        Assert.True(report.ConfirmationPending);
        Assert.False(SyncEngine.Succeeded(report));
        var pending = setup.Store.Load().Accounts[One].PendingResult;
        Assert.NotNull(pending);
        var completions = setup.Site.Requests.Where(r => r.Path == "/api/sync/progress" && r.Body.Contains("\"stage\":\"finished\"")).ToList();
        Assert.Equal(3, completions.Count);
        Assert.All(completions, r => Assert.Contains(pending.RunId, r.Body));
        report = await setup.Engine.RunAsync(One, CancellationToken.None);
        Assert.True(SyncEngine.Succeeded(report));
        Assert.Null(setup.Store.Load().Accounts[One].PendingResult);
    }

    [Theory]
    [InlineData("silent", true, false)]
    [InlineData("silent", false, false)]
    [InlineData("attention", true, false)]
    [InlineData("attention", false, true)]
    [InlineData("all", true, true)]
    [InlineData("all", false, true)]
    public void Notification_preference_never_changes_sync_success(string mode, bool completed, bool notify)
    {
        var report = new SyncReport("A result", Completed: completed);
        Assert.Equal(notify, SyncNotifications.ShouldNotify(mode, report));
    }

    [Fact]
    public async Task Having_no_patch_is_not_a_successful_file_sync()
    {
        using var setup = new Setup(); setup.Site.Patch = null;
        Assert.False(SyncEngine.Succeeded(await setup.Engine.RunAsync(One, CancellationToken.None)));
    }

    [Fact]
    public async Task Changed_installed_file_is_not_falsely_confirmed_as_current()
    {
        using var setup = new Setup();
        await setup.Engine.RunAsync(One, CancellationToken.None);
        File.WriteAllText(GameFolder.GlobalIniPath(setup.Install.Live), "key=changed by the player\n");
        var report = await setup.Engine.RunAsync(One, CancellationToken.None);
        Assert.True(report.UnknownFile);
        Assert.False(SyncEngine.Succeeded(report));
        Assert.Contains("changed by the player", File.ReadAllText(GameFolder.GlobalIniPath(setup.Install.Live)));
    }
    private const string Blueprint = """<2026-04-02T10:11:12.345Z> [Notice] <SHUDEvent_OnNotification> Added notification "Received Blueprint: Arclight Pistol: " [12] to queue.""";
    private const string Completed = """<2026-04-02T11:00:00.000Z> [Notice] <SHUDEvent_OnNotification> Added notification "Contract Complete: Clear the Area: " [13] to queue. MissionId: [0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0]""";
    private const string One = "aaaaaaaaaaaaaaaa", Two = "bbbbbbbbbbbbbbbb";

    private sealed class Setup : IDisposable
    {
        public FakeInstall Install { get; } = new();
        public FakeSite Site { get; } = new();
        public StateStore Store { get; }
        public SyncEngine Engine { get; }
        public bool GameRunning { get; set; }

        public Setup(bool linked = true, bool agreed = true)
        {
            Store = new StateStore(Install.Data, new FakeProtector());
            var state = Store.Load();
            if (linked) Store.Link(state, One, "Pilot One", "t-123");
            state.GameFolder = Install.Live;
            state.InstallAgreed = agreed;
            Store.Save(state);
            Engine = EngineFor(Site);
        }

        public SyncEngine EngineFor(HttpMessageHandler site) =>
            new(new SyncApi(new HttpClient(site) { BaseAddress = new Uri("https://stardexs.test/") }), Store, new InstallWriter(Install.Data, () => GameRunning));

        public void Dispose() => Install.Dispose();
    }

    [Fact]
    public async Task Sends_log_findings_and_installs_the_file()
    {
        using var setup = new Setup();
        setup.Install.Log("Game.log", Blueprint + "\n");

        var report = await setup.Engine.RunAsync(One, CancellationToken.None);

        Assert.True(report.Installed);
        Assert.Equal(1, report.NewBlueprints);
        Assert.Equal("0.2.0", report.HelperLatest);
        Assert.Equal(setup.Site.File, File.ReadAllBytes(GameFolder.GlobalIniPath(setup.Install.Live)));
        Assert.All(setup.Site.Requests, request => Assert.Equal("t-123", request.Auth));
        // The website is told exactly which file went in, so it can keep it as the backup.
        Assert.Contains(setup.Site.Requests, r => r.Path == "/api/sync/installed" && r.Body.Contains(InstallWriter.Sha256(setup.Site.File)));

        setup.Site.Requests.Clear();
        report = await setup.Engine.RunAsync(null, CancellationToken.None); // "Sync" from the tray: the last account
        Assert.False(report.Installed);
        Assert.Equal(0, report.NewBlueprints);
        Assert.DoesNotContain(setup.Site.Requests, r => r.Path == "/api/sync/file");
    }

    [Fact]
    public async Task Every_sync_sends_everything_so_what_was_removed_on_the_website_comes_back()
    {
        using var setup = new Setup();
        setup.Install.Log("logbackups/Game Build(1) 01.log", Blueprint + "\n" + Completed + "\n");
        await setup.Engine.RunAsync(One, CancellationToken.None);
        Assert.Single(setup.Site.Owned);

        setup.Site.Owned.Clear(); // the player pressed Forget on the StarDad website
        setup.Site.Completed.Clear();
        var report = await setup.Engine.RunAsync(One, CancellationToken.None);

        Assert.Equal(1, report.NewBlueprints);
        Assert.Equal(1, report.NewCompleted);
        Assert.Contains("Arclight Pistol", setup.Site.Owned);
    }

    [Fact]
    public async Task A_log_is_read_again_only_when_it_changes()
    {
        using var setup = new Setup();
        setup.Install.Log("logbackups/old.log", Blueprint + "\n");
        await setup.Engine.RunAsync(One, CancellationToken.None);
        var kept = setup.Store.Load().Logs["old.log"];
        Assert.Single(kept.Blueprints);

        // Same size and time: what was kept is used, not the file (proved by changing the kept copy).
        var state = setup.Store.Load();
        state.Logs["old.log"].Blueprints[0] = new Blueprint("Kept Copy", kept.Blueprints[0].FirstSeen);
        setup.Store.Save(state);
        await setup.Engine.RunAsync(One, CancellationToken.None);
        Assert.Contains("Kept Copy", setup.Site.Owned);

        // A changed log is read again; a deleted one is forgotten.
        setup.Install.Log("logbackups/old.log", Blueprint + "\n" + Blueprint.Replace("Arclight Pistol", "Karna Rifle") + "\n");
        File.SetLastWriteTimeUtc(Path.Combine(setup.Install.Live, "logbackups", "old.log"), DateTime.UtcNow.AddMinutes(1));
        await setup.Engine.RunAsync(One, CancellationToken.None);
        Assert.Contains("Karna Rifle", setup.Site.Owned);
        File.Delete(Path.Combine(setup.Install.Live, "logbackups", "old.log"));
        await setup.Engine.RunAsync(One, CancellationToken.None);
        Assert.Empty(setup.Store.Load().Logs);
    }

    [Fact]
    public async Task Takes_the_schedule_the_player_chose_and_runs_it_only_when_due()
    {
        using var setup = new Setup();
        Assert.Empty(setup.Engine.DueAccounts(DateTimeOffset.UtcNow.AddHours(2.9)));
        Assert.Equal([One], setup.Engine.DueAccounts(DateTimeOffset.UtcNow.AddHours(3.1))); // new-link default

        setup.Site.Schedule = """{"every_hours":6,"at_minute":null,"on_game_update":true}""";
        var (schedule, problem) = await setup.Engine.RefreshScheduleAsync(One, CancellationToken.None);
        Assert.Null(problem);
        Assert.Equal(new SyncSchedule(6, null, true), schedule);
        Assert.DoesNotContain(setup.Site.Requests, r => r.Path is "/api/sync/file" or "/api/sync/blueprints"); // only asked
        Assert.Equal([One], setup.Engine.OnGameUpdateAccounts());

        Assert.Empty(setup.Engine.DueAccounts(DateTimeOffset.UtcNow.AddHours(5)));
        Assert.Equal([One], setup.Engine.DueAccounts(DateTimeOffset.UtcNow.AddHours(6.1)));
        await setup.Engine.RunAsync(One, CancellationToken.None); // a sync counts as the timed one
        Assert.Empty(setup.Engine.DueAccounts(DateTimeOffset.UtcNow.AddHours(5)));

        setup.Site.Schedule = "null"; // turned off on the website: the next contact takes it away
        await setup.Engine.RunAsync(One, CancellationToken.None);
        Assert.Null(setup.Store.Load().Accounts[One].Schedule);
        Assert.Empty(setup.Engine.DueAccounts(DateTimeOffset.UtcNow.AddDays(30)));
    }

    [Fact]
    public async Task A_schedule_the_app_doesnt_know_is_treated_as_off()
    {
        using var setup = new Setup();
        setup.Site.Schedule = """{"every_hours":1,"at_minute":null,"on_game_update":false}""";
        await setup.Engine.RunAsync(One, CancellationToken.None);
        Assert.Null(setup.Store.Load().Accounts[One].Schedule);
    }

    [Fact]
    public async Task Reports_each_stage_to_the_app_and_the_account_page()
    {
        using var setup = new Setup();
        setup.Install.Log("Game.log", Blueprint + "\n");
        var stages = new List<SyncStage>();
        await setup.Engine.RunAsync(One, CancellationToken.None, new Recorder(stages));

        Assert.Equal([SyncStage.Reading, SyncStage.Sending, SyncStage.Checking, SyncStage.Downloading, SyncStage.Installing, SyncStage.Finished], stages);
        var told = setup.Site.Requests.Where(r => r.Path == "/api/sync/progress").Select(r => r.Body).ToList();
        Assert.Equal(6, told.Count);
        Assert.Contains("\"stage\":\"finished\"", told[^1]);
        Assert.Contains("\"ok\":true", told[^1]);
        Assert.Contains("Installed your file", told[^1]);
    }

    [Fact]
    public async Task Unlinking_also_stops_the_token_on_the_website()
    {
        using var setup = new Setup();
        Assert.Equal("Unlinked Pilot One from this PC.", await setup.Engine.UnlinkAsync(One, CancellationToken.None));
        Assert.Contains(setup.Site.Requests, r => r.Path == "/api/sync/unlink" && r.Auth == "t-123");
        Assert.Empty(setup.Store.Load().Accounts);

        // Unreachable website: still unlinked here.
        var state = setup.Store.Load();
        setup.Store.Link(state, Two, "Pilot Two", "t-456");
        setup.Store.Save(state);
        await setup.EngineFor(new Unreachable()).UnlinkAsync(Two, CancellationToken.None);
        Assert.Empty(setup.Store.Load().Accounts);
    }

    // Records stages as they're reported (Progress<T> would post them later, on another thread).
    private sealed class Recorder(List<SyncStage> stages) : IProgress<SyncStage>
    {
        public void Report(SyncStage value) => stages.Add(value);
    }

    [Fact]
    public async Task Sends_only_names_and_dates_never_log_text()
    {
        using var setup = new Setup();
        setup.Install.Log("Game.log", "<2026-04-02T10:00:00Z> Player handle SomePilot joined\n" + Blueprint + "\n");
        await setup.Engine.RunAsync(One, CancellationToken.None);
        var sent = Assert.Single(setup.Site.Requests, r => r.Path == "/api/sync/blueprints");
        Assert.Contains("Arclight Pistol", sent.Body);
        Assert.DoesNotContain("SomePilot", string.Concat(setup.Site.Requests.Select(r => r.Body)));
    }

    [Fact]
    public async Task Reads_only_Game_log_and_logbackups()
    {
        using var setup = new Setup();
        setup.Install.Log(@"logbackups\Game Build(1) old.log", Blueprint + "\n");
        setup.Install.Log(@"logs\other.log", Blueprint.Replace("Arclight Pistol", "Secret Thing") + "\n");
        setup.Install.Log(@"logbackups\notes.txt", Blueprint.Replace("Arclight Pistol", "Not A Log") + "\n");
        await setup.Engine.RunAsync(One, CancellationToken.None);
        var sent = Assert.Single(setup.Site.Requests, r => r.Path == "/api/sync/blueprints").Body;
        Assert.Contains("Arclight Pistol", sent);
        Assert.DoesNotContain("Secret Thing", sent);
        Assert.DoesNotContain("Not A Log", sent);
    }

    [Fact]
    public async Task Each_linked_account_gets_the_logs_and_its_own_file()
    {
        using var setup = new Setup();
        var state = setup.Store.Load();
        setup.Store.Link(state, Two, "Pilot Two", "t-456");
        setup.Store.Save(state);
        setup.Install.Log("Game.log", Blueprint + "\n");

        await setup.Engine.RunAsync(One, CancellationToken.None);
        await setup.Engine.RunAsync(Two, CancellationToken.None);
        Assert.Contains(setup.Site.Requests, r => r.Path == "/api/sync/blueprints" && r.Auth == "t-123");
        Assert.Contains(setup.Site.Requests, r => r.Path == "/api/sync/blueprints" && r.Auth == "t-456");
        // Switching accounts puts the other account's file in, even on the same patch.
        Assert.Contains(setup.Site.Requests, r => r.Path == "/api/sync/file" && r.Auth == "t-456");
        Assert.Equal(Two, setup.Store.Load().InstalledAccount);
    }

    [Fact]
    public async Task A_link_for_an_account_not_on_this_PC_does_nothing_online()
    {
        using var setup = new Setup();
        var report = await setup.Engine.RunAsync(Two, CancellationToken.None);
        Assert.True(report.NotLinked);
        Assert.Empty(setup.Site.Requests);
    }

    [Fact]
    public async Task Never_writes_without_the_players_agreement()
    {
        using var setup = new Setup(agreed: false);
        var report = await setup.Engine.RunAsync(One, CancellationToken.None);
        Assert.False(report.Installed);
        Assert.False(File.Exists(GameFolder.GlobalIniPath(setup.Install.Live)));
        Assert.DoesNotContain(setup.Site.Requests, r => r.Path == "/api/sync/file");
    }

    [Fact]
    public async Task Waits_for_the_game_to_close()
    {
        using var setup = new Setup { GameRunning = true };
        var report = await setup.Engine.RunAsync(One, CancellationToken.None);
        Assert.True(report.WaitingForGame);
        Assert.False(File.Exists(GameFolder.GlobalIniPath(setup.Install.Live)));

        setup.GameRunning = false;
        Assert.True((await setup.Engine.RunAsync(One, CancellationToken.None)).Installed);
    }

    [Fact]
    public async Task Asks_once_before_replacing_someone_elses_global_ini()
    {
        using var setup = new Setup();
        Directory.CreateDirectory(Path.GetDirectoryName(GameFolder.GlobalIniPath(setup.Install.Live))!);
        File.WriteAllText(GameFolder.GlobalIniPath(setup.Install.Live), "other=tool\n");

        Assert.True((await setup.Engine.RunAsync(One, CancellationToken.None)).UnknownFile);
        var state = setup.Store.Load();
        state.ReplaceUnknownOnce = true;
        setup.Store.Save(state);
        Assert.True((await setup.Engine.RunAsync(One, CancellationToken.None)).Installed);
        Assert.False(setup.Store.Load().ReplaceUnknownOnce);
    }

    [Fact]
    public async Task Puts_back_the_previous_file_from_the_website_and_removes_only_its_own()
    {
        using var setup = new Setup();
        Assert.False((await setup.Engine.PutBackPreviousAsync(CancellationToken.None)).Installed); // nothing installed yet
        await setup.Engine.RunAsync(One, CancellationToken.None);
        Assert.Contains("no earlier file", (await setup.Engine.PutBackPreviousAsync(CancellationToken.None)).Message);

        setup.Site.Previous = Encoding.UTF8.GetBytes("key=Before\n");
        Assert.True((await setup.Engine.PutBackPreviousAsync(CancellationToken.None)).Installed);
        Assert.Equal("key=Before\n", File.ReadAllText(GameFolder.GlobalIniPath(setup.Install.Live)));
        // The next sync installs the current file again.
        Assert.True((await setup.Engine.RunAsync(One, CancellationToken.None)).Installed);

        Assert.Equal("Took your StarDad file out of the game. Restart the game to see its own text again.", setup.Engine.Remove().Message);
        Assert.False(File.Exists(GameFolder.GlobalIniPath(setup.Install.Live)));
    }

    [Fact]
    public async Task Notices_a_game_update_on_this_PC_once()
    {
        using var setup = new Setup();
        await setup.Engine.RunAsync(One, CancellationToken.None); // installs 4.10.2-live.1
        File.WriteAllText(GameFolder.ManifestPath(setup.Install.Live), """{"Data":{"RequestedP4ChangeNum":"1"}}""");
        Assert.Null(setup.Engine.GameUpdated()); // the file is for this build

        setup.Site.Requests.Clear();
        File.WriteAllText(GameFolder.ManifestPath(setup.Install.Live), """{"Data":{"RequestedP4ChangeNum":"2"}}""");
        Assert.Equal("2", setup.Engine.GameUpdated());
        Assert.Null(setup.Engine.GameUpdated()); // said once
        Assert.Empty(setup.Site.Requests);        // nothing online
    }

    [Fact]
    public async Task Forgets_an_account_the_website_unlinked()
    {
        using var setup = new Setup { Site = { Unlinked = true } };
        var report = await setup.Engine.RunAsync(One, CancellationToken.None);
        Assert.True(report.NotLinked);
        Assert.Empty(setup.Store.Load().Accounts);
    }

    [Fact]
    public async Task Sends_everything_once_stardexs_com_is_back_in_reach()
    {
        using var setup = new Setup();
        setup.Install.Log("Game.log", Blueprint + "\n");
        await setup.EngineFor(new Unreachable()).RunAsync(One, CancellationToken.None);

        Assert.Equal(1, (await setup.Engine.RunAsync(One, CancellationToken.None)).NewBlueprints);
    }

    [Fact]
    public async Task Linking_names_the_account()
    {
        var api = new SyncApi(new HttpClient(new FakeSite()) { BaseAddress = new Uri("https://stardexs.test/") });
        var start = await api.StartLinkAsync("PC", CancellationToken.None);
        Assert.Equal("https://stardexs.test/link?code=ABCD-1234", start.LinkPage.AbsoluteUri);
        Assert.Equal(new LinkReady("t-123", One, "Pilot One"), await api.PollLinkAsync(start.Poll, CancellationToken.None));
    }

    [Theory]
    [InlineData("4.10.2-live.12913470", "12913470")]
    [InlineData("4.10.2", "2")]
    [InlineData("live", null)]
    [InlineData(null, null)]
    public void Reads_the_build_from_a_patch_name(string? patch, string? build) => Assert.Equal(build, GameFolder.PatchBuild(patch));

    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            throw new HttpRequestException("offline");
    }
}
