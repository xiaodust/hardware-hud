# 逐个类别探测 PerformanceCounter 是否可读，并测一次性读取的开销。
# 用途：对照程序里 PDH 英文计数器路径，确认某一项读数失败是「计数器缺失」还是「用法不对」。
$ErrorActionPreference = 'Continue'

# 类别 -> (计数器名, 是否有实例)
$cats = @(
  @{ Cat = 'Processor Information'; Counter = '% Processor Time';    Instance = '_Total' },
  @{ Cat = 'Processor Information'; Counter = 'Processor Frequency'; Instance = '_Total' },
  @{ Cat = 'Memory';                Counter = 'Committed Bytes';     Instance = $null },
  @{ Cat = 'Memory';                Counter = 'Available MBytes';   Instance = $null },
  @{ Cat = 'PhysicalDisk';          Counter = 'Disk Read Bytes/sec'; Instance = '_Total' },
  @{ Cat = 'PhysicalDisk';          Counter = 'Disk Write Bytes/sec'; Instance = '_Total' },
  @{ Cat = 'System';                Counter = 'Processes';          Instance = $null },
  @{ Cat = 'Process';               Counter = '% Processor Time';   Instance = '_Total' }
)

foreach ($c in $cats) {
  $label = if ($c.Instance) { "$($c.Cat)\$($c.Counter)($($c.Instance))" } else { "$($c.Cat)\$($c.Counter)" }
  try {
    $pc = if ($c.Instance) {
      New-Object Diagnostics.PerformanceCounter($c.Cat, $c.Counter, $c.Instance)
    } else {
      New-Object Diagnostics.PerformanceCounter($c.Cat, $c.Counter)
    }
    $null = $pc.NextValue()          # 第一读只是建基线，多半是 0
    Start-Sleep -Milliseconds 200
    $v = $pc.NextValue()
    Write-Output ("OK   {0} = {1}" -f $label, $v)
  } catch {
    Write-Output ("FAIL {0}: {1}" -f $label, $_.Exception.Message)
  }
}

Write-Output ""
Write-Output "--- 逐核读取开销（本机逻辑处理器数）---"
$n = [Environment]::ProcessorCount
Write-Output "Environment.ProcessorCount = $n"

$sw = [Diagnostics.Stopwatch]::StartNew()
$cores = 0..($n - 1) | ForEach-Object { New-Object Diagnostics.PerformanceCounter('Processor', '% Processor Time', "$($_)") }
Write-Output ("创建 {0} 个计数器: {1} ms" -f $n, $sw.ElapsedMilliseconds)

$sw.Restart()
$null = $cores | ForEach-Object { $_.NextValue() }
Write-Output ("首次读取 {0} 个: {1} ms" -f $n, $sw.ElapsedMilliseconds)

Start-Sleep -Milliseconds 250
$sw.Restart()
$vals = $cores | ForEach-Object { [math]::Round($_.NextValue(), 1) }
Write-Output ("第二次读取 {0} 个: {1} ms -> {2}" -f $n, $sw.ElapsedMilliseconds, ($vals -join ' '))

Write-Output ""
Write-Output "--- CPU 频率 ---"
try {
  $f = New-Object Diagnostics.PerformanceCounter('Processor Information', 'Processor Frequency', '_Total')
  $null = $f.NextValue()
  Start-Sleep -Milliseconds 300
  Write-Output ("Processor Frequency = {0} MHz" -f $f.NextValue())
} catch { Write-Output "freq FAIL: $($_.Exception.Message)" }

Write-Output ""
Write-Output "--- 内存（WMI 对照）---"
try {
  $os = Get-CimInstance Win32_OperatingSystem
  Write-Output ("TotalVisibleMemorySize = {0:N1} GB, FreePhysicalMemory = {1:N1} GB" -f ($os.TotalVisibleMemorySize / 1MB), ($os.FreePhysicalMemory / 1MB))
} catch { Write-Output "wmi FAIL: $($_.Exception.Message)" }
