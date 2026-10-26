using static StarDexs.Sync.NativeMethods;

namespace StarDexs.Sync;

/// <summary>Plain Windows message boxes and the folder picker, always on top so they're never lost behind the game.</summary>
internal static unsafe class Dialogs
{
    private const string Title = "StarDexs Sync";

    public static void Tell(string text, bool warning = false) =>
        MessageBoxW(0, text, Title, MB_OK | MB_SETFOREGROUND | MB_TOPMOST | (warning ? MB_ICONWARNING : MB_ICONINFORMATION));

    public static bool Ask(string text) =>
        MessageBoxW(0, text, Title, MB_YESNO | MB_ICONQUESTION | MB_SETFOREGROUND | MB_TOPMOST) == IDYES;

    public static bool Confirm(string text) =>
        MessageBoxW(0, text, Title, MB_OKCANCEL | MB_ICONINFORMATION | MB_SETFOREGROUND | MB_TOPMOST) == IDOK;

    /// <summary>The folder the player picks, or null if they cancel.</summary>
    public static string? PickFolder(string prompt)
    {
        var name = stackalloc char[260];
        fixed (char* title = prompt)
        {
            var info = new BROWSEINFOW
            {
                pszDisplayName = name, lpszTitle = title, ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE,
            };
            var pidl = SHBrowseForFolderW(&info);
            if (pidl == 0) return null;
            try
            {
                var path = stackalloc char[1024];
                return SHGetPathFromIDListW(pidl, path) ? new string(path) : null;
            }
            finally { CoTaskMemFree(pidl); }
        }
    }
}
