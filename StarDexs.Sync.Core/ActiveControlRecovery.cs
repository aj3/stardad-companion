using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace StarDexs.Sync.Core;

/// <summary>One local, explicitly authorized recovery baseline. Never uploaded with log findings.</summary>
public sealed class ControlRecoveryState
{
    public bool Enabled { get; set; }
    public string Account { get; set; }="";
    public string Folder { get; set; }="";
    public string Hardware { get; set; }="";
    public string Build { get; set; }="";
    public string Sha256 { get; set; }="";
    public byte[] Data { get; set; }=[];
    public string? ObservedBuild { get; set; }
    public DateTimeOffset ObservedSince { get; set; }
    public string? RestoredBuild { get; set; }
    public string? PreviousSha256 { get; set; }
    public bool GameSeenAfterRestore { get; set; }
    public string Status { get; set; }="Not enabled.";
}

public sealed record ControlRecoveryResult(string Message,bool Changed=false,bool Restored=false,bool NeedsAttention=false);

/// <summary>Active controls, not an exported layout. Uses only a pinned LIVE folder and default/actionmaps.xml.</summary>
public static class ActiveControlRecovery
{
    public const int MaxBytes=1024*1024;
    public static readonly TimeSpan SettleTime=TimeSpan.FromSeconds(30);
    public static string FilePath(string live)=>Path.Combine(live,"USER","Client","0","Profiles","default","actionmaps.xml");
    private static bool ValidBuild(string? build)=>build is {Length:>0 and <=20}&&build.All(char.IsAsciiDigit)&&ulong.TryParse(build,out _);
    private static bool ValidHash(string? hash)=>hash is {Length:64}&&hash.All(char.IsAsciiHexDigit);
    private static string Hash(byte[] data)=>Convert.ToHexStringLower(SHA256.HashData(data));
    private static bool IsBound(string? input)=>KeybindFiles.HasBindingInput(input);
    public static bool UsesJoystick(byte[] data)
    {
        BindingCount(data);
        using var input=new MemoryStream(data);using var reader=XmlReader.Create(input,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=MaxBytes});
        return XDocument.Load(reader).Descendants("rebind").Any(e=>IsBound((string?)e.Attribute("input"))&&((string?)e.Attribute("input"))?.StartsWith("js",StringComparison.OrdinalIgnoreCase)==true);
    }

    /// <summary>The current game's observed active-controls schema; unknown versions fail closed.</summary>
    public static int BindingCount(byte[] data)
    {
        if(data.Length is 0 or >MaxBytes)throw new InvalidDataException("The active controls file is empty or exceeds 1 MiB.");
        try
        {
            using var input=new MemoryStream(data);
            using var reader=XmlReader.Create(input,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=MaxBytes});
            var xml=XDocument.Load(reader);var root=xml.Root;
            var profile=root?.Elements("ActionProfiles").SingleOrDefault();
            if(root?.Name!="ActionMaps"||root.Elements().Count()!=1||profile is null||
                (string?)profile.Attribute("version")!="1"||(string?)profile.Attribute("optionsVersion")!="2"||
                (string?)profile.Attribute("rebindVersion")!="2"||(string?)profile.Attribute("profileName")!="default")
                throw new InvalidDataException("This active-controls schema has not been verified. Recovery was not attempted.");
            return profile.Elements("actionmap").Elements("action").Elements("rebind").Count(e=>IsBound((string?)e.Attribute("input")));
        }
        catch(XmlException){throw new InvalidDataException("The active controls XML is malformed or contains a prohibited document definition.");}
        catch(InvalidOperationException){throw new InvalidDataException("The active controls XML has ambiguous profiles.");}
    }
    private static string CheckedPath(string live)
    {
        if(!GameFolder.IsLive(live))throw new InvalidDataException("Select an existing LIVE installation.");
        var target=Path.GetFullPath(FilePath(live));
        for(var dir=new DirectoryInfo(Path.GetDirectoryName(target)!);dir is not null;dir=dir.Parent)
            if(dir.Exists&&dir.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new IOException("A controls folder is a link. Recovery was not attempted.");
        foreach(var file in new[]{target,target+".stardad-recovery-new",target+".stardad-before-recovery",target+".stardad-before-new"})
            if(File.Exists(file)&&File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))throw new IOException("A controls destination is a link. Recovery was not attempted.");
        return target;
    }
    private static byte[] ReadBounded(string path)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length>MaxBytes)throw new InvalidDataException("The active controls file exceeds 1 MiB.");
        using var output=new MemoryStream();var buffer=new byte[8192];int read;
        while((read=stream.Read(buffer))>0){if(output.Length+read>MaxBytes)throw new InvalidDataException("The active controls file grew beyond its limit.");output.Write(buffer,0,read);}
        return output.ToArray();
    }
    private static void ValidateBaseline(ControlRecoveryState state,string account,string live,string? hardware)
    {
        if(state.Account!=account||!Path.GetFullPath(live).Equals(state.Folder,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This recovery baseline belongs to a different account or installation.");
        if(!ValidHash(hardware)||hardware!=state.Hardware)
            throw new InvalidOperationException("Connected devices changed or could not be verified. Review the devices before restoring.");
        if(!ValidBuild(state.Build)||!ValidHash(state.Sha256)||state.Data is not {Length:>0 and <=MaxBytes}||Hash(state.Data)!=state.Sha256||BindingCount(state.Data)==0)
            throw new InvalidDataException("The recovery baseline failed validation. No game file was changed.");
    }

    /// <summary>Explicitly confirmed recovery also handles same-build resets/conflicts; never runs during a game.</summary>
    public static ControlRecoveryResult RestoreNow(ControlRecoveryState state,string account,string live,string? hardware,bool confirmed,Func<bool> gameRunning)
    {
        if(!confirmed)return new("Restore cancelled. No files were changed.");
        ValidateBaseline(state,account,live,hardware);
        if(gameRunning())throw new IOException("Close the game before restoring active controls.");
        var build=GameFolder.Build(live);if(!ValidBuild(build))throw new InvalidDataException("The installed build could not be verified.");
        var target=CheckedPath(live);var current=File.Exists(target)?ReadBounded(target):null;
        if(current is {Length:>0})BindingCount(current); // Preserve malformed/unknown files for manual review.
        Restore(state,live,build!,current,gameRunning);
        state.RestoredBuild=build;state.GameSeenAfterRestore=false;
        state.Status="Controls restored. Start the game and check your bindings.";
        return new(state.Status,true,true);
    }

    /// <summary>Undo is an explicit closed-game action. Disable automatic recovery to avoid immediately reapplying it.</summary>
    public static ControlRecoveryResult Undo(ControlRecoveryState state,string account,string live,string? hardware,bool confirmed,Func<bool> gameRunning)
    {
        if(!confirmed)return new("Undo cancelled. No files were changed.");
        ValidateBaseline(state,account,live,hardware);
        if(gameRunning())throw new IOException("Close the game before undoing recovery.");
        if(state.PreviousSha256 is null)throw new InvalidOperationException("The last recovery replaced a missing file, so there is no previous controls file to restore. Your last-good baseline is retained.");
        var target=CheckedPath(live);var previous=ReadBounded(target+".stardad-before-recovery");BindingCount(previous);
        if(!ValidHash(state.PreviousSha256)||Hash(previous)!=state.PreviousSha256)throw new IOException("The previous-controls copy could not be verified. No files were replaced.");
        var current=File.Exists(target)?ReadBounded(target):null;
        if(current is null||Hash(current)!=state.Sha256)throw new IOException("Active controls changed since recovery. They were kept; review them before undoing.");
        var build=GameFolder.Build(live);if(!ValidBuild(build))throw new InvalidDataException("The installed build could not be verified.");
        Restore(new(){Data=previous,Sha256=Hash(previous),PreviousSha256=state.PreviousSha256},live,build!,current,gameRunning);
        state.PreviousSha256=Hash(current);
        state.Enabled=false;state.RestoredBuild=null;state.GameSeenAfterRestore=false;
        state.Status="Restore undone. Your backup is still saved.";
        return new(state.Status,true,true);
    }
    public static ControlRecoveryState Capture(string account,string live,string hardware,DateTimeOffset now,Func<bool> gameRunning,ControlRecoveryState? priorState=null)
    {
        if(string.IsNullOrWhiteSpace(account)||!ValidHash(hardware))throw new InvalidOperationException("Link an account and verify connected devices first.");
        if(gameRunning())throw new IOException("Close the game before capturing active controls.");
        var build=GameFolder.Build(live);if(!ValidBuild(build))throw new InvalidDataException("The installed build could not be verified.");
        var target=CheckedPath(live);var data=ReadBounded(target);
        if(BindingCount(data)==0)throw new InvalidDataException("No custom active bindings were found. Set and save a binding in game first.");
        if(gameRunning()||GameFolder.Build(live)!=build||Hash(ReadBounded(target))!=Hash(data))throw new IOException("The game or controls changed during capture. Try again after closing it.");
        string? previous=null;
        if(priorState is {} known&&known.Account==account&&Path.GetFullPath(live).Equals(known.Folder,StringComparison.OrdinalIgnoreCase)&&ValidHash(known.PreviousSha256)&&File.Exists(target+".stardad-before-recovery")&&Hash(ReadBounded(target+".stardad-before-recovery"))==known.PreviousSha256)previous=known.PreviousSha256;
        return new(){Enabled=true,Account=account,Folder=Path.GetFullPath(live),Hardware=hardware,Build=build!,Sha256=Hash(data),Data=data,PreviousSha256=previous,
            ObservedBuild=build,ObservedSince=now,Status=$"Controls backed up {now.ToLocalTime():d MMM yyyy, h:mm tt}. Restore them here if a patch resets them."};
    }
    public static ControlRecoveryResult Tick(ControlRecoveryState state,string account,string live,string? hardware,DateTimeOffset now,Func<bool> gameRunning)
    {
        ControlRecoveryResult Say(string message,bool changed=false,bool restored=false,bool attention=false)
        {var different=state.Status!=message;state.Status=message;return new(message,changed||different,restored,attention);}
        if(!state.Enabled)return new("Automatic controls recovery is off.");
        if(state.Account!=account||!Path.GetFullPath(live).Equals(state.Folder,StringComparison.OrdinalIgnoreCase))return Say("Recovery belongs to a different account or installation. Review Settings.",attention:true);
        if(!ValidHash(hardware)||hardware!=state.Hardware)return Say("Connected devices changed or could not be verified. Review active controls before re-enabling recovery.",attention:true);
        ValidateBaseline(state,account,live,hardware);
        var build=GameFolder.Build(live);if(!ValidBuild(build))return Say("Waiting for a verified installed build.");
        if(gameRunning())
        {
            var changed=state.RestoredBuild==build&&!state.GameSeenAfterRestore;
            if(changed)state.GameSeenAfterRestore=true;
            return Say("Recovery waits until the game is closed.",changed);
        }
        if(state.ObservedBuild!=build){state.ObservedBuild=build;state.ObservedSince=now;return Say("Patch detected. Waiting for the installation to settle.",true);}
        if(now-state.ObservedSince<SettleTime)return Say("Waiting for a stable installed build.");
        var target=CheckedPath(live);var current=File.Exists(target)?ReadBounded(target):null;
        if(current is {Length:>0}&&BindingCount(current)>0)
        {
            var hash=Hash(current);
            if(hash!=state.Sha256&&build!=state.Build&&!(state.RestoredBuild==build&&state.GameSeenAfterRestore))
                return Say("This patch has a different populated control profile. Both versions were kept; review them in Settings.",attention:true);
            // Same-build user edits, or edits after a restored profile has been used, become the new baseline.
            var capture=hash!=state.Sha256||(state.Build!=build&&(state.RestoredBuild!=build||state.GameSeenAfterRestore));
            if(capture){state.Data=current;state.Sha256=hash;state.Build=build!;state.RestoredBuild=null;state.GameSeenAfterRestore=false;}
            return Say("Active controls are present. Your last good baseline is kept.",capture);
        }
        if(ulong.Parse(build!)<=ulong.Parse(state.Build))return Say("Controls are missing or reset, but no newer patch is confirmed. Use an explicit restore.",attention:true);
        if(File.Exists(target)&&now-new DateTimeOffset(File.GetLastWriteTimeUtc(target))<SettleTime)return Say("The controls file is still changing. Waiting before recovery.");
        Restore(state,live,build!,current,gameRunning);
        state.RestoredBuild=build;state.GameSeenAfterRestore=false;
        return Say("Active controls restored and file hash verified. They will load on the next game start.",true,true);
    }
    private static void Restore(ControlRecoveryState state,string live,string build,byte[]? expected,Func<bool> gameRunning)
    {
        if(expected is null)state.PreviousSha256=null;
        var target=CheckedPath(live);Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary=target+".stardad-recovery-new";
        if(File.Exists(temporary))
        {
            if(Hash(ReadBounded(temporary))!=state.Sha256)throw new IOException("A different recovery temporary file exists. No files were replaced.");
        }
        else using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(state.Data);file.Flush(true);}
        // Check all inputs again immediately before the atomic replacement; never win a race against user edits.
        CheckedPath(live);var present=File.Exists(target)?ReadBounded(target):null;
        if((present is null)!=(expected is null)||(present is not null&&Hash(present)!=Hash(expected!))||gameRunning()||GameFolder.Build(live)!=build)
            throw new IOException("The game, patch or controls changed during recovery. The original was kept.");
        if(present is not null)
        {
            var prior=target+".stardad-before-recovery";
            if(File.Exists(prior)&&Hash(ReadBounded(prior))!=Hash(present)&&Hash(ReadBounded(prior))!=state.PreviousSha256)
                throw new IOException("A previous-controls copy changed or is not recognized. It was kept for review.");
            if(!File.Exists(prior)||Hash(ReadBounded(prior))!=Hash(present))
            {
                var priorTemp=target+".stardad-before-new";
                if(File.Exists(priorTemp)){if(Hash(ReadBounded(priorTemp))!=Hash(present))throw new IOException("A different previous-controls temporary file exists.");}
                else using(var file=new FileStream(priorTemp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(present);file.Flush(true);}
                File.Move(priorTemp,prior,true);
            }
            state.PreviousSha256=Hash(present);
        }
        CheckedPath(live);var final=File.Exists(target)?ReadBounded(target):null;
        if((final is null)!=(expected is null)||(final is not null&&Hash(final)!=Hash(expected!))||gameRunning()||GameFolder.Build(live)!=build)
            throw new IOException("The game, patch or controls changed. Recovery is waiting; the original was kept.");
        File.Move(temporary,target,true);
        if(Hash(ReadBounded(target))!=state.Sha256)throw new IOException("The restored controls did not pass verification. The baseline is retained.");
    }
}
