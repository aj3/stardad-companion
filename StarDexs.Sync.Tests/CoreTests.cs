using System.Text;
using StarDexs.Sync.Core;
using Xunit;

namespace StarDexs.Sync.Tests;

public class LogReaderTests
{
    private const string Blueprint = """<2026-04-02T10:11:12.345Z> [Notice] <SHUDEvent_OnNotification> Added notification "Received Blueprint: Arclight Pistol: " [12] to queue.""";
    private const string Done = """<2026-04-02T11:00:00.000Z> [Notice] <SHUDEvent_OnNotification> Added notification "Contract Complete: Clear the Outpost: " [7] to queue. MissionId: [0f8fad5b-d9cb-469f-a165-70867728950e]""";

    [Fact]
    public void Reads_blueprints_and_completed_contracts()
    {
        var found = new LogFindings();
        LogReader.ReadLine(Blueprint, found);
        LogReader.ReadLine(Done, found);
        Assert.Equal(new DateTimeOffset(2026, 4, 2, 10, 11, 12, 345, TimeSpan.Zero), found.Blueprints["Arclight Pistol"].FirstSeen);
        Assert.Equal("Clear the Outpost", found.Completed["0f8fad5b-d9cb-469f-a165-70867728950e"]);
    }

    [Fact]
    public void Ignores_blueprints_before_they_existed()
    {
        var found = new LogFindings();
        LogReader.ReadLine(Blueprint.Replace("2026-04-02", "2025-12-01"), found);
        Assert.True(found.IsEmpty);
    }

    [Fact]
    public void Keeps_the_earliest_date()
    {
        var found = new LogFindings();
        LogReader.ReadLine(Blueprint, found);
        LogReader.ReadLine(Blueprint.Replace("2026-04-02", "2026-03-15"), found);
        LogReader.ReadLine(Blueprint.Replace("2026-04-02", "2026-05-01"), found);
        Assert.Equal(15, found.Blueprints["Arclight Pistol"].FirstSeen.Day);
    }

    [Fact]
    public void Reads_on_from_where_it_stopped_and_leaves_a_half_line()
    {
        using var install = new FakeInstall();
        var log = Path.Combine(install.Live, "Game.log");
        File.WriteAllText(log, Blueprint + "\n" + Done[..40]);
        var first = new LogFindings();
        var offset = LogReader.ReadFrom(log, 0, first);
        Assert.Single(first.Blueprints);
        Assert.Empty(first.Completed);

        File.AppendAllText(log, Done[40..] + "\n");
        var second = new LogFindings();
        offset = LogReader.ReadFrom(log, offset, second);
        Assert.Empty(second.Blueprints);
        Assert.Single(second.Completed);
        Assert.Equal(new FileInfo(log).Length, offset);
    }

    [Fact]
    public void Starts_over_when_the_game_starts_a_new_log()
    {
        using var install = new FakeInstall();
        var log = Path.Combine(install.Live, "Game.log");
        File.WriteAllText(log, Blueprint + "\n");
        var found = new LogFindings();
        LogReader.ReadFrom(log, 1_000_000, found);
        Assert.Single(found.Blueprints);
    }
}

public class UserCfgTests
{
    [Fact]
    public void Adds_the_language_lines_to_a_missing_file() =>
        Assert.Equal("g_language = english\ng_languageAudio = english\n", UserCfg.WithLanguage(null));

    [Fact]
    public void Keeps_everything_else_and_the_newline_style()
    {
        var updated = UserCfg.WithLanguage("r_DisplayInfo = 1\r\n-- mine\r\ng_language = french\r\n");
        Assert.Equal("r_DisplayInfo = 1\r\n-- mine\r\ng_language = english\r\ng_languageAudio = english\r\n", updated);
    }

    [Fact]
    public void Leaves_a_file_that_already_has_them() =>
        Assert.Null(UserCfg.WithLanguage("g_language=english ; set by me\ng_languageAudio = English\n"));
}

public class GameFolderTests
{
    [Fact]
    public void Finds_the_newest_install_in_the_launcher_log()
    {
        var text = """
            {"t":"1","msg":"Launching C:\\Program Files\\Roberts Space Industries\\StarCitizen\\LIVE\\Bin64"}
            {"t":"2","msg":"Launching Z:\\RSI\\StarCitizen\\LIVE\\Bin64"}
            """.Replace(@"\\", @"\");
        Assert.Equal([@"Z:\RSI\StarCitizen\LIVE", @"C:\Program Files\Roberts Space Industries\StarCitizen\LIVE"], GameFolder.FromLauncherLog(text));
    }

    [Fact]
    public void Finds_LIVE_inside_whatever_folder_was_picked()
    {
        using var install = new FakeInstall();
        Assert.Equal(install.Live, GameFolder.ResolveLive(install.Live));
        Assert.Equal(install.Live, GameFolder.ResolveLive(install.Root)); // the folder above LIVE
        var rsi = Path.Combine(install.Root, "rsi");
        var deeper = Path.Combine(rsi, "StarCitizen", "LIVE");
        Directory.CreateDirectory(deeper);
        File.WriteAllBytes(Path.Combine(deeper, "Data.p4k"), [0]);
        Assert.Equal(deeper, GameFolder.ResolveLive(rsi));
        Assert.Null(GameFolder.ResolveLive(Path.Combine(install.Live, "nowhere")));
        Assert.Null(GameFolder.ResolveLive(null));
    }

    [Fact]
    public void A_folder_without_Data_p4k_is_not_the_game()
    {
        using var install = new FakeInstall();
        Assert.True(GameFolder.IsLive(install.Live));
        Assert.False(GameFolder.IsLive(install.Root));
        Assert.False(GameFolder.IsLive(null));
    }
}
