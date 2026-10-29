using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace StarDexs.Sync;

/// <summary>Draws the panel, in a few sync states, into one image (see "--render-preview" in Program).</summary>
internal static class PanelPreview
{
    private sealed class Nothing : IPanelActions
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

    public static void Save(string path)
    {
        (string Id, string Name, string? Schedule)[] one = [("a", "sample@example.invalid", "Auto: Every day at 6:00 PM")];
        (string Id, string Name, string? Schedule)[] two = [("a", "sample@example.invalid", "Auto: Every day at 6:00 PM"), ("b", "Alt account", null)];
        PanelView[] views =
        [
            new("Downloading your file…", SyncBar.Look.Running, 0.6, "Last synced 7:26 PM", one, true, true, false, true, true),
            new("Installed your file for 4.10.2-live.12913470. Restart the game to see it.", SyncBar.Look.Hit, 1, "Last synced 7:26 PM", two, true, true, false, true, false, "0.4.0"),
            new("Couldn't reach the StarDad website. Try again in a moment.", SyncBar.Look.Missed, 0.4, "Last synced 7:26 PM", one, true, true, true, true, false),
            new("Press \"Send to my PC\" in your account, or Sync here.", SyncBar.Look.Idle, 0, null, one, true, true, false, false, false),
        ];
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 16, Margin = new Thickness(16) };
        var windows = new List<TrayPanel>();
        foreach (var view in views)
        {
            // Opened off-screen so the controls get their theme, then drawn.
            var panel = new TrayPanel(new Nothing()) { Position = new PixelPoint(-4000, -4000), ShowActivated = false };
            panel.Show(view);
            panel.Show();
            windows.Add(panel);
        }
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var shots = new List<RenderTargetBitmap>();
        foreach (var panel in windows)
        {
            var content = (Control)panel.Content!;
            var shot = new RenderTargetBitmap(new PixelSize((int)content.Bounds.Width, (int)content.Bounds.Height));
            shot.Render(content);
            shots.Add(shot);
            panel.Close();
        }
        foreach (var shot in shots) row.Children.Add(new Image { Source = shot, Width = shot.PixelSize.Width, Height = shot.PixelSize.Height });
        var host = new Border { Background = Palette.Brush(Avalonia.Media.Color.Parse("#05070A")), Child = row };
        host.Measure(Size.Infinity);
        host.Arrange(new Rect(host.DesiredSize));
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)host.DesiredSize.Width, (int)host.DesiredSize.Height));
        bitmap.Render(host);
        bitmap.Save(path);
    }
}
