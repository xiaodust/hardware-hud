$ErrorActionPreference = 'Continue'
Write-Output "=== perf categories containing GPU ==="
try { [Diagnostics.PerformanceCounterCategory]::GetCategories() | Where-Object { $_.CategoryName -match 'GPU' } | Select-Object -ExpandProperty CategoryName } catch { "err" }

Write-Output "`n=== GPU Engine instances ==="
try {
  $names = (New-Object Diagnostics.PerformanceCounterCategory('GPU Engine')).GetInstanceNames()
  Write-Output "count=$($names.Count)"
  $names | Select-Object -First 8
  $counters = (New-Object Diagnostics.PerformanceCounterCategory('GPU Engine')).GetCounters()
  Write-Output "counters: $($counters.CounterName -join ' | ')"
} catch { "GPU Engine err: $($_.Exception.Message)" }

Write-Output "`n=== GPU Adapter Memory ==="
try {
  $cat = New-Object Diagnostics.PerformanceCounterCategory('GPU Adapter Memory')
  $cat.GetInstanceNames() | Select-Object -First 6
  Write-Output "counters: $((New-Object Diagnostics.PerformanceCounterCategory('GPU Adapter Memory')).GetCounters().CounterName -join ' | ')"
} catch { "err: $($_.Exception.Message)" }

Write-Output "`n=== WMI GPU perf class props ==="
try {
  Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine -ErrorAction Stop |
    Select-Object -First 2 | Get-Member -MemberType Property | Select-Object -ExpandProperty Name
} catch { "wmi err: $($_.Exception.Message)" }

Write-Output "`n=== Processor Information counters ==="
$pi = Get-Counter -ListSet 'Processor Information' -ErrorAction SilentlyContinue
if ($pi) { $pi.Counter -join ' | ' } else { "no ListSet" }
