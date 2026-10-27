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
        public void Update() { }
        public void Quit() { }
        public void CycleSyncNotifications() { }
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

    public static void Run(string? screenshot = null)
    {
        var view = new PanelView("Your file is up to date.", SyncBar.Look.Hit, 1, "Last synced just now",
            [("sample", "Sample account", "Auto: Every 3 hours")], true, true, false, true, false);
        var panel = new TrayPanel(new Actions()) { Position = new PixelPoint(-4000, -4000), ShowActivated = false };
        try
        {
            panel.Show(view); panel.Show(); Dispatcher.UIThread.RunJobs();
            var collapsed = panel.Height;
            Click(Button(panel, "More options"));
            Require(panel.Height > collapsed + 60, $"Expanded panel did not grow: {collapsed} -> {panel.Height}.");
            Require(Button(panel, "Quit").TranslatePoint(new Point(0, Button(panel, "Quit").Bounds.Height), panel) is { } end && end.Y <= panel.ClientSize.Height,
                "The footer is clipped after expansion.");
            Click(Button(panel, "Fewer options"));
            Require(Math.Abs(panel.Height - collapsed) <= 2, "Collapsed panel did not shrink back.");
            panel.MaxHeight = 300;
            panel.Show(view with { Accounts = Enumerable.Range(1, 10).Select(i => ($"account-{i}", $"Sample account {i}", (string?)"Auto: Every 3 hours")).ToArray() });
            Dispatcher.UIThread.RunJobs(); Click(Button(panel, "More options"));
            Require(panel.Height <= 300, "Panel exceeded its height limit.");
            var scroll = panel.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.Extent.Height > s.Viewport.Height + 1);
            Require(scroll is not null, "Tall panel has no usable scrolling fallback.");
            scroll!.Offset = new Vector(0, scroll.Extent.Height); Dispatcher.UIThread.RunJobs();
            var last = Button(panel, "Unlink Sample account 10…");
            Require(last.TranslatePoint(new Point(0, last.Bounds.Height), panel) is { } bottom && bottom.Y <= panel.ClientSize.Height,
                "The last expanded action cannot be reached by scrolling.");
            Require(Button(panel, "Quit").TranslatePoint(new Point(0, Button(panel, "Quit").Bounds.Height), panel) is { } footer && footer.Y <= panel.ClientSize.Height,
                "The footer is clipped in the short panel.");
            if (screenshot is not null)
            {
                var content = (Control)panel.Content!;
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)content.Bounds.Width, (int)content.Bounds.Height));
                bitmap.Render(content); bitmap.Save(screenshot);
            }
        }
        finally { panel.Close(); }
    }
}
