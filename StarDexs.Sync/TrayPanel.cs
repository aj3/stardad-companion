using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using static StarDexs.Sync.NativeMethods;

namespace StarDexs.Sync;

/// <summary>What the panel shows; the tray app builds one each time it opens or something changes.</summary>
internal sealed record PanelView(
    string Status, SyncBar.Look Look, double Share, string? LastSync,
    IReadOnlyList<(string Id, string Name, string? Schedule)> Accounts, bool HasFolder, bool InstallAgreed, bool NeedsAccess, bool HasInstalled, bool Busy,
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
/// The panel that opens from the tray icon. Top to bottom: how things stand (a badge and a plain-words line),
/// anything that needs the player, each linked account with its Sync button, then a short menu; the rarely
/// used actions fold away under "More options". It closes when it loses focus or on Escape.
/// </summary>
internal sealed class TrayPanel : Window
{
    private const double PanelWidth = 320;
    // Segoe icon font glyphs.
    private const string Globe = "", Folder = "", Link = "", Undo = "", Remove = "",
        Refresh = "", Unlinked = "", Down = "", Up = "", Shield = "", Download = "";

    private readonly IPanelActions _actions;
    private readonly SyncBar _badge = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _headline = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Palette.Text) };
    private readonly TextBlock _detail = Small("", Palette.Soft);
    private readonly TextBlock _last = Small("", Palette.Faint);
    private readonly StackPanel _notices = new() { Spacing = 8 };
    private readonly StackPanel _accounts = new() { Spacing = 8 };
    private readonly StackPanel _menu = new() { Spacing = 1 };
    private readonly StackPanel _extra = new() { Spacing = 1, IsVisible = false };
    private readonly Button _moreToggle;
    private PanelView? _view;

    public TrayPanel(IPanelActions actions)
    {
        _actions = actions;
        Title = "StarDad Companion";
        Width = PanelWidth;
        // Sized to its contents by Fit(), before it's shown: Windows' default size would leave a large empty area.
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Palette.Brush(Palette.Background);
        FontSize = 13;

        _moreToggle = Row(Down, "More options", () =>
        {
            _extra.IsVisible = !_extra.IsVisible;
            SetToggle();
            Fit();
            Place();
        }, closes: false);

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(16, 14, 16, 0),
            Children =
            {
                new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = Palette.Brush(Palette.Accent), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                At(1, new TextBlock { Text = "StarDad Companion", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Palette.Soft), LetterSpacing = 0.3 }),
            },
        };
        var status = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 12,
            Margin = new Thickness(16, 12, 16, 4),
            Children =
            {
                _badge,
                At(1, new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { _headline, _detail, _last } }),
            },
        };
        var version = typeof(TrayPanel).Assembly.GetName().Version?.ToString(3) ?? "";
        var quit = new Button
        {
            Content = "Quit", Padding = new Thickness(8, 3), FontSize = 12, Background = Brushes.Transparent,
            Foreground = Palette.Brush(Palette.Soft), HorizontalAlignment = HorizontalAlignment.Right,
        };
        quit.Classes.Add("row");
        quit.Click += (_, _) => { Hide(); _actions.Quit(); };
        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(16, 2, 10, 10),
            Children =
            {
                new TextBlock { Text = $"Version {version}", FontSize = 11.5, Foreground = Palette.Brush(Palette.Faint), VerticalAlignment = VerticalAlignment.Center },
                At(1, quit),
            },
        };

        Content = new Border
        {
            Background = Palette.Brush(Palette.Background),
            BorderBrush = Palette.Brush(Palette.Border),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Children =
                {
                    header,
                    status,
                    new StackPanel { Margin = new Thickness(12, 8, 12, 0), Spacing = 8, Children = { _notices, _accounts } },
                    new Border { Height = 1, Background = Palette.Brush(Palette.Border), Margin = new Thickness(16, 12, 16, 6) },
                    new StackPanel { Margin = new Thickness(8, 0), Children = { _menu, _moreToggle, _extra } },
                    new Border { Height = 1, Background = Palette.Brush(Palette.Border), Margin = new Thickness(16, 6, 16, 4) },
                    footer,
                },
            },
        };
        Deactivated += (_, _) => Hide();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        Opened += (_, _) => RoundCorners();
    }

    /// <summary>Opens above the point the player clicked (the tray icon), inside that screen's work area.</summary>
    public void ShowNear(PixelPoint click, PanelView view)
    {
        _anchor = click;
        _extra.IsVisible = false;
        SetToggle();
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

    /// <summary>Windows 11 rounds the panel's corners like its own flyouts; Windows 10 keeps them square.</summary>
    private unsafe void RoundCorners()
    {
        if (TryGetPlatformHandle()?.Handle is not { } window || window == 0) return;
        var round = DWMWCP_ROUND;
        try { DwmSetWindowAttribute(window, DWMWA_WINDOW_CORNER_PREFERENCE, &round, sizeof(int)); }
        catch (EntryPointNotFoundException) { }
    }

    /// <summary>Redraws with a fresh view (also while it's open, as a sync moves on).</summary>
    public void Show(PanelView view)
    {
        _view = view;
        _badge.Show(view.Look, view.Share);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { Fit(); if (IsVisible) Place(); }, Avalonia.Threading.DispatcherPriority.Render);
        _headline.Text = Headline(view);
        _detail.Text = view.Status;
        _detail.IsVisible = view.Status.Length > 0 && view.Status != _headline.Text;
        _last.Text = view.LastSync ?? "";
        _last.IsVisible = view.LastSync is not null;

        // Anything that needs the player, as a tinted card with one button.
        _notices.Children.Clear();
        if (view.UpdateTo is { } update)
            _notices.Children.Add(Notice(Palette.Accent, Download, $"Version {update} is ready", "Update", _actions.Update, !view.Busy));
        if (view.NeedsAccess)
            _notices.Children.Add(Notice(Palette.Warn, Shield, "Windows needs your OK to write to the game folder", "Allow", _actions.AllowAccess, true));
        if (view.Accounts.Count > 0 && view.HasFolder && !view.InstallAgreed)
            _notices.Children.Add(Notice(Palette.Warn, Shield, "Allow installing your file into the game", "Allow", _actions.AllowInstalling, true));
        if (view.Accounts.Count > 0 && !view.HasFolder)
            _notices.Children.Add(Notice(Palette.Warn, Folder, "Choose your game folder to start", "Choose", _actions.ChangeFolder, true));
        _notices.IsVisible = _notices.Children.Count > 0;

        _accounts.Children.Clear();
        if (view.Accounts.Count == 0)
        {
            var link = new Button { Content = "Link your StarDad account", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 9) };
            Primary(link);
            link.Click += (_, _) => { Hide(); _actions.Link(); };
            _accounts.Children.Add(link);
        }
        foreach (var (id, name, schedule) in view.Accounts) _accounts.Children.Add(AccountCard(id, name, schedule, view));

        _menu.Children.Clear();
        _menu.Children.Add(Row(Globe, "Open the StarDad website", _actions.OpenSite));
        _menu.Children.Add(Row(Folder, "Change game folder…", _actions.ChangeFolder));

        _extra.Children.Clear();
        if (view.Accounts.Count > 0) _extra.Children.Add(Row(Link, "Link another account…", _actions.Link));
        _extra.Children.Add(Row(Undo, "Put previous file back…", _actions.PutBackPrevious, enabled: view.HasFolder && view.HasInstalled && !view.Busy));
        _extra.Children.Add(Row(Remove, "Remove StarDad from the game…", _actions.RemoveFile, enabled: view.HasFolder && view.HasInstalled && !view.Busy));
        _extra.Children.Add(Row(Refresh, "Check for updates", _actions.CheckForUpdates, enabled: !view.Busy));
        foreach (var (id, name, _) in view.Accounts)
            _extra.Children.Add(Row(Unlinked, $"Unlink {name}…", () => _actions.Unlink(id), enabled: !view.Busy));
    }

    public PanelView? View => _view;

    private static string Headline(PanelView view) => view.Look switch
    {
        SyncBar.Look.Running => "Syncing…",
        SyncBar.Look.Hit => "All set",
        SyncBar.Look.Missed => "Needs attention",
        _ when view.Accounts.Count == 0 => "Not linked yet",
        _ when !view.HasFolder => "Almost ready",
        _ => "Ready",
    };

    /// <summary>One linked account: its name, when it syncs on its own, and its Sync button.</summary>
    private Border AccountCard(string id, string name, string? schedule, PanelView view)
    {
        var sync = new Button { Content = "Sync", IsEnabled = view.HasFolder && !view.Busy, Padding = new Thickness(16, 6), VerticalAlignment = VerticalAlignment.Center };
        Primary(sync);
        sync.Click += (_, _) => _actions.Sync(id);
        var when = Small(schedule ?? "Syncs when you ask", Palette.Faint);
        when.FontSize = 11.5;
        return new Border
        {
            Background = Palette.Brush(Palette.Card), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 10, 10),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 10,
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
                        Children = { new TextBlock { Text = name, Foreground = Palette.Brush(Palette.Text), TextTrimming = TextTrimming.CharacterEllipsis }, when },
                    },
                    At(1, sync),
                },
            },
        };
    }

    /// <summary>A tinted card that asks for one thing.</summary>
    private Border Notice(Color colour, string icon, string text, string button, Action act, bool enabled)
    {
        var go = new Button
        {
            Content = button, IsEnabled = enabled, Padding = new Thickness(12, 5), VerticalAlignment = VerticalAlignment.Center,
            Background = Palette.Tint(colour, 0.22), Foreground = Palette.Brush(colour), FontWeight = FontWeight.SemiBold, CornerRadius = new CornerRadius(7),
        };
        go.Click += (_, _) => { Hide(); act(); };
        return new Border
        {
            Background = Palette.Tint(colour, 0.10), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 8, 8),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                ColumnSpacing = 10,
                Children =
                {
                    new TextBlock { Text = icon, FontFamily = Palette.Icons, FontSize = 14, Foreground = Palette.Brush(colour), VerticalAlignment = VerticalAlignment.Center },
                    At(1, new TextBlock { Text = text, Foreground = Palette.Brush(Palette.Text), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 }),
                    At(2, go),
                },
            },
        };
    }

    /// <summary>A menu line: an icon and its words; closes the panel unless it only folds something open.</summary>
    private Button Row(string icon, string text, Action act, bool enabled = true, bool closes = true)
    {
        var button = new Button
        {
            IsEnabled = enabled, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent, Padding = new Thickness(8, 7), CornerRadius = new CornerRadius(6),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12,
                Children =
                {
                    new TextBlock { Text = icon, FontFamily = Palette.Icons, FontSize = 14, Width = 16, Foreground = Palette.Brush(enabled ? Palette.Soft : Palette.Faint), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        button.Classes.Add("row");
        button.Click += (_, _) => { if (closes) Hide(); act(); };
        return button;
    }

    private void SetToggle()
    {
        if (_moreToggle.Content is StackPanel { Children: [TextBlock icon, TextBlock words] })
        {
            icon.Text = _extra.IsVisible ? Up : Down;
            words.Text = _extra.IsVisible ? "Fewer options" : "More options";
        }
    }

    /// <summary>The teal tint for main actions (the hover and press shades come from the app's styles).</summary>
    private static void Primary(Button button)
    {
        button.Classes.Add("primary");
        button.Background = Palette.Tint(Palette.Accent, 0.22);
        button.Foreground = Palette.Brush(Palette.Accent);
        button.FontWeight = FontWeight.SemiBold;
        button.CornerRadius = new CornerRadius(8);
    }

    private static T At<T>(int column, T control) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private static TextBlock Small(string text, Color colour) => new()
    {
        Text = text, FontSize = 12.5, Foreground = Palette.Brush(colour), TextWrapping = TextWrapping.Wrap,
    };
}
