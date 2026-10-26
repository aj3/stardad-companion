using System.Text;
using StarDexs.Sync.Core;
using Xunit;

namespace StarDexs.Sync.Tests;

public class InstallWriterTests
{
    private static readonly byte[] Ini = Encoding.UTF8.GetBytes("a=1\n");

    [Fact]
    public void Installs_without_keeping_copies_on_the_PC()
    {
        using var install = new FakeInstall();
        File.WriteAllText(GameFolder.UserCfgPath(install.Live), "r_DisplayInfo = 1\n");
        var writer = new InstallWriter(install.Data, () => false);

        var result = writer.Install(install.Live, Ini, "4.10", ours: null);

        Assert.Equal(WriteOutcome.Written, result.Outcome);
        Assert.Equal(InstallWriter.Sha256(Ini), result.Sha256);
        Assert.Equal(Ini, File.ReadAllBytes(GameFolder.GlobalIniPath(install.Live)));
        Assert.Equal("r_DisplayInfo = 1\ng_language = english\ng_languageAudio = english\n", File.ReadAllText(GameFolder.UserCfgPath(install.Live)));
        Assert.Contains("installed 4.10", File.ReadAllText(writer.JournalPath));
        // Only the journal is kept: no backup folder, no temporary files left in the game.
        Assert.Equal([writer.JournalPath], Directory.GetFiles(install.Data, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(install.Live, "*.stardexs-tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Writes_only_inside_the_language_folder_and_user_cfg()
    {
        using var install = new FakeInstall();
        var before = Directory.GetFiles(install.Live, "*", SearchOption.AllDirectories).ToHashSet();
        new InstallWriter(install.Data, () => false).Install(install.Live, Ini, "4.10", ours: null);
        var added = Directory.GetFiles(install.Live, "*", SearchOption.AllDirectories).Where(path => !before.Contains(path));
        Assert.Equal([GameFolder.GlobalIniPath(install.Live), GameFolder.UserCfgPath(install.Live)], added.Order());
    }

    [Theory]
    [InlineData(@"Data.p4k")]
    [InlineData(@"Bin64\StarCitizen.exe")]
    [InlineData(@"data\Localization\english\other.ini")]
    [InlineData(@"..\elsewhere.txt")]
    public void Refuses_any_other_path(string relative)
    {
        using var install = new FakeInstall();
        Assert.Throws<OutsideGameFilesException>(() => GameFolder.CheckWritable(install.Live, Path.Combine(install.Live, relative)));
    }

    [Fact]
    public void Refuses_a_language_folder_that_leads_somewhere_else()
    {
        using var install = new FakeInstall();
        var elsewhere = Directory.CreateDirectory(Path.Combine(install.Root, "elsewhere")).FullName;
        Directory.CreateDirectory(Path.Combine(install.Live, "data", "Localization"));
        try { Directory.CreateSymbolicLink(Path.Combine(install.Live, GameFolder.LocalizationFolder), elsewhere); }
        catch (IOException) { return; } // this Windows account can't make links; nothing to check
        catch (UnauthorizedAccessException) { return; }
        var result = new InstallWriter(install.Data, () => false).Install(install.Live, Ini, "4.10", ours: null);
        Assert.Equal(WriteOutcome.Failed, result.Outcome);
        Assert.Empty(Directory.GetFiles(elsewhere));
    }

    [Fact]
    public void Asks_before_replacing_a_global_ini_it_did_not_install()
    {
        using var install = new FakeInstall();
        Directory.CreateDirectory(Path.GetDirectoryName(GameFolder.GlobalIniPath(install.Live))!);
        File.WriteAllText(GameFolder.GlobalIniPath(install.Live), "someone=else\n");
        var writer = new InstallWriter(install.Data, () => false);

        Assert.Equal(WriteOutcome.UnknownFile, writer.Install(install.Live, Ini, "4.10", ours: null).Outcome);
        Assert.Equal("someone=else\n", File.ReadAllText(GameFolder.GlobalIniPath(install.Live)));
        Assert.Equal(WriteOutcome.Written, writer.Install(install.Live, Ini, "4.10", ours: null, replaceUnknown: true).Outcome);
    }

    [Fact]
    public void Replaces_its_own_file_freely()
    {
        using var install = new FakeInstall();
        var writer = new InstallWriter(install.Data, () => false);
        var first = writer.Install(install.Live, Ini, "4.10", ours: null);
        Assert.Equal(WriteOutcome.Written, writer.Install(install.Live, Encoding.UTF8.GetBytes("a=2\n"), "4.11", ours: first.Sha256).Outcome);
    }

    [Fact]
    public void Does_nothing_when_the_file_is_already_there()
    {
        using var install = new FakeInstall();
        var writer = new InstallWriter(install.Data, () => false);
        var first = writer.Install(install.Live, Ini, "4.10", ours: null);
        Assert.Equal(WriteOutcome.AlreadyCurrent, writer.Install(install.Live, Ini, "4.10", ours: first.Sha256).Outcome);
    }

    [Fact]
    public void Waits_while_the_game_is_running()
    {
        using var install = new FakeInstall();
        var result = new InstallWriter(install.Data, () => true).Install(install.Live, Ini, "4.10", ours: null);
        Assert.Equal(WriteOutcome.GameRunning, result.Outcome);
        Assert.False(File.Exists(GameFolder.GlobalIniPath(install.Live)));
    }

    [Theory]
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00 })]             // a program, not text
    [InlineData(new byte[] { 0x61, 0x3D, 0xC3, 0x28 })]             // broken UTF-8
    [InlineData(new byte[] { 0x3C, 0x68, 0x74, 0x6D, 0x6C, 0x3E })] // "<html>": an error page
    [InlineData(new byte[0])]
    public void Refuses_anything_but_game_text(byte[] content)
    {
        using var install = new FakeInstall();
        var result = new InstallWriter(install.Data, () => false).Install(install.Live, content, "4.10", ours: null);
        Assert.Equal(WriteOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(GameFolder.GlobalIniPath(install.Live)));
    }

    private const string Text = "; note\r\nkey,P=Value = x\r\n\r\nb=\n";

    [Fact]
    public void Accepts_game_text_with_a_byte_order_mark_and_comments() =>
        Assert.True(InstallWriter.LooksLikeGlobalIni(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Text)).ToArray()));

    [Fact]
    public void Refuses_a_folder_that_is_not_the_game()
    {
        using var install = new FakeInstall();
        Assert.Equal(WriteOutcome.Failed, new InstallWriter(install.Data, () => false).Install(install.Root, Ini, "4.10", ours: null).Outcome);
    }

    [Fact]
    public void Removes_only_its_own_file()
    {
        using var install = new FakeInstall();
        var writer = new InstallWriter(install.Data, () => false);
        var installed = writer.Install(install.Live, Ini, "4.10", ours: null);

        Assert.Equal(WriteOutcome.UnknownFile, writer.Remove(install.Live, ours: "not-this-one").Outcome);
        Assert.True(File.Exists(GameFolder.GlobalIniPath(install.Live)));
        Assert.Equal(WriteOutcome.Written, writer.Remove(install.Live, installed.Sha256).Outcome);
        Assert.False(File.Exists(GameFolder.GlobalIniPath(install.Live)));
        Assert.True(File.Exists(GameFolder.UserCfgPath(install.Live)));
    }

    [Fact]
    public void Keeps_the_journal_short()
    {
        using var install = new FakeInstall();
        var writer = new InstallWriter(install.Data, () => false);
        string? ours = null;
        for (var i = 0; i < InstallWriter.JournalLinesKept + 20; i++)
            ours = writer.Install(install.Live, Encoding.UTF8.GetBytes($"a={i}\n"), "4.10", ours).Sha256;
        Assert.Equal(InstallWriter.JournalLinesKept, File.ReadAllLines(writer.JournalPath).Length);
    }
}

