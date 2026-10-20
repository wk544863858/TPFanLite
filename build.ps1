# TPFanLite 构建脚本：从 src/ 编译出 TPFanLite.exe（-Debug 时额外产 TPFanLite-debug.exe）
# 用法:  powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 [-Debug] [-NoDriver]
#        默认只产 TPFanLite.exe；调试版加 -Debug
param([switch]$Debug, [switch]$NoDriver)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src'
$csc  = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
$ico  = Join-Path $src 'tpfanlite.ico'
$sysRes = Join-Path $root 'assets\TVicPort64.sys'

# src/ 下所有 .cs（Cfg/Ec/Hw/Bios/MainForm/Program …），按名排序保证编译顺序稳定
$sources = @(Get-ChildItem -Path $src -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw "no sources in $src" }
if (-not (Test-Path $ico)) { throw "icon not found: $ico" }
if (-not (Test-Path $csc)) { throw "csc not found: $csc" }

$common = @(
    '/nologo', '/platform:x86', '/codepage:65001', '/optimize+',
    "/win32icon:$ico",
    "/resource:$ico,TPFanLite.ico",
    '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Management.dll'
)

# 驱动默认内嵌（方便最终用户开箱自装）；-NoDriver 时不内嵌，运行时从程序目录读取 TVicPort64.sys
if (-not $NoDriver) {
    if (-not (Test-Path $sysRes)) { throw "driver sys not found: $sysRes（无该文件请加 -NoDriver 构建）" }
    $common += "/resource:$sysRes,TVicPort64.sys"
} else {
    Write-Host "注意：-NoDriver 构建不含内嵌驱动，运行时需把 TVicPort64.sys 放到程序目录"
}

& $csc @common '/target:winexe' "/out:$root\TPFanLite.exe" $sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Debug) {
    & $csc @common '/target:exe' "/out:$root\TPFanLite-debug.exe" $sources
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Output "OK: $root\TPFanLite.exe"
if ($Debug) { Write-Output "OK: $root\TPFanLite-debug.exe" }
