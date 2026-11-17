$src = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public class Nvml2 {
    [DllImport("nvml.dll")] public static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetHandleByIndex_v2(int index, out IntPtr dev);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetPowerUsage(IntPtr dev, out uint mw);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetPowerManagementLimit(IntPtr dev, out uint mw);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr dev, out uint mw);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetTotalEnergyConsumption(IntPtr dev, out ulong mj);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetFanSpeedRPM(IntPtr dev, out uint rpm);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetClockInfo(IntPtr dev, int type, out uint mhz);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetTemperatureThreshold(IntPtr dev, int t, out uint c);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetPciInfo_v3(IntPtr dev, out Pci p);
    [StructLayout(LayoutKind.Sequential)] public struct Pci { public uint bus; public uint busId; public IntPtr domain; [MarshalAs(UnmanagedType.ByValArray, SizeConst=8)] public uint[] bdf; }
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetComputeRunningProcesses(IntPtr dev, out uint n, IntPtr info);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetGraphicsRunningProcesses(IntPtr dev, out uint n, IntPtr info);
}
'@
Add-Type -TypeDefinition $src
[Nvml2]::nvmlInit_v2() | Out-Null
$dev = [IntPtr]::Zero
[Nvml2]::nvmlDeviceGetHandleByIndex_v2(0, [ref]$dev) | Out-Null

$p = [uint32]0; $r = [Nvml2]::nvmlDeviceGetPowerUsage($dev, [ref]$p); Write-Output "PowerUsage rc=$r val=$p"
$p2 = [uint32]0; $r = [Nvml2]::nvmlDeviceGetPowerManagementLimit($dev, [ref]$p2); Write-Output "PowerMgmtLimit rc=$r val=$p2"
$p3 = [uint32]0; $r = [Nvml2]::nvmlDeviceGetEnforcedPowerLimit($dev, [ref]$p3); Write-Output "EnforcedPowerLimit rc=$r val=$p3"
$e = [uint64]0; $r = [Nvml2]::nvmlDeviceGetTotalEnergyConsumption($dev, [ref]$e); Write-Output "Energy rc=$r val=$e mJ"
$fr = [uint32]0; $r = [Nvml2]::nvmlDeviceGetFanSpeedRPM($dev, [ref]$fr); Write-Output "FanRPM rc=$r val=$fr"
foreach ($t in 0,1,2,3) { $c=[uint32]0; $r=[Nvml2]::nvmlDeviceGetTemperatureThreshold($dev,$t,[ref]$c); Write-Output "TempThreshold[$t] rc=$r val=$c" }
foreach ($cl in 0..5) { $m=[uint32]0; $r=[Nvml2]::nvmlDeviceGetClockInfo($dev,$cl,[ref]$m); Write-Output "Clock[$cl] rc=$r val=$m" }
