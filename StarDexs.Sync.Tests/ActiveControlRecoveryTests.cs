using System.Text;
using StarDexs.Sync.Core;
using Xunit;

namespace StarDexs.Sync.Tests;

public sealed class ActiveControlRecoveryTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"stardad-controls-"+Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset now=DateTimeOffset.UtcNow;
    private static readonly string hardware=new('a',64);
    private string Live=>Path.Combine(root,"LIVE");
    public ActiveControlRecoveryTests(){Directory.CreateDirectory(Live);File.WriteAllText(Path.Combine(Live,"Data.p4k"),"synthetic marker only");Build("100");Write(Xml(1));}
    public void Dispose()=>Directory.Delete(root,true);
    private void Build(string build)=>File.WriteAllText(GameFolder.ManifestPath(Live),"{\"Data\":{\"RequestedP4ChangeNum\":\""+build+"\"}}");
    private static byte[] Xml(int? key)=>Encoding.UTF8.GetBytes("<ActionMaps><ActionProfiles version='1' optionsVersion='2' rebindVersion='2' profileName='default'><options type='keyboard' instance='1'/>"+(key is {} k?$"<actionmap name='synthetic'><action name='fixture'><rebind input='kb1_fixture{k}'/></action></actionmap>":"")+"</ActionProfiles></ActionMaps>");
    private void Write(byte[] data){Directory.CreateDirectory(Path.GetDirectoryName(ActiveControlRecovery.FilePath(Live))!);File.WriteAllBytes(ActiveControlRecovery.FilePath(Live),data);File.SetLastWriteTimeUtc(ActiveControlRecovery.FilePath(Live),now.AddMinutes(-2).UtcDateTime);}
    private ControlRecoveryState Capture()=>ActiveControlRecovery.Capture("owner",Live,hardware,now,()=>false);
    private ControlRecoveryResult Tick(ControlRecoveryState state,int seconds=40,bool running=false,string? owner=null,string? device=null)=>ActiveControlRecovery.Tick(state,owner??"owner",Live,device??hardware,now.AddSeconds(seconds),()=>running);
    [Fact]
    public void Manual_backup_change_restore_and_undo_like_a_player()
    {
        // Back up, as the app does it: manual only, so nothing restores on its own.
        var state=Capture();state.Enabled=false;
        // The player changes a binding in game, then a patch resets the file.
        Write(Xml(2));Build("101");
        Assert.False(Tick(state).Restored);Assert.Equal(Xml(2),File.ReadAllBytes(ActiveControlRecovery.FilePath(Live)));
        // Restore refuses while the game runs, then brings the backup back.
        Assert.Throws<IOException>(()=>ActiveControlRecovery.RestoreNow(state,"owner",Live,hardware,true,()=>true));
        Assert.True(ActiveControlRecovery.RestoreNow(state,"owner",Live,hardware,true,()=>false).Restored);
        Assert.Equal(Xml(1),File.ReadAllBytes(ActiveControlRecovery.FilePath(Live)));
        // Undo puts back what was there before the restore.
        Assert.True(ActiveControlRecovery.Undo(state,"owner",Live,hardware,true,()=>false).Restored);
        Assert.Equal(Xml(2),File.ReadAllBytes(ActiveControlRecovery.FilePath(Live)));
        Assert.Equal(Xml(1),state.Data);
    }
    [Fact]
    public void New_patch_missing_controls_restores_active_file_without_an_import_or_other_game_writes()
    {
        var state=Capture();File.Delete(ActiveControlRecovery.FilePath(Live));Build("101");
        File.WriteAllText(GameFolder.UserCfgPath(Live),"sentinel");
        Assert.False(Tick(state,0).Restored);Assert.True(Tick(state).Restored);Assert.Equal(Xml(1),File.ReadAllBytes(ActiveControlRecovery.FilePath(Live)));
        Assert.Equal("sentinel",File.ReadAllText(GameFolder.UserCfgPath(Live)));Assert.False(Directory.Exists(KeybindFiles.Folder(Live)));
    }
    [Fact]
    public void Reset_controls_preserve_baseline_and_restore_with_a_previous_copy()
    {
        var state=Capture();Build("101");Write(Xml(null));Tick(state,0);Assert.True(Tick(state).Restored);
        Assert.Equal(Xml(null),File.ReadAllBytes(ActiveControlRecovery.FilePath(Live)+".stardad-before-recovery"));
        Assert.Equal(Xml(1),state.Data);
    }
    [Fact]
    public void Game_open_patch_settle_and_device_changes_never_write()
    {
        var state=Capture();Build("101");File.Delete(ActiveControlRecovery.FilePath(Live));Tick(state,0);
        Assert.False(Tick(state,5).Restored);Assert.False(Tick(state,40,true).Restored);
        Assert.True(Tick(state,50,device:new('b',64)).NeedsAttention);Assert.False(File.Exists(ActiveControlRecovery.FilePath(Live)));
        Assert.True(Tick(state,60).Restored);
    }
    [Fact]
    public void Different_populated_controls_are_not_replaced_after_a_patch()
    {
        var state=Capture();Build("101");Write(Xml(2));Tick(state,0);Assert.True(Tick(state).NeedsAttention);
        Assert.Equal(Xml(2),File.ReadAllBytes(ActiveControlRecovery.FilePath(Live)));Assert.Equal(Xml(1),state.Data);
    }
    [Fact]
    public void Intentional_same_build_edits_update_baseline_and_same_build_resets_are_not_undone()
    {
        var state=Capture();Write(Xml(2));Assert.True(Tick(state).Changed);Assert.Equal(Xml(2),state.Data);
        Write(Xml(null));Assert.True(Tick(state,80).NeedsAttention);Assert.Equal(Xml(2),state.Data);Assert.Equal(Xml(null),File.ReadAllBytes(ActiveControlRecovery.FilePath(Live)));
    }
    [Fact]
    public void Invalid_baselines_unknown_schema_and_changed_owner_fail_closed()
    {
        var state=Capture();Assert.True(Tick(state,owner:"other").NeedsAttention);
        state.Data=Xml(2);Assert.Throws<InvalidDataException>(()=>Tick(state));
        Assert.Throws<InvalidDataException>(()=>ActiveControlRecovery.BindingCount(Encoding.UTF8.GetBytes("<!DOCTYPE ActionMaps [<!ENTITY x SYSTEM 'file:///private'>]><ActionMaps>&x;</ActionMaps>")));
        Assert.Throws<InvalidDataException>(()=>ActiveControlRecovery.BindingCount(Encoding.UTF8.GetBytes("<ActionMaps><ActionProfiles version='99'/></ActionMaps>")));
        Assert.Throws<InvalidDataException>(()=>ActiveControlRecovery.BindingCount(new byte[ActiveControlRecovery.MaxBytes+1]));
    }
    [Fact]
    public void Corrupt_destination_is_preserved_for_review_not_mistaken_for_a_reset()
    {
        var state=Capture();Build("101");Write(Encoding.UTF8.GetBytes("not XML"));Tick(state,0);Assert.Throws<InvalidDataException>(()=>Tick(state));
        Assert.Equal("not XML",File.ReadAllText(ActiveControlRecovery.FilePath(Live)));
    }
    [Fact]
    public void Restart_keeps_recovery_state_and_resumes_only_a_matching_interrupted_write()
    {
        var state=Capture();Build("101");File.Delete(ActiveControlRecovery.FilePath(Live));Tick(state,0);
        var store=new StateStore(Path.Combine(root,"app-data"),new FakeProtector());store.Save(new SyncState{ControlRecovery=state});state=store.Load().ControlRecovery!;
        File.WriteAllBytes(ActiveControlRecovery.FilePath(Live)+".stardad-recovery-new",state.Data);Assert.True(Tick(state).Restored);
        File.Delete(ActiveControlRecovery.FilePath(Live));File.WriteAllText(ActiveControlRecovery.FilePath(Live)+".stardad-recovery-new","unrelated");
        Assert.Throws<IOException>(()=>Tick(state,80));Assert.False(File.Exists(ActiveControlRecovery.FilePath(Live)));
    }
    [Fact]
    public void Restored_profile_remains_pinned_through_patch_cleanup_then_accepts_closed_game_edits()
    {
        var state=Capture();Build("101");File.Delete(ActiveControlRecovery.FilePath(Live));Tick(state,0);Assert.True(Tick(state).Restored);
        File.Delete(ActiveControlRecovery.FilePath(Live));Assert.True(Tick(state,80).Restored);
        Tick(state,100,true);Write(Xml(3));Assert.True(Tick(state,140).Changed);Assert.Equal(Xml(3),state.Data);Assert.Equal("101",state.Build);
    }
    [Fact]
    public void Explicit_restore_handles_same_build_reset_and_undo_restores_the_previous_file()
    {
        var state=Capture();Write(Xml(null));
        Assert.False(ActiveControlRecovery.RestoreNow(state,"owner",Live,hardware,false,()=>false).Restored);
        Assert.Equal(Xml(null),File.ReadAllBytes(ActiveControlRecovery.FilePath(Live)));
        Assert.True(ActiveControlRecovery.RestoreNow(state,"owner",Live,hardware,true,()=>false).Restored);
        Assert.Equal(Xml(1),File.ReadAllBytes(ActiveControlRecovery.FilePath(Live)));
        Assert.True(ActiveControlRecovery.Undo(state,"owner",Live,hardware,true,()=>false).Restored);
        Assert.False(state.Enabled);Assert.Equal(Xml(null),File.ReadAllBytes(ActiveControlRecovery.FilePath(Live)));
        Assert.Equal(Xml(1),state.Data);
    }
    [Fact]
    public void Undo_preserves_post_recovery_edits_and_rejects_a_tampered_previous_copy()
    {
        var state=Capture();Write(Xml(2));ActiveControlRecovery.RestoreNow(state,"owner",Live,hardware,true,()=>false);
        Write(Xml(3));Assert.Throws<IOException>(()=>ActiveControlRecovery.Undo(state,"owner",Live,hardware,true,()=>false));
        Write(Xml(1));File.WriteAllBytes(ActiveControlRecovery.FilePath(Live)+".stardad-before-recovery",Xml(4));
        Assert.Throws<IOException>(()=>ActiveControlRecovery.Undo(state,"owner",Live,hardware,true,()=>false));
        Assert.Equal(Xml(1),File.ReadAllBytes(ActiveControlRecovery.FilePath(Live)));
    }
    [Fact]
    public void Explicit_restore_checks_game_owner_and_hardware_and_unlink_requires_new_opt_in()
    {
        var state=Capture();Write(Xml(null));
        Assert.Throws<IOException>(()=>ActiveControlRecovery.RestoreNow(state,"owner",Live,hardware,true,()=>true));
        Assert.Throws<InvalidOperationException>(()=>ActiveControlRecovery.RestoreNow(state,"other",Live,hardware,true,()=>false));
        Assert.Throws<InvalidOperationException>(()=>ActiveControlRecovery.RestoreNow(state,"owner",Live,new('b',64),true,()=>false));
        var store=new StateStore(Path.Combine(root,"app-data"),new FakeProtector());var saved=new SyncState{ControlRecovery=state};store.Link(saved,"owner","Owner","token");store.Unlink(saved,"owner");store.Link(saved,"owner","Owner","token");
        Assert.False(saved.ControlRecovery!.Enabled);Assert.Equal(Xml(null),File.ReadAllBytes(ActiveControlRecovery.FilePath(Live)));
    }
    [Fact]
    public void Repeated_manual_recovery_keeps_the_latest_verified_previous_controls()
    {
        var state=Capture();Write(Xml(2));ActiveControlRecovery.RestoreNow(state,"owner",Live,hardware,true,()=>false);
        Write(Xml(3));ActiveControlRecovery.RestoreNow(state,"owner",Live,hardware,true,()=>false);
        ActiveControlRecovery.Undo(state,"owner",Live,hardware,true,()=>false);
        Assert.Equal(Xml(3),File.ReadAllBytes(ActiveControlRecovery.FilePath(Live)));
    }
    [Fact]
    public void Recapturing_preserves_only_a_verified_previous_copy_for_the_same_owner_and_folder()
    {
        var state=Capture();Write(Xml(2));ActiveControlRecovery.RestoreNow(state,"owner",Live,hardware,true,()=>false);
        var captured=ActiveControlRecovery.Capture("owner",Live,hardware,now,()=>false,state);
        Assert.Equal(state.PreviousSha256,captured.PreviousSha256);
        Write(Xml(3));ActiveControlRecovery.RestoreNow(captured,"owner",Live,hardware,true,()=>false);ActiveControlRecovery.Undo(captured,"owner",Live,hardware,true,()=>false);
        Assert.Equal(Xml(3),File.ReadAllBytes(ActiveControlRecovery.FilePath(Live)));
    }
    private sealed class FakeProtector:ISecretProtector{public string Protect(string value)=>value;public string? Unprotect(string value)=>value;}
}
