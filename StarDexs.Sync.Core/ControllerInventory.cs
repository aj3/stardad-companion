using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace StarDexs.Sync.Core;

public sealed record ControllerFingerprint(string Hash,int Controllers);

/// <summary>Device metadata only. Never registers for, reads, injects or records input events.</summary>
public static class ControllerInventory
{
    [StructLayout(LayoutKind.Sequential)] private struct Device { public nint Handle;public uint Type; }
    [StructLayout(LayoutKind.Explicit,Size=32)] private struct Info
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Type;
        [FieldOffset(20)] public ushort UsagePage;
        [FieldOffset(22)] public ushort Usage;
    }
    [DllImport("user32.dll",SetLastError=true)]private static extern uint GetRawInputDeviceList([Out]Device[]? list,ref uint count,uint size);
    [DllImport("user32.dll",EntryPoint="GetRawInputDeviceInfoW",SetLastError=true,CharSet=CharSet.Unicode)]private static extern uint DeviceName(nint device,uint command,StringBuilder? text,ref uint count);
    [DllImport("user32.dll",EntryPoint="GetRawInputDeviceInfoW",SetLastError=true)]private static extern uint DeviceInfo(nint device,uint command,ref Info info,ref uint count);
    public static ControllerFingerprint? Read()
    {
        if(!OperatingSystem.IsWindows())return null;
        uint count=0;var size=(uint)Marshal.SizeOf<Device>();
        if(GetRawInputDeviceList(null,ref count,size)==uint.MaxValue||count>256)return null;
        var devices=new Device[count];var found=GetRawInputDeviceList(devices,ref count,size);
        if(found==uint.MaxValue||found>devices.Length)return null;
        var names=new List<string>();var controllers=0;
        foreach(var device in devices.Take((int)found))
        {
            uint chars=0;if(DeviceName(device.Handle,0x20000007,null,ref chars)==uint.MaxValue||chars is 0 or >8192)return null;
            var name=new StringBuilder((int)chars);if(DeviceName(device.Handle,0x20000007,name,ref chars)==uint.MaxValue)return null;
            names.Add(device.Type+":"+name.ToString().TrimEnd('\0').ToUpperInvariant());
            if(device.Type==2)
            {
                var info=new Info{Size=32};uint bytes=32;
                if(DeviceInfo(device.Handle,0x2000000b,ref info,ref bytes)==uint.MaxValue)return null;
                if(info.UsagePage==1&&info.Usage is 4 or 5 or 8)controllers++;
            }
        }
        names.Sort(StringComparer.Ordinal);
        return new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",names)))),controllers);
    }
}
