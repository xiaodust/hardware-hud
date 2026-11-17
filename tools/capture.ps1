# 截取当前屏幕，用于视觉自评
param([string]$Out = (Join-Path $PSScriptRoot "shot.png"))

Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Cap {
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
}
"@

$w = [Win32Cap]::GetSystemMetrics(0)
$h = [Win32Cap]::GetSystemMetrics(1)

$bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(0, 0, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"screen $w x $h -> $Out"
