using static StarDexs.Sync.NativeMethods;

namespace StarDexs.Sync;

/// <summary>Plain Windows message boxes, always on top so they're never lost behind the game.</summary>
internal static class Dialogs
{
    private const string Title = "StarDad Companion";

    public static void Tell(string text, bool warning = false) =>
        MessageBoxW(0, text, Title, MB_OK | MB_SETFOREGROUND | MB_TOPMOST | (warning ? MB_ICONWARNING : MB_ICONINFORMATION));

    public static bool Ask(string text) =>
        MessageBoxW(0, text, Title, MB_YESNO | MB_ICONQUESTION | MB_SETFOREGROUND | MB_TOPMOST) == IDYES;

    public static bool Confirm(string text) =>
        MessageBoxW(0, text, Title, MB_OKCANCEL | MB_ICONINFORMATION | MB_SETFOREGROUND | MB_TOPMOST) == IDOK;
}
