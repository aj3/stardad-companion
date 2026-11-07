using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StarDexs.Sync.Core;

namespace StarDexs.Sync;

/// <summary>Explicit keybind actions for linked accounts. No writes happen merely by opening it.</summary>
internal sealed class KeybindWindow : Window
{
    private sealed record AccountChoice(string Id,string Name){public override string ToString()=>Name;}
    private sealed record BackupChoice(KeybindCopy Copy){public override string ToString()=>Copy.Name+" · "+Copy.Created[..Math.Min(16,Copy.Created.Length)].Replace('T',' ');}
    private readonly ComboBox _accounts=new(){MinHeight=44,HorizontalAlignment=HorizontalAlignment.Stretch};
    private readonly ComboBox _backups=new(){MinHeight=44,HorizontalAlignment=HorizontalAlignment.Stretch};
    private readonly CheckBox _automatic=new(){Content="Back up changed exports during sync",MinHeight=44};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _recoveryStatus=new(){TextWrapping=TextWrapping.Wrap};
    private readonly List<Button> _buttons=[];
    private Button? _restoreSelected,_restoreActive,_undoActive;
    private readonly SyncApi _api;private readonly StateStore _store;private readonly SemaphoreSlim _gate;private readonly Action<bool> _busy;private readonly CancellationToken _cancel;
    public KeybindWindow(SyncApi api,StateStore store,SemaphoreSlim gate,Action<bool> busy,CancellationToken cancel)
    {
        _api=api;_store=store;_gate=gate;_busy=busy;_cancel=cancel;
        Title="Controls & backups";Width=500;Height=680;MaxHeight=680;FontSize=16;SizeToContent=SizeToContent.Manual;CanResize=false;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        Background=Palette.Brush(Palette.Background);Foreground=Palette.Brush(Palette.Text);
        var state=store.Load();_accounts.ItemsSource=state.Accounts.Select(a=>new AccountChoice(a.Key,a.Value.Name)).ToList();
        _accounts.SelectedItem=(_accounts.ItemsSource as List<AccountChoice>)?.FirstOrDefault(a=>a.Id==state.LastAccount)??(_accounts.ItemsSource as List<AccountChoice>)?.FirstOrDefault();
        Button Action(string label,Func<Task> work){var button=new Button{Content=label,MinHeight=44,HorizontalAlignment=HorizontalAlignment.Stretch};button.Click+=async(_,_)=>await Run(work);_buttons.Add(button);return button;}
        var exports=new StackPanel{Spacing=16,Children={
            new TextBlock{Text="Export your controls in game first. These cloud backups keep exported XML profiles; unsaved changes are not included.",TextWrapping=TextWrapping.Wrap},
            _automatic,Action("Back up exported profiles now",BackupNow),
            new TextBlock{Text="Saved revision"},_backups,Action("Refresh saved profiles",LoadBackups),(_restoreSelected=Action("Restore selected profile",RestoreSelected)),
            new TextBlock{Text="Restoring requires a closed game and confirmation. Then import the restored profile in the game's Control Profiles menu. Joystick ordering is not changed.",TextWrapping=TextWrapping.Wrap}
        }};
        var recovery=new StackPanel{Spacing=16,Children={
            new TextBlock{Text="Your current controls, joysticks included",FontWeight=FontWeight.SemiBold},
            new TextBlock{Text="Saves a copy of the game's active controls file (actionmaps.xml) on this PC, outside the game folder. If a patch resets your bindings, close the game and restore it. No in-game import needed.",TextWrapping=TextWrapping.Wrap},
            _recoveryStatus,Action("Back up my controls",EnableRecovery),
            (_restoreActive=Action("Restore my controls",RestoreActive)),(_undoActive=Action("Undo the last restore",UndoActive)),
            new TextBlock{Text="Keep the same joysticks plugged in when you restore. Check your bindings in game afterward.",TextWrapping=TextWrapping.Wrap}
        }};
        TabItem Tab(string label,Control body)=>new(){Header=new TextBlock{Text=label,FontSize=16,Foreground=Palette.Brush(Palette.Text)},MinHeight=44,Padding=new Thickness(12,8),Content=new ScrollViewer{Content=body,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled}};
        exports.Margin=new Thickness(0,0,12,0);recovery.Margin=new Thickness(0,0,12,0);
        var tabs=new TabControl{ItemsSource=new[]{Tab("Exported profiles",exports),Tab("Current controls",recovery)}};
        _automatic.Click+=async(_,_)=>await Run(SavePreference);
        tabs.SelectionChanged+=(_,_)=>_status.Text="";
        var heading=new StackPanel{Spacing=12,Children={new TextBlock{Text="Controls & backups",FontSize=24,FontWeight=FontWeight.SemiBold},new TextBlock{Text="Account"},_accounts}};
        var close=new Button{Content="Close",MinHeight=44,HorizontalAlignment=HorizontalAlignment.Right};close.Click+=(_,_)=>Close();
        var footer=new StackPanel{Spacing=12,Children={_status,close}};
        var content=new Grid{Margin=new Thickness(24),RowDefinitions=new RowDefinitions("Auto,*,Auto")};
        tabs.Margin=new Thickness(0,16);Grid.SetRow(heading,0);Grid.SetRow(tabs,1);Grid.SetRow(footer,2);content.Children.Add(heading);content.Children.Add(tabs);content.Children.Add(footer);
        Content=new Border{Background=Palette.Brush(Palette.Background),Child=content};
        _accounts.SelectionChanged+=async(_,_)=>await Run(LoadBackups);Opened+=async(_,_)=>await Run(LoadBackups);
    }
    private (SyncState State,string Account,string Token,string Live) Current()
    {
        var state=_store.Load();var id=(_accounts.SelectedItem as AccountChoice)?.Id??throw new InvalidOperationException("Link an account first.");
        return(state,id,_store.Token(state,id)??throw new InvalidOperationException("This account is no longer linked."),state.GameFolder??"");
    }
    private async Task Run(Func<Task> action)
    {
        if(!await _gate.WaitAsync(0)){_status.Text="A sync is running. Try again when it finishes.";return;}
        _accounts.IsEnabled=false;_automatic.IsEnabled=false;_buttons.ForEach(b=>b.IsEnabled=false);_busy(true);_status.Text="Working…";
        try{await action();}
        catch(Exception e)when(e is IOException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException or NotLinkedException or OperationCanceledException){_status.Text=e is NotLinkedException?"This account is no longer linked. Link it again to access backups.":e.Message;}
        finally{_gate.Release();_busy(false);_accounts.IsEnabled=true;_automatic.IsEnabled=true;_buttons.ForEach(b=>b.IsEnabled=true);UpdateActions();}
    }
    // Restore and undo only when there is something to restore or undo.
    private void UpdateActions()
    {
        if(_restoreSelected is not null)_restoreSelected.IsEnabled=_backups.SelectedItem is not null;
        var state=_store.Load();var id=(_accounts.SelectedItem as AccountChoice)?.Id;
        var mine=state.ControlRecovery is {} recovery&&recovery.Account==id?recovery:null;
        if(_restoreActive is not null)_restoreActive.IsEnabled=mine is not null;
        if(_undoActive is not null)_undoActive.IsEnabled=mine?.PreviousSha256 is not null;
    }
    private async Task LoadBackups()
    {
        var current=Current();var status=await _api.StatusAsync(current.Token,_cancel);_automatic.IsChecked=status.KeybindBackup;
        var found=await _api.KeybindsAsync(current.Token,_cancel);_backups.ItemsSource=found.Select(b=>new BackupChoice(b)).ToArray();_backups.SelectedIndex=found.Count>0?0:-1;
        _status.Text=found.Count==0?"No backups yet. Export a profile in game, then back it up.":$"{found.Count} saved revisions. Only this account's profiles are listed.";
        _recoveryStatus.Text=current.State.ControlRecovery is {} recovery?recovery.Account==current.Account?recovery.Status:"The backup on this PC belongs to a different linked account.":"No backup yet. Close the game, then choose Back up my controls.";
    }
    private async Task EnableRecovery()
    {
        var current=Current();

        var hardware=await Task.Run(ControllerInventory.Read,_cancel)??throw new IOException("Connected devices could not be verified.");
        var recovery=await Task.Run(()=>ActiveControlRecovery.Capture(current.Account,current.Live,hardware.Hash,DateTimeOffset.UtcNow,InstallWriter.GameIsRunning,current.State.ControlRecovery),_cancel);
        // A joystick baseline needs an attached controller, not a disconnected/default device list.
        if(hardware.Controllers==0&&ActiveControlRecovery.UsesJoystick(recovery.Data))throw new IOException("Plug in your joysticks before backing up, so the backup matches them.");
        // Manual only, like the community guides: no automatic restore after patches.
        recovery.Enabled=false;current.State.ControlRecovery=recovery;_store.Save(current.State);_recoveryStatus.Text=recovery.Status;_status.Text="Controls backed up.";
    }
    private Task DisableRecovery()
    {
        var current=Current();
        if(current.State.ControlRecovery is {} recovery&&recovery.Account==current.Account){recovery.Enabled=false;recovery.Status="Automatic recovery is off. The local last-good baseline is kept.";_store.Save(current.State);_recoveryStatus.Text=recovery.Status;}
        _status.Text="Automatic recovery is off.";
        return Task.CompletedTask;
    }
    private async Task RestoreActive()
    {
        var current=Current();var recovery=current.State.ControlRecovery??throw new InvalidOperationException("Back up your controls first.");
        if(!Dialogs.Ask("Restore your backed-up controls?\n\nThis replaces the game's current controls file. Your current one is kept so you can undo. The game must be closed.\n\nRestore now?")){_status.Text="Cancelled. No files were changed.";return;}
        var hardware=await Task.Run(ControllerInventory.Read,_cancel);
        var result=await Task.Run(()=>ActiveControlRecovery.RestoreNow(recovery,current.Account,current.Live,hardware?.Hash,true,InstallWriter.GameIsRunning),_cancel);
        _store.Save(current.State);_recoveryStatus.Text=result.Message;_status.Text="Controls restored. Start the game and check your bindings.";
    }
    private async Task UndoActive()
    {
        var current=Current();var recovery=current.State.ControlRecovery??throw new InvalidOperationException("There's no restore to undo.");
        if(!Dialogs.Ask("Undo the last restore?\n\nThis puts back the controls you had before restoring, if they haven't changed since. The game must be closed.\n\nUndo?")){_status.Text="Cancelled. No files were changed.";return;}
        var hardware=await Task.Run(ControllerInventory.Read,_cancel);
        var result=await Task.Run(()=>ActiveControlRecovery.Undo(recovery,current.Account,current.Live,hardware?.Hash,true,InstallWriter.GameIsRunning),_cancel);
        _store.Save(current.State);_recoveryStatus.Text=result.Message;_status.Text="Restore undone.";
    }
    private async Task SavePreference()
    {
        var current=Current();var enabled=_automatic.IsChecked==true;
        // Saved as soon as it is ticked; if the save fails, the box goes back to what is actually saved.
        try{await _api.SetKeybindBackupAsync(current.Token,enabled,_cancel);}catch{_automatic.IsChecked=!enabled;throw;}
        current.State.Accounts[current.Account].BackupKeybinds=enabled;_store.Save(current.State);_status.Text=enabled?"Changed exports will be backed up during syncs.":"Automatic keybind backup is off. Saved backups are retained.";
    }
    private async Task BackupNow()
    {
        var current=Current();var status=await _api.StatusAsync(current.Token,_cancel);var names=await Task.Run(()=>KeybindFiles.Exports(current.Live),_cancel);var added=0;
        foreach(var name in names)
        {
            var bytes=await Task.Run(()=>KeybindFiles.Read(current.Live,name),_cancel);
            if(await _api.UploadKeybindAsync(current.Token,name,bytes,status.Patch,_cancel))added++;
            current.State.Accounts[current.Account].KeybindHashes[Path.Combine(Path.GetFullPath(current.Live),name)]=KeybindFiles.Hash(bytes);
        }
        _store.Save(current.State);await LoadBackups();_status.Text=names.Count==0?"No exports were found. Save Control Settings in game first.":$"{names.Count} profiles checked; {added} new revisions backed up.";
    }
    private async Task RestoreSelected()
    {
        if(_backups.SelectedItem is not BackupChoice selected)throw new InvalidOperationException("Select a saved profile first.");
        var current=Current();if(InstallWriter.GameIsRunning())throw new IOException("Close the game before restoring, then try again.");
        var profile=await _api.DownloadKeybindAsync(current.Token,selected.Copy.Id,_cancel);
        if(!Dialogs.Ask($"Restore {profile.Name} from {selected.Copy.Source}?\n\nThis replaces that exported profile in your selected LIVE folder and keeps a previous known-good copy beside it. It does not change active bindings. Import the profile in game afterward.\n\nRestore this profile?")){_status.Text="Restore cancelled. No files were changed.";return;}
        await Task.Run(()=>KeybindFiles.Restore(current.Live,profile.Name,profile.Data,profile.Sha256,true,InstallWriter.GameIsRunning),_cancel);
        _status.Text="Profile restored and verified. Import it from Control Profiles in game to activate your bindings.";
    }
}
