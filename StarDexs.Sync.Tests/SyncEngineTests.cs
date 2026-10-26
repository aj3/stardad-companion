using System.Text;
using StarDexs.Sync.Core;
using Xunit;

namespace StarDexs.Sync.Tests;

public class SyncEngineTests
{
    private const string Blueprint = """<2026-04-02T10:11:12.345Z> [Notice] <SHUDEvent_OnNotification> Added notification "Received Blueprint: Arclight Pistol: " [12] to queue.""";
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
    public async Task Sends_new_log_findings_once_and_installs_the_file()
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
        Assert.DoesNotContain(setup.Site.Requests, r => r.Path is "/api/sync/blueprints" or "/api/sync/file");
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
    public async Task Each_linked_account_gets_the_logs_once_and_its_own_file()
    {
        using var setup = new Setup();
        var state = setup.Store.Load();
        setup.Store.Link(state, Two, "Pilot Two", "t-456");
        setup.Store.Save(state);
        setup.Install.Log("Game.log", Blueprint + "\n");

        Assert.Equal(1, (await setup.Engine.RunAsync(One, CancellationToken.None)).NewBlueprints);
        Assert.Equal(1, (await setup.Engine.RunAsync(Two, CancellationToken.None)).NewBlueprints);
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

        Assert.Equal("Took your StarDexs file out of the game. It shows the game's own text again.", setup.Engine.Remove().Message);
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
    public async Task Keeps_log_positions_when_stardexs_com_is_out_of_reach()
    {
        using var setup = new Setup();
        setup.Install.Log("Game.log", Blueprint + "\n");
        await setup.EngineFor(new Unreachable()).RunAsync(One, CancellationToken.None);
        Assert.Empty(setup.Store.Load().Accounts[One].LogOffsets);

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