public class StateStoreTests
{
    [Fact]
    public void Keeps_each_accounts_token_protected_and_survives_a_round_trip()
    {
        using var install = new FakeInstall();
        var store = new StateStore(install.Data, new FakeProtector());
        var state = store.Load();
        store.Link(state, "aaaaaaaaaaaaaaaa", "Pilot One", "token-one");
        store.Link(state, "bbbbbbbbbbbbbbbb", "Pilot Two", "token-two");
        state.Accounts["aaaaaaaaaaaaaaaa"].LogOffsets["Game.log"] = 42;
        store.Save(state);

        Assert.DoesNotContain("token-one", File.ReadAllText(store.Path));
        var loaded = store.Load();
        Assert.Equal("token-one", store.Token(loaded, "aaaaaaaaaaaaaaaa"));
        Assert.Equal("token-two", store.Token(loaded, "bbbbbbbbbbbbbbbb"));
        Assert.Equal("bbbbbbbbbbbbbbbb", loaded.LastAccount);
        Assert.Equal(42, loaded.Accounts["aaaaaaaaaaaaaaaa"].LogOffsets["game.LOG"]);

        store.Unlink(loaded, "bbbbbbbbbbbbbbbb");
        Assert.Null(store.Token(loaded, "bbbbbbbbbbbbbbbb"));
        Assert.Equal("aaaaaaaaaaaaaaaa", loaded.LastAccount);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"Version":1,"ProtectedToken":"p:dA=="}""")] // the unreleased first format
    public void A_damaged_or_older_state_file_starts_fresh(string text)
    {
        using var install = new FakeInstall();
        Directory.CreateDirectory(install.Data);
        var store = new StateStore(install.Data, new FakeProtector());
        File.WriteAllText(store.Path, text);
        Assert.Empty(store.Load().Accounts);
    }
}
