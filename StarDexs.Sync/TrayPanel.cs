using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace StarDexs.Sync;

/// <summary>What the panel shows; the tray app builds one each time it opens or something changes.</summary>
internal sealed record PanelView(
    string Status, SyncBar.Look Look, double Share, string? LastSync,
    IReadOnlyList<(string Id, string Name)> Accounts, bool HasFolder, bool InstallAgreed, bool NeedsAccess, bool HasInstalled, bool Busy,
    string? UpdateTo = null);

/// <summary>What the panel's buttons do; the tray app does it.</summary>
internal interface IPanelActions
{
    void Sync(string account);
    void Unlink(string account);
    void Link();
    void AllowInstalling();
    void AllowAccess();
    void OpenSite();
    void ChangeFolder();
    void PutBackPrevious();
    void RemoveFile();
    void CheckForUpdates();
    void Update();
    void Quit();
}

/// <summary>
/// The panel that opens from the tray icon, in the website's colours: the sync bar, each linked account with
/// its own Sync and Unlink, and the app's actions. It closes when it loses focus or on Escape.
/// </summary>
internal sealed class TrayPanel : Window
{
    private const double PanelWidth = 340;
    private readonly IPanelActions _actions;
    private readonly SyncBar _bar = new();
    private readonly TextBlock _status = Small("", Palette.Text);
    private readonly TextBlock _last = Small("", Palette.Soft);
    private readonly StackPanel _accounts = new() { Spacing = 6 };
    private readonly StackPanel _menu = new() { Spacing = 2 };
    private readonly Button _update = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
        Padding = new Thickness(10, 7), FontWeight = FontWeight.SemiBold, IsVisible = false,
    };
    private PanelView? _view;

    public TrayPanel(IPanelActions actions)
    {
        _actions = actions;
        Title = "StarDexs Companion";
        Width = PanelWidth;
        // Sized to its contents by Fit(), before it's shown: Windows' default size would leave a large empty area.
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Palette.Brush(Palette.Background);

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Palette.Brush(Palette.Accent), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = "StarDexs Companion", FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Palette.Brush(Palette.Text) },
            },
        };
        Content = new Border
        {
            Background = Palette.Brush(Palette.Background),
            BorderBrush = Palette.Brush(Palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(16, 14),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    header,
                    new StackPanel { Spacing = 4, Children = { _bar, _status, _last } },
                    _update,
                    Section("Accounts"),
                    _accounts,
                    new Border { Height = 1, Background = Palette.Brush(Palette.Border), Margin = new Thickness(0, 2) },
                    _menu,
                },
            },
        };
        _update.Background = Palette.Brush(Palette.Accent);
        _update.Foreground = Palette.Brush(Palette.Background);
        _update.Click += (_, _) => { Hide(); _actions.Update(); };
        Deactivated += (_, _) => Hide();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
    }

    /// <summary>Opens above the point the player clicked (the tray icon), inside that screen's work area.</summary>
    public void ShowNear(PixelPoint click, PanelView view)
    {
        _anchor = click;
        Show(view);
        Fit();
        Place();
        Show();
        Activate();
        // Fonts and templates settle once it's on screen: measure again so it fits exactly.
        Fit();
        Place();
    }

    private PixelPoint? _anchor;

    /// <summary>The panel's height for its contents right now, measured before it's drawn.</summary>
    private void Fit()
    {
        if (Content is not Control content) return;
        content.Measure(new Size(PanelWidth, double.PositiveInfinity));
        Width = PanelWidth;
        Height = Math.Ceiling(content.DesiredSize.Height);
    }

    /// <summary>Just above the point the player clicked (the tray icon), inside that screen's work area.</summary>
    private void Place()
    {
        if (_anchor is not { } click) return;
        var screen = Screens.ScreenFromPoint(click) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var size = new PixelSize((int)Math.Ceiling(Width * screen.Scaling), (int)Math.Ceiling(Height * screen.Scaling));
        var x = Math.Clamp(click.X - size.Width / 2, area.X + 8, Math.Max(area.X + 8, area.Right - size.Width - 8));
        var y = Math.Clamp(click.Y - size.Height - 12, area.Y + 8, Math.Max(area.Y + 8, area.Bottom - size.Height - 8));
        Position = new PixelPoint(x, y);
    }

    /// <summary>Redraws with a fresh view (also while it's open, as a sync moves on).</summary>
    public void Show(PanelView view)
    {
        _view = view;
        _bar.Show(view.Look, view.Share);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { Fit(); if (IsVisible) Place(); }, Avalonia.Threading.DispatcherPriority.Render);
        _status.Text = view.Status;
        _last.Text = view.LastSync ?? "";
        _last.IsVisible = view.LastSync is not null;

        _accounts.Children.Clear();
        if (view.Accounts.Count == 0) _accounts.Children.Add(Small("No account linked yet.", Palette.Soft));
        foreach (var (id, name) in view.Accounts)
        {
            var sync = new Button { Content = "Sync", IsEnabled = view.HasFolder && !view.Busy, Padding = new Thickness(12, 4) };
            sync.Click += (_, _) => _actions.Sync(id);
            var unlink = new Button { Content = "Unlink", IsEnabled = !view.Busy, Padding = new Thickness(10, 4), Foreground = Palette.Brush(Palette.Soft) };
            unlink.Click += (_, _) => { Hide(); _actions.Unlink(id); };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
            var label = new TextBlock { Text = name, Foreground = Palette.Brush(Palette.Text), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(sync, 1);
            Grid.SetColumn(unlink, 2);
            row.Children.Add(label);
            row.Children.Add(sync);
            row.Children.Add(unlink);
            _accounts.Children.Add(new Border
            {
                Background = Palette.Brush(Palette.Panel), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6), Child = row,
            });
        }

        _menu.Children.Clear();
        // A newer signed release: one clear button under the status, not a line in the menu.
        _update.IsVisible = view.UpdateTo is not null;
        _update.IsEnabled = !view.Busy;
        _update.Content = $"Update to {view.UpdateTo}";
        Item(view.Accounts.Count == 0 ? "Link your account…" : "Link another account…", _actions.Link);
        if (view.Accounts.Count > 0 && view.HasFolder && !view.InstallAgreed) Item("Allow installing files…", _actions.AllowInstalling, accent: true);
        if (view.NeedsAccess) Item("Allow access to the game folder…", _actions.AllowAccess, accent: true);
        Item("Open stardexs.com", _actions.OpenSite);
        Item("Change game folder…", _actions.ChangeFolder);
        Item("Put previous file back…", _actions.PutBackPrevious, enabled: view.HasFolder && view.HasInstalled && !view.Busy);
        Item("Remove StarDexs from the game…", _actions.RemoveFile, enabled: view.HasFolder && view.HasInstalled && !view.Busy);
        Item("Check for updates", _actions.CheckForUpdates, enabled: !view.Busy);
        Item("Quit", _actions.Quit);
    }

    public PanelView? View => _view;

    private void Item(string text, Action act, bool enabled = true, bool accent = false)
    {
        var button = new Button
        {
            Content = text, IsEnabled = enabled, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(8, 6),
            Foreground = Palette.Brush(accent ? Palette.Accent : Palette.Text),
        };
        button.Click += (_, _) => { Hide(); act(); };
        _menu.Children.Add(button);
    }

    private static TextBlock Section(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 11, LetterSpacing = 1.2, FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Palette.Accent),
    };

    private static TextBlock Small(string text, Color colour) => new()
    {
        Text = text, FontSize = 12.5, Foreground = Palette.Brush(colour), TextWrapping = TextWrapping.Wrap,
    };
}
