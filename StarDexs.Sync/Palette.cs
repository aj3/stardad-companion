using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace StarDexs.Sync;

/// <summary>
/// The panel's colours: stardexs.com's teal on a softer slate than the website, with gentle contrast and no
/// bright red, so it's easy on the eyes next to the taskbar.
/// </summary>
internal static class Palette
{
    public static readonly Color Background = Color.Parse("#171C23");
    public static readonly Color Card = Color.Parse("#1F262F");
    public static readonly Color Hover = Color.Parse("#28313C");
    public static readonly Color Pressed = Color.Parse("#303A46");
    public static readonly Color Border = Color.Parse("#2C3541");
    public static readonly Color Text = Color.Parse("#DCE3EA");
    public static readonly Color Soft = Color.Parse("#8E99A6");
    public static readonly Color Faint = Color.Parse("#5F6B78");
    public static readonly Color Accent = Color.Parse("#5DBFC2");
    public static readonly Color Good = Color.Parse("#72C69B");
    public static readonly Color Warn = Color.Parse("#E2B868");

    public static IBrush Brush(Color color) => new SolidColorBrush(color);

    /// <summary>A colour at a given opacity (0 to 1), for tinted backgrounds.</summary>
    public static IBrush Tint(Color color, double opacity) => new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), color.R, color.G, color.B));

    /// <summary>Segoe's icon font (Windows 11's, or Windows 10's with the same glyphs).</summary>
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
}

/// <summary>The Avalonia application: dark Fluent controls recoloured to the panel's palette.</summary>
internal sealed class CompanionApp(TrayApp? tray) : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        // Fluent's own button colours, swapped for the palette: hover lifts the button a shade, never flashes.
        Resources["ButtonBackground"] = Palette.Brush(Palette.Card);
        Resources["ButtonBackgroundPointerOver"] = Palette.Brush(Palette.Hover);
        Resources["ButtonBackgroundPressed"] = Palette.Brush(Palette.Pressed);
        Resources["ButtonForeground"] = Palette.Brush(Palette.Text);
        Resources["ButtonForegroundPointerOver"] = Palette.Brush(Palette.Text);
        Resources["ButtonForegroundPressed"] = Palette.Brush(Palette.Text);
        Resources["ButtonBorderBrush"] = Brushes.Transparent;
        Resources["ButtonBorderBrushPointerOver"] = Brushes.Transparent;
        Resources["ButtonBorderBrushPressed"] = Brushes.Transparent;
        Resources["ButtonBackgroundDisabled"] = Brushes.Transparent;
        Resources["ButtonBorderBrushDisabled"] = Brushes.Transparent;
        Resources["ButtonForegroundDisabled"] = Palette.Brush(Palette.Faint);

        // "primary": the one main action, tinted teal, a little stronger on hover.
        Styles.Add(Presenter("primary", ":pointerover", Palette.Tint(Palette.Accent, 0.32), Palette.Accent));
        Styles.Add(Presenter("primary", ":pressed", Palette.Tint(Palette.Accent, 0.42), Palette.Accent));
        // "row": menu items, flat until the pointer is on them.
        Styles.Add(Presenter("row", ":pointerover", Palette.Brush(Palette.Hover), Palette.Text));
    }

    /// <summary>A style for a class of button in one state (Fluent colours the template's presenter, not the button).</summary>
    private static Style Presenter(string kind, string state, IBrush background, Color foreground) =>
        new(x => x.OfType<Button>().Class(kind).Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, background),
                new Setter(ContentPresenter.ForegroundProperty, Palette.Brush(foreground)),
            },
        };

    public override void OnFrameworkInitializationCompleted()
    {
        tray?.Start();
        base.OnFrameworkInitializationCompleted();
    }
}
