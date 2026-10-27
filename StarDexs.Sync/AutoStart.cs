using Microsoft.Win32;

namespace StarDexs.Sync;

/// <summary>
/// Starts StarDad Companion quietly when the player signs in to Windows, but only while they have an
/// automatic sync on (chosen on the StarDad website); a schedule can't run if the app isn't running. For the
/// current player only, and taken away again when the last schedule is turned off or the app is uninstalled.
/// </summary>
internal static class AutoStart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "StarDad Companion", OldName = "StarDexs Companion";
    /// <summary>Started by Windows at sign-in: no "ready" notice.</summary>
    public const string Background = "--background";
    /// <summary>Run by the uninstaller: take the entry away and exit.</summary>
    public const string Forget = "--stop-starting";

    public static void Set(bool on)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(Key);
            run.DeleteValue(OldName, throwOnMissingValue: false); // from before the app was renamed
            if (on && Environment.ProcessPath is { } exe) run.SetValue(Name, $"\"{exe}\" {Background}");
            else run.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }
}
