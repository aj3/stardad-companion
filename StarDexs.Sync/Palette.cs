using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace StarDexs.Sync;

/// <summary>stardexs.com's colours, so the Companion's panel looks like the website.</summary>
internal static class Palette
{
    public static readonly Color Background = Color.Parse("#0A0E14");
    public static readonly Color Panel = Color.Parse("#121A22");
    public static readonly Color Raised = Color.Parse("#1A2530");
    public static readonly Color Border = Color.Parse("#26323E");
    public static readonly Color Text = Color.Parse("#E6EEF5");
    public static readonly Color Soft = Color.Parse("#8A9AA8");
    public static readonly Color Accent = Color.Parse("#3FD0D4");
    public static readonly Color Target = Color.Parse("#FF4D4D");

    public static IBrush Brush(Color color) => new SolidColorBrush(color);
}

/// <summary>The Avalonia application: dark Fluent controls recoloured to the website's theme.</summary>
internal sealed class CompanionApp(TrayApp? tray) : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        // Fluent's own colours for buttons, swapped for the site's.
        Resources["ButtonBackground"] = Palette.Brush(Palette.Raised);
        Resources["ButtonBackgroundPointerOver"] = Palette.Brush(Color.FromArgb(0x33, Palette.Accent.R, Palette.Accent.G, Palette.Accent.B));
        Resources["ButtonBackgroundPressed"] = Palette.Brush(Color.FromArgb(0x55, Palette.Accent.R, Palette.Accent.G, Palette.Accent.B));
        Resources["ButtonForeground"] = Palette.Brush(Palette.Text);
        Resources["ButtonForegroundPointerOver"] = Palette.Brush(Palette.Text);
        Resources["ButtonForegroundPressed"] = Palette.Brush(Palette.Text);
        Resources["ButtonBorderBrush"] = Palette.Brush(Palette.Border);
        Resources["ButtonBorderBrushPointerOver"] = Palette.Brush(Palette.Accent);
        Resources["ButtonBackgroundDisabled"] = Avalonia.Media.Brushes.Transparent;
        Resources["ButtonBorderBrushDisabled"] = Palette.Brush(Palette.Border);
        Resources["ButtonForegroundDisabled"] = Palette.Brush(Palette.Soft);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        tray?.Start();
        base.OnFrameworkInitializationCompleted();
    }
}
