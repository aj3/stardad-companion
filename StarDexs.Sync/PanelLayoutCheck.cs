using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StarDexs.Sync.Core;

namespace StarDexs.Sync;

/// <summary>Exercises the real native panel without accounts, game access, or a tray session.</summary>
internal static class PanelLayoutCheck
{
    private sealed class Actions : IPanelActions
    {
        public void Sync(string account) { }
        public void Unlink(string account) { }
        public void Link() { }
        public void AllowInstalling() { }
        public void AllowAccess() { }
        public void OpenSite() { }
        public void ChangeFolder() { }
        public void PutBackPrevious() { }
        public void RemoveFile() { }
        public void CheckForUpdates() { }
        public void Keybinds() { }
        public void Update() { }
        public void Quit() { }
        public void ReplaceUnknown() { }
    }

    private static Button Button(TrayPanel panel, string caption) => panel.GetVisualDescendants().OfType<Button>().Single(b =>
        b.Content is string s && s == caption || b.Content is StackPanel row && row.Children.OfType<TextBlock>().Any(t => t.Text == caption));
    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Capture(Window window, string path)
    {
        var content = (Control)window.Content!;
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)content.Bounds.Width, (int)content.Bounds.Height));
        bitmap.Render(content); bitmap.Save(path);
    }

    public static void Run(string? screenshot = null)
    {
        var view = new PanelView("Your file is up to date.", SyncBar.Look.Hit, 1, "Last synced just now",
            [("sample", "Sample account", "Auto: Every 3 hours")], true, true, false, true, false);
        var panel = new TrayPanel(new Actions()) { Position = new PixelPoint(-4000, -4000), ShowActivated = false };
        try
        {
            panel.Show(view); panel.Show(); Dispatcher.UIThread.RunJobs();
            panel.Show(view with { UnknownFile = true }); Dispatcher.UIThread.RunJobs();
            Require(Button(panel, "Review").IsEnabled, "File conflict has no explicit review action.");
            panel.Show(view with { UnknownFile = true, Busy = true }); Dispatcher.UIThread.RunJobs();
            Require(!Button(panel, "Review").IsEnabled, "Conflict review remained enabled during a sync.");
            panel.Show(view); Dispatcher.UIThread.RunJobs();
            Require(panel.GetVisualDescendants().OfType<Button>().Where(b=>b.IsVisible).All(b=>b.Bounds.Height>=43), "A panel action is smaller than the minimum target height.");
            var collapsed = panel.Height;
            if (screenshot is not null) Capture(panel, screenshot + ".main.png");
            Click(Button(panel, "Settings"));
            Require(panel.SettingsWindow?.IsVisible == true, "Settings did not open.");
            Require(Math.Abs(panel.Height - collapsed) <= 2, "Opening Settings resized the main panel.");
            if (screenshot is not null) Capture(panel.SettingsWindow!, screenshot + ".settings.png");
            panel.SettingsWindow!.Close(); Dispatcher.UIThread.RunJobs();
            Require(Button(panel, "Quit").TranslatePoint(new Point(0, Button(panel, "Quit").Bounds.Height), panel) is { } end && end.Y <= panel.ClientSize.Height,
                "The footer is clipped after expansion.");
            Require(Math.Abs(panel.Height - collapsed) <= 2, "Collapsed panel did not shrink back.");
            panel.MaxHeight = 300;
            panel.Show(view with { Accounts = Enumerable.Range(1, 10).Select(i => ($"account-{i}", $"Sample account {i}", (string?)"Auto: Every 3 hours")).ToArray() });
            Dispatcher.UIThread.RunJobs();
            Require(panel.Height <= 300, "Panel exceeded its height limit.");
            var scroll = panel.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.Extent.Height > s.Viewport.Height + 1);
            Require(scroll is not null, "Tall panel has no usable scrolling fallback.");
            scroll!.Offset = new Vector(0, scroll.Extent.Height); Dispatcher.UIThread.RunJobs();
            var last = Button(panel, "Settings");
            Require(last.TranslatePoint(new Point(0, last.Bounds.Height), panel) is { } bottom && bottom.Y <= panel.ClientSize.Height,
                "The last expanded action cannot be reached by scrolling.");
            Require(Button(panel, "Quit").TranslatePoint(new Point(0, Button(panel, "Quit").Bounds.Height), panel) is { } footer && footer.Y <= panel.ClientSize.Height,
                "The footer is clipped in the short panel.");
            if (screenshot is not null)
            {
                Capture(panel, screenshot);
            }
        }
        finally { panel.Close(); }
        CheckControls(screenshot);
    }
    private sealed class PreviewProtector:ISecretProtector{public string Protect(string value)=>value;public string? Unprotect(string value)=>value;}
    private sealed class PreviewApi:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancel)
        {
            var body=request.RequestUri!.AbsolutePath.EndsWith("/status")?"{\"settings\":\"sample\",\"has_settings\":false}":"{\"backups\":[]}";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent(body,System.Text.Encoding.UTF8,"application/json")});
        }
    }
    private static void CheckControls(string? screenshot)
    {
        var folder=Directory.CreateTempSubdirectory("stardad-controls-layout-").FullName;
        using var gate=new SemaphoreSlim(1,1);using var http=new HttpClient(new PreviewApi()){BaseAddress=new Uri("https://sample.invalid/")};
        var store=new StateStore(folder,new PreviewProtector());var state=new SyncState();store.Link(state,"sample","Sample account","sample-token");store.Save(state);
        var window=new KeybindWindow(new SyncApi(http),store,gate,_=>{},CancellationToken.None){Position=new PixelPoint(-4000,-4000),ShowActivated=false,WindowStartupLocation=WindowStartupLocation.Manual};
        try
        {
            window.Show();Dispatcher.UIThread.RunJobs();
            var tabs=window.GetVisualDescendants().OfType<TabControl>().Single();tabs.SelectedIndex=1;Dispatcher.UIThread.RunJobs();
            Require(window.ClientSize.Height<=680,"Controls window exceeded its height cap.");
            var undersized=window.GetVisualDescendants().OfType<Button>().Where(b=>b.IsEffectivelyVisible&&b.Content is string&&b.Bounds.Height<43).Select(b=>$"{b.Content} ({b.Bounds.Height})").ToArray();
            Require(undersized.Length==0,"A controls action is undersized: "+string.Join(", ",undersized));
            Require(window.GetVisualDescendants().OfType<Button>().Any(b=>b.Content as string=="Undo the last restore"),"Recovery undo action is missing.");
            if(screenshot is not null)Capture(window,screenshot+".controls.png");
            var scroll=window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s=>s.Extent.Height>s.Viewport.Height+1);
            // The controls tab may fit without scrolling; if it doesn't, scroll to the end before checking.
            if(scroll is not null){scroll.Offset=new Vector(0,scroll.Extent.Height);Dispatcher.UIThread.RunJobs();}
            var undo=window.GetVisualDescendants().OfType<Button>().Single(b=>b.Content as string=="Undo the last restore");
            Require(undo.TranslatePoint(new Point(0,undo.Bounds.Height),window) is {} end&&end.Y<window.ClientSize.Height,"Undo recovery is unreachable.");
            if(screenshot is not null)Capture(window,screenshot+".controls-end.png");
        }
        finally{window.Close();Directory.Delete(folder,true);}
    }

}
