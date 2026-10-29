using System.Text;
using StarDexs.Sync.Core;
using Xunit;

namespace StarDexs.Sync.Tests;

public sealed class KeybindFileTests : IDisposable
{
    private readonly string live=Directory.CreateTempSubdirectory("stardad-keybind-fixture-").FullName;
    private const string Name="layout_fixture_exported.xml";
    private static byte[] Xml(int value)=>Encoding.UTF8.GetBytes($"<ActionMaps><actionmap name='fixture'><action name='test'><rebind input='js1_button{value}'/></action></actionmap></ActionMaps>");
    public KeybindFileTests()=>File.WriteAllText(Path.Combine(live,"Data.p4k"),"synthetic marker");
    public void Dispose()=>Directory.Delete(live,true);
    [Fact]
    public void Restore_requires_separate_consent_closed_game_and_matching_hash()
    {
        var data=Xml(1);var hash=KeybindFiles.Hash(data);
        Assert.Throws<InvalidOperationException>(()=>KeybindFiles.Restore(live,Name,data,hash,false,()=>false));
        Assert.Throws<IOException>(()=>KeybindFiles.Restore(live,Name,data,hash,true,()=>true));
        Assert.Throws<InvalidDataException>(()=>KeybindFiles.Restore(live,Name,data,"bad",true,()=>false));
        Assert.False(Directory.Exists(KeybindFiles.Folder(live)));
        KeybindFiles.Restore(live,Name,data,hash,true,()=>false);Assert.Equal(data,KeybindFiles.Read(live,Name));
    }
    [Fact]
    public void Restore_preserves_previous_valid_profile_and_does_not_touch_global_ini()
    {
        var one=Xml(1);var two=Xml(2);KeybindFiles.Restore(live,Name,one,KeybindFiles.Hash(one),true,()=>false);
        var sentinel=Path.Combine(live,"user.cfg");File.WriteAllText(sentinel,"synthetic sentinel");
        KeybindFiles.Restore(live,Name,two,KeybindFiles.Hash(two),true,()=>false);
        Assert.Equal(one,File.ReadAllBytes(Path.Combine(KeybindFiles.Folder(live),Name)+".stardad-previous"));
        Assert.Equal("synthetic sentinel",File.ReadAllText(sentinel));Assert.Single(KeybindFiles.Exports(live));
        File.WriteAllText(Path.Combine(KeybindFiles.Folder(live),Name),"");
        KeybindFiles.Restore(live,Name,two,KeybindFiles.Hash(two),true,()=>false);
        Assert.Equal(one,File.ReadAllBytes(Path.Combine(KeybindFiles.Folder(live),Name)+".stardad-previous"));
    }
    [Theory]
    [InlineData("../layout_fixture_exported.xml")]
    [InlineData("user.cfg")]
    [InlineData("layout_fixture_exported.xml.exe")]
    public void Restore_is_confined_to_exported_profile_names(string name)=>Assert.Throws<InvalidDataException>(()=>KeybindFiles.Restore(live,name,Xml(1),KeybindFiles.Hash(Xml(1)),true,()=>false));
    [Fact]
    public void Empty_and_external_entity_profiles_are_not_accepted()
    {
        Assert.Throws<InvalidDataException>(()=>KeybindFiles.Validate(Name,Encoding.UTF8.GetBytes("<ActionMaps/>")));
        Assert.Throws<InvalidDataException>(()=>KeybindFiles.Validate(Name,Encoding.UTF8.GetBytes("<!DOCTYPE ActionMaps [<!ENTITY x SYSTEM 'file:///not-readable'>]><ActionMaps>&x;</ActionMaps>")));
        Assert.Throws<InvalidDataException>(()=>KeybindFiles.Validate(Name,new byte[KeybindFiles.MaxBytes+1]));
    }
}
