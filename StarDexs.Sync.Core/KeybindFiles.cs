using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace StarDexs.Sync.Core;

/// <summary>Exported control profiles only; separate consent and allowlist from global.ini.</summary>
public static class KeybindFiles
{
    public const int MaxBytes=1024*1024;
    public static string Folder(string live)=>Path.Combine(live,"USER","Client","0","Controls","Mappings");
    public static bool ValidName(string name)=>name.Length<=140&&Regex.IsMatch(name,@"\Alayout_[a-zA-Z0-9 _.\-]+_exported\.xml\z",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    public static void Validate(string name,byte[] data)
    {
        if(!ValidName(name)||data.Length is 0 or >MaxBytes)throw new InvalidDataException("Choose a populated exported control profile of up to 1 MiB.");
        try
        {
            using var source=new MemoryStream(data);using var reader=XmlReader.Create(source,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=MaxBytes});
            var xml=XDocument.Load(reader);
            if(xml.Root?.Name!="ActionMaps"||!xml.Descendants("actionmap").Any()||!xml.Descendants("rebind").Any())throw new InvalidDataException("This is not a populated exported control profile.");
        }
        catch(XmlException){throw new InvalidDataException("This profile is invalid XML or contains a prohibited document definition.");}
    }
    private static string Destination(string live,string name)
    {
        if(!GameFolder.IsLive(live)||!ValidName(name))throw new InvalidDataException("Choose your LIVE folder and an exported control profile.");
        var target=Path.GetFullPath(Path.Combine(Folder(live),name));
        for(var dir=new DirectoryInfo(Path.GetDirectoryName(target)!);dir is not null;dir=dir.Parent)
            if(dir.Exists&&dir.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new IOException("A keybind folder is a link. Select the installation's real folder instead.");
        foreach(var path in new[]{target,target+".stardad-previous",target+".stardad-tmp",target+".stardad-backup-tmp"})
            if(File.Exists(path)&&File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))throw new IOException("A keybind destination is a file link, so it was not changed.");
        return target;
    }
    public static IReadOnlyList<string> Exports(string live)
    {
        Destination(live,"layout_check_exported.xml");var folder=Folder(live);
        if(!Directory.Exists(folder))return [];
        var names=Directory.EnumerateFiles(folder,"layout_*_exported.xml").Select(Path.GetFileName).Where(n=>n is not null&&ValidName(n)).Select(n=>n!).Take(11).ToArray();
        if(names.Length>10)throw new IOException("More than 10 exported profiles were found. Upload selected profiles on the website instead.");return names;
    }
    private static byte[] ReadAtMost(string path)
    {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var output=new MemoryStream();var buffer=new byte[8192];int n;
        while((n=file.Read(buffer))>0){if(output.Length+n>MaxBytes)throw new IOException("A control profile exceeds 1 MiB.");output.Write(buffer,0,n);}return output.ToArray();
    }
    public static byte[] Read(string live,string name){var bytes=ReadAtMost(Destination(live,name));Validate(name,bytes);return bytes;}
    public static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static void Restore(string live,string name,byte[] bytes,string sha256,bool consent,Func<bool> gameRunning)
    {
        if(!consent)throw new InvalidOperationException("Confirm keybind restoration separately before writing a profile.");
        Validate(name,bytes);if(Hash(bytes)!=sha256)throw new InvalidDataException("The downloaded profile failed its integrity check.");
        if(gameRunning())throw new IOException("Close the game before restoring keybinds, then try Restore again.");
        var target=Destination(live,name);Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if(File.Exists(target)&&Hash(ReadAtMost(target))==sha256)return;
        if(File.Exists(target))
        {
            var old=ReadAtMost(target);
            try{Validate(name,old);WriteNew(target+".stardad-backup-tmp",target+".stardad-previous",old);}catch(InvalidDataException){/* Keep the previous known-good backup when a patch reset the export. */}
        }
        Destination(live,name);if(gameRunning())throw new IOException("The game started. Close it before restoring the profile.");
        WriteNew(target+".stardad-tmp",target,bytes);
        if(Hash(ReadAtMost(target))!=sha256)throw new IOException("Profile verification failed. The previous known-good export is retained beside it.");
    }
    private static void WriteNew(string temporary,string destination,byte[] bytes)
    {
        var created=false;
        try{using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){created=true;file.Write(bytes);file.Flush(true);}File.Move(temporary,destination,true);created=false;}
        finally{if(created&&File.Exists(temporary))File.Delete(temporary);}
    }
}
