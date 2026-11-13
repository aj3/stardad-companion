using StarDexs.Sync.Core;
using Xunit;

namespace StarDexs.Sync.Tests;

/// <summary>Custom character files, in a synthetic LIVE folder (a marker Data.p4k, made-up bytes for .chf files).</summary>
public sealed class CharacterFileTests : IDisposable
{
    private readonly string live = Directory.CreateTempSubdirectory("stardad-character-fixture-").FullName;
    private string Folder => CharacterFiles.Folder(live);
    private static byte[] Bytes(byte fill, int length = 4096) => Enumerable.Repeat(fill, length).ToArray();

    public CharacterFileTests() => File.WriteAllText(Path.Combine(live, "Data.p4k"), "synthetic marker");
    public void Dispose() => Directory.Delete(live, true);

    [Fact]
    public void A_missing_folder_means_no_characters()
    {
        var (names, skipped) = CharacterFiles.List(live);
        Assert.Empty(names); Assert.Equal(0, skipped);
    }

    [Fact]
    public void Only_chf_files_of_a_sensible_size_are_listed()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(Path.Combine(Folder, "Pilot One.chf"), Bytes(1));
        File.WriteAllBytes(Path.Combine(Folder, "pilot two.CHF"), Bytes(2));
        File.WriteAllBytes(Path.Combine(Folder, "empty.chf"), []);
        File.WriteAllBytes(Path.Combine(Folder, "huge.chf"), Bytes(3, CharacterFiles.MaxBytes + 1));
        File.WriteAllBytes(Path.Combine(Folder, "notes.txt"), Bytes(4, 10));
        var (names, skipped) = CharacterFiles.List(live);
        Assert.Equal(["Pilot One.chf", "pilot two.CHF"], names);
        Assert.Equal(2, skipped);
        Assert.Equal(Bytes(1), CharacterFiles.Read(live, "Pilot One.chf"));
    }

    [Fact]
    public void Names_that_could_leave_the_folder_are_refused()
    {
        foreach (var name in new[] { "..\\evil.chf", "a/b.chf", ".hidden.chf", "x.chf.exe", "con:.chf", "a..b.chf" })
            Assert.False(CharacterFiles.ValidName(name), name);
        Assert.True(CharacterFiles.ValidName("Gilly Mk II.chf"));
        Assert.Throws<InvalidDataException>(() => CharacterFiles.AddMissing(live, "..\\evil.chf", Bytes(1), CharacterFiles.Hash(Bytes(1)), () => false));
    }

    [Fact]
    public void Restore_adds_a_missing_character_and_never_replaces_one_that_is_there()
    {
        var data = Bytes(7); var hash = CharacterFiles.Hash(data);
        Assert.True(CharacterFiles.AddMissing(live, "Back Again.chf", data, hash, () => false));
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(Folder, "Back Again.chf")));

        var mine = Bytes(9);
        File.WriteAllBytes(Path.Combine(Folder, "Mine.chf"), mine);
        Assert.False(CharacterFiles.AddMissing(live, "Mine.chf", data, hash, () => false));
        Assert.Equal(mine, File.ReadAllBytes(Path.Combine(Folder, "Mine.chf")));
        Assert.Empty(Directory.GetFiles(Folder, "*.stardad-tmp"));
    }

    [Fact]
    public void Restore_waits_for_the_game_and_checks_the_fingerprint()
    {
        var data = Bytes(5);
        Assert.Throws<IOException>(() => CharacterFiles.AddMissing(live, "Later.chf", data, CharacterFiles.Hash(data), () => true));
        Assert.Throws<InvalidDataException>(() => CharacterFiles.AddMissing(live, "Later.chf", data, "not-the-hash", () => false));
        Assert.False(File.Exists(Path.Combine(Folder, "Later.chf")));
    }

    [Fact]
    public void Only_a_game_folder_is_used()
    {
        var elsewhere = Directory.CreateTempSubdirectory("stardad-not-live-").FullName;
        try { Assert.Throws<InvalidDataException>(() => CharacterFiles.List(elsewhere)); }
        finally { Directory.Delete(elsewhere, true); }
    }
}
