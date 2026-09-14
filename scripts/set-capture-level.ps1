<#
.SYNOPSIS
    Read or set a Windows capture device's level, from the command line.

.DESCRIPTION
    A recording that clips decodes as noise, and the CW bench is worthless until
    the level is right. Windows only exposes the slider in Sound Settings, which
    makes "record, check, adjust, record again" a trip through the GUI every
    time. This does it in one line, so scripts/cw-bench-record.ps1 can set the
    level itself and a bench run is reproducible.

    For a USB audio class device - which is what both radios present - the
    scalar this sets is passed through to the device's own volume control unit,
    so it takes effect before the samples reach the PC and genuinely prevents
    clipping, rather than scaling down something already clipped.

.EXAMPLE
    .\scripts\set-capture-level.ps1 -Device "USB Audio Device"
    Report the current level.

.EXAMPLE
    .\scripts\set-capture-level.ps1 -Device "USB Audio Device" -Percent 30
    Set it to 30%.
#>
# Keep this file ASCII - see the note in cw-bench-record.ps1 for why.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Device,
    [ValidateRange(0, 100)][int] $Percent = -1
)

$ErrorActionPreference = 'Stop'

# The Core Audio API has no PowerShell surface and no cmdlet ships with Windows,
# so the interop is declared here. Only the two calls this script needs are
# bound; the rest of each interface is padded with placeholders so the vtable
# slots line up, which is why the unused methods have no names worth reading.
Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace YwcAudio
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        int NotImpl1();
        int NotImpl2();
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
                     [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        int NotImpl1();
        int NotImpl2();
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        int GetMasterVolumeLevel(out float levelDb);
        int GetMasterVolumeLevelScalar(out float level);
    }

    public static class EndpointVolume
    {
        public static float Get(string deviceId)
        {
            float level;
            Volume(deviceId).GetMasterVolumeLevelScalar(out level);
            return level;
        }

        public static void Set(string deviceId, float level)
        {
            var ctx = Guid.Empty;
            var hr = Volume(deviceId).SetMasterVolumeLevelScalar(level, ref ctx);
            if (hr != 0) throw new COMException("SetMasterVolumeLevelScalar failed", hr);
        }

        private static IAudioEndpointVolume Volume(string deviceId)
        {
            var enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumerator());
            IMMDevice device;
            var hr = enumerator.GetDevice(deviceId, out device);
            if (hr != 0) throw new COMException("GetDevice failed for " + deviceId, hr);

            var iid = typeof(IAudioEndpointVolume).GUID;
            object iface;
            hr = device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out iface);
            if (hr != 0) throw new COMException("Activate(IAudioEndpointVolume) failed", hr);
            return (IAudioEndpointVolume)iface;
        }
    }
}
'@

# The MMDevice id is the endpoint's PnP instance id with the SWD\MMDEVAPI\
# prefix taken off, so the friendly name the user typed can be resolved without
# touching IPropertyStore and its PROPVARIANT marshalling.
# The data-flow direction is in the id: {0.0.1.*} is capture, {0.0.0.*} is
# render. Without this filter "USB Audio Device" matches the speakers too and
# the script refuses a name that is in fact unambiguous for a recording.
$endpoints = @(Get-PnpDevice -Class AudioEndpoint -Status OK |
               Where-Object { $_.InstanceId -like '*{0.0.1.00000000}*' -and
                              $_.FriendlyName -like "*$Device*" })

if ($endpoints.Count -eq 0) { throw "No active capture endpoint matching '$Device'." }
if ($endpoints.Count -gt 1) {
    $names = ($endpoints | ForEach-Object { $_.FriendlyName }) -join "`n  "
    throw "'$Device' matches more than one endpoint:`n  $names"
}

$name = $endpoints[0].FriendlyName
$id   = $endpoints[0].InstanceId -replace '^[^{]*', ''

if ($Percent -ge 0) {
    [YwcAudio.EndpointVolume]::Set($id, $Percent / 100.0)
}

$now = [YwcAudio.EndpointVolume]::Get($id)
Write-Host ("{0}: {1:F0}%" -f $name, ($now * 100))
