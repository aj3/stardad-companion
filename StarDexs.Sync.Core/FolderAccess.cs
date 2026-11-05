using System.Security.AccessControl;
using System.Security.Principal;

namespace StarDexs.Sync.Core;

/// <summary>
/// The one step that needs administrator rights, run only after the player agrees: give their own Windows
/// account permission to change the game's english language folder and its user.cfg. Nothing else in the
/// game folder is touched, and no part of StarDad Companion keeps running as administrator.
/// </summary>
public static class FolderAccess
{
    public static void Grant(string live, SecurityIdentifier user)
    {
        if (!GameFolder.IsLive(live)) throw new ArgumentException("That folder isn't the game's LIVE folder.", nameof(live));
        var localization = Path.Combine(live, GameFolder.LocalizationFolder);
        Directory.CreateDirectory(localization);
        GameFolder.CheckWritable(live, GameFolder.GlobalIniPath(live));
        RefuseLinks(live, localization);
        var folder = new DirectoryInfo(localization);
        var folderRules = folder.GetAccessControl();
        folderRules.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        folder.SetAccessControl(folderRules);

        var cfg = new FileInfo(GameFolder.UserCfgPath(live));
        if (!cfg.Exists) File.WriteAllText(cfg.FullName, "");
        if (cfg.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("user.cfg is a link to somewhere else, so its permissions weren't changed.");
        var cfgRules = cfg.GetAccessControl();
        cfgRules.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Modify, AccessControlType.Allow));
        cfg.SetAccessControl(cfgRules);
    }

    /// <summary>Refuses a junction or symbolic link anywhere between LIVE and the language folder, so the
    /// permission change can't be redirected to a folder outside the game.</summary>
    internal static void RefuseLinks(string live, string target)
    {
        var path = Path.GetFullPath(live);
        foreach (var part in Path.GetRelativePath(path, Path.GetFullPath(target)).Split(Path.DirectorySeparatorChar))
        {
            path = Path.Combine(path, part);
            if (new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"{path} is a link to somewhere else, so its permissions weren't changed.");
        }
    }
}
