# Builds dist\ClaudeMonitor.exe with the C# compiler that ships with .NET Framework 4.x (preinstalled on Windows 10/11).
# -Demo builds a variant with fake accounts, used for the README screenshots.
param([switch]$Demo, [string]$Out)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$repo = Split-Path $root -Parent
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
if (-not $Out) { $Out = "$repo\dist\ClaudeMonitor.exe" }
New-Item -ItemType Directory -Force (Split-Path $Out -Parent) | Out-Null
$obj = "$root\obj"
New-Item -ItemType Directory -Force $obj | Out-Null
Add-Type -AssemblyName System.Drawing

# Version shared with the Android app.
$version = (Get-Content "$repo\VERSION" -Raw).Trim()
@"
[assembly: System.Reflection.AssemblyTitle("Claude Monitor")]
[assembly: System.Reflection.AssemblyProduct("Claude Monitor")]
[assembly: System.Reflection.AssemblyVersion("$version.0")]
[assembly: System.Reflection.AssemblyFileVersion("$version.0")]
namespace ClaudeMonitor { static class AppInfo { public const string Version = "$version"; } }
"@ | Set-Content "$obj\Version.cs" -Encoding UTF8

# Generate app.ico (Claude-style starburst) with PNG frames.
function New-BurstPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(0xD9, 0x77, 0x57)), ([Math]::Max(1.6, $size * 0.12))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $c = $size / 2; $inner = $size * 0.06
    for ($i = 0; $i -lt 10; $i++) {
        $a = $i * [Math]::PI / 5 + 0.3
        $len = $size * $(if ($i % 2 -eq 0) { 0.44 } else { 0.36 })
        $g.DrawLine($pen, [float]($c + [Math]::Cos($a) * $inner), [float]($c + [Math]::Sin($a) * $inner),
            [float]($c + [Math]::Cos($a) * $len), [float]($c + [Math]::Sin($a) * $len))
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 256
$pngs = $sizes | ForEach-Object { , (New-BurstPng $_) }
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[IO.File]::WriteAllBytes("$obj\app.ico", $ico.ToArray())

$defines = @()
if ($Demo) { $defines += '/define:DEMO' }
& $csc /nologo /target:winexe /optimize+ /platform:anycpu "/win32icon:$obj\app.ico" "/out:$Out" @defines `
    "/r:$fw\System.Web.Extensions.dll" /r:System.Security.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    "$root\ClaudeMonitor.cs" "$obj\Version.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "OK: $Out ($version)"
