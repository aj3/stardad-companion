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
    private readonly List<Button> _buttons=[];
    private readonly SyncApi _api;private readonly StateStore _store;private readonly SemaphoreSlim _gate;private readonly Action<bool> _busy;private readonly CancellationToken _cancel;
    public KeybindWindow(SyncApi api,StateStore store,SemaphoreSlim gate,Action<bool> busy,CancellationToken cancel)
    {
        _api=api;_store=store;_gate=gate;_busy=busy;_cancel=cancel;
        Title="Keybind backups";Width=500;MaxHeight=680;SizeToContent=SizeToContent.Height;CanResize=false;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        Background=Palette.Brush(Palette.Background);Foreground=Palette.Brush(Palette.Text);
        var state=store.Load();_accounts.ItemsSource=state.Accounts.Select(a=>new AccountChoice(a.Key,a.Value.Name)).ToList();
        _accounts.SelectedItem=(_accounts.ItemsSource as List<AccountChoice>)?.FirstOrDefault(a=>a.Id==state.LastAccount)??(_accounts.ItemsSource as List<AccountChoice>)?.FirstOrDefault();
        Button Action(string label,Func<Task> work){var button=new Button{Content=label,MinHeight=44,HorizontalAlignment=HorizontalAlignment.Stretch};button.Click+=async(_,_)=>await Run(work);_buttons.Add(button);return button;}
        var content=new StackPanel{Margin=new Thickness(24),Spacing=16,Children={
            new TextBlock{Text="Keybind backups",FontSize=24,FontWeight=FontWeight.SemiBold},
            new TextBlock{Text="Export your controls in game first. These backups keep exported XML profiles; unsaved changes are not included.",TextWrapping=TextWrapping.Wrap},
            new TextBlock{Text="Account"},_accounts,_automatic,
            Action("Save automatic backup setting",SavePreference),Action("Back up exported profiles now",BackupNow),
            new TextBlock{Text="Saved revision"},_backups,Action("Refresh saved profiles",LoadBackups),Action("Restore selected profile",RestoreSelected),
            new TextBlock{Text="Restoring requires a closed game and confirmation. Then import the restored profile in the game's Control Profiles menu. Joystick ordering is not changed.",TextWrapping=TextWrapping.Wrap},_status,
        }};
        var close=new Button{Content="Close",MinHeight=44,HorizontalAlignment=HorizontalAlignment.Right};close.Click+=(_,_)=>Close();content.Children.Add(close);
        Content=new ScrollViewer{Content=content,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled};
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
        _accounts.IsEnabled=false;_buttons.ForEach(b=>b.IsEnabled=false);_busy(true);_status.Text="Working…";
        try{await action();}
        catch(Exception e)when(e is IOException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException or NotLinkedException or OperationCanceledException){_status.Text=e is NotLinkedException?"This account is no longer linked. Link it again to access backups.":e.Message;}
        finally{_gate.Release();_busy(false);_accounts.IsEnabled=true;_buttons.ForEach(b=>b.IsEnabled=true);}
    }
    private async Task LoadBackups()
    {
        var current=Current();var status=await _api.StatusAsync(current.Token,_cancel);_automatic.IsChecked=status.KeybindBackup;
        var found=await _api.KeybindsAsync(current.Token,_cancel);_backups.ItemsSource=found.Select(b=>new BackupChoice(b)).ToArray();_backups.SelectedIndex=found.Count>0?0:-1;
        _status.Text=found.Count==0?"No backups yet. Export a profile in game, then back it up.":$"{found.Count} saved revisions. Only this account's profiles are listed.";
    }
    private async Task SavePreference()
    {
        var current=Current();var enabled=_automatic.IsChecked==true;await _api.SetKeybindBackupAsync(current.Token,enabled,_cancel);
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
