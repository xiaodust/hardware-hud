$src = @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public class NvmlProbe {
    [DllImport("nvml.dll")] public static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetCount_v2(out int count);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetHandleByIndex_v2(int index, out IntPtr dev);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetName(IntPtr dev, StringBuilder name, int len);
    public struct Utilization { public uint Gpu; public uint Memory; }
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetUtilizationRates(IntPtr dev, out Utilization u);
    public struct MemInfo { public ulong Total; public ulong Free; public ulong Used; }
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetMemoryInfo(IntPtr dev, out MemInfo m);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetTemperature(IntPtr dev, int sensor, out uint temp);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetPowerUsage(IntPtr dev, out uint milliW);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetFanSpeed(IntPtr dev, out uint percent);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetClockInfo(IntPtr dev, int type, out uint mhz);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetPowerManagementLimit(IntPtr dev, out uint milliW);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetNumFans(IntPtr dev, out uint n);
}
"@
Add-Type -TypeDefinition $src
$rc = [NvmlProbe]::nvmlInit_v2()
Write-Output "init rc=$rc"
$count = 0
[NvmlProbe]::nvmlDeviceGetCount_v2([ref]$count) | Out-Null
Write-Output "count=$count"
for ($i = 0; $i -lt $count; $i++) {
    $dev = [IntPtr]::Zero
    [NvmlProbe]::nvmlDeviceGetHandleByIndex_v2($i, [ref]$dev) | Out-Null
    $sb = New-Object Text.StringBuilder 64
    [NvmlProbe]::nvmlDeviceGetName($dev, $sb, 64) | Out-Null
    $u = New-Object NvmlProbe+Utilization
    $r1 = [NvmlProbe]::nvmlDeviceGetUtilizationRates($dev, [ref]$u)
    $m = New-Object NvmlProbe+MemInfo
    $r2 = [NvmlProbe]::nvmlDeviceGetMemoryInfo($dev, [ref]$m)
    $t = 0; $r3 = [NvmlProbe]::nvmlDeviceGetTemperature($dev, 0, [ref]$t)
    $p = 0; $r4 = [NvmlProbe]::nvmlDeviceGetPowerUsage($dev, [ref]$p)
    $f = 0; $r5 = [NvmlProbe]::nvmlDeviceGetFanSpeed($dev, [ref]$f)
    $nf = 0; $r6 = [NvmlProbe]::nvmlDeviceGetNumFans($dev, [ref]$nf)
    $c = 0; $r7 = [NvmlProbe]::nvmlDeviceGetClockInfo($dev, 0, [ref]$c)
    Write-Output ("GPU{0} {1} | util={2}/{3} rc={4} | mem={5}/{6} MiB rc={7} | temp={8}C rc={9} | power={10}W rc={11} | fan={12}% (fans={13}) rc={14} | coreClock={15}MHz rc={16}" -f `
        $i, $sb.ToString(), $u.Gpu, $u.Memory, $r1, [math]::Round($m.Used/1MB), [math]::Round($m.Total/1MB), $r2, $t, $r3, ($p/1000), $r4, $f, $nf, $r5, $c, $r7)
}
