using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
    }
}
