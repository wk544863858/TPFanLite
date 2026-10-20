# deploy.ps1 —— 把仓库里构建好的 TPFanLite 部署到安装目录并重启
# 步骤：优雅停止旧实例（程序自己把风扇写回 0x80）→ 备份旧 exe → 复制 exe 与依赖 → 启动
# 注意：TPFanLite 不需要管理员权限（EC 访问与 PL1 钉住在非提权下实测正常）。
param(
  [string]$Repo = $PSScriptRoot,
  [string]$DestDir = (Join-Path $env:LOCALAPPDATA 'TPFanLite'),
  [switch]$Tray
)
$ErrorActionPreference = 'Stop'

$exe = Join-Path $Repo 'TPFanLite.exe'
if (-not (Test-Path $exe)) { throw "缺少 $exe（先运行 build.ps1 构建）" }
if (-not (Test-Path $DestDir)) { New-Item -ItemType Directory -Force -Path $DestDir | Out-Null }

Write-Host '1) 优雅停止旧实例 ...'
$procs = @(Get-Process -Name 'TPFanLite', 'TPFanLite-debug' -ErrorAction SilentlyContinue)
foreach ($p in $procs) { try { $p.CloseMainWindow() | Out-Null } catch { } }
Start-Sleep -Seconds 2
foreach ($p in $procs) { try { if (-not $p.HasExited) { $p.Kill(); Write-Host "  强制结束 PID $($p.Id)" } } catch { } }
Start-Sleep -Milliseconds 500

Write-Host '2) 备份旧 exe ...'
$newExe = Join-Path $DestDir 'TPFanLite.exe'
if (Test-Path $newExe) {
  $bak = "$newExe.bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
  Copy-Item $newExe $bak -Force
  Write-Host "  备份: $bak"
}

Write-Host '3) 复制文件 ...'
foreach ($f in @('TPFanLite.exe', 'TVicPort.dll', 'TPFanLite.ico')) {
  $src = Join-Path $Repo $f
  if (Test-Path $src) { Copy-Item $src (Join-Path $DestDir $f) -Force; Write-Host "  $f" }
}
$ini = Join-Path $DestDir 'TPFanLite.ini'
if (-not (Test-Path $ini)) { Copy-Item (Join-Path $Repo 'TPFanLite.ini') $ini; Write-Host '  TPFanLite.ini（首次部署）' }

Write-Host '4) 启动 ...'
if ($Tray) { Start-Process -FilePath $newExe -WorkingDirectory $DestDir -ArgumentList '--tray' }
else { Start-Process -FilePath $newExe -WorkingDirectory $DestDir }
Start-Sleep -Seconds 3
$running = @(Get-Process -Name 'TPFanLite' -ErrorAction SilentlyContinue)
if ($running.Count -eq 0) { Write-Host '警告：启动后未发现进程，请手动双击 exe 查看错误' }
else { Write-Host ('运行中: ' + (($running | ForEach-Object { "PID $($_.Id)" }) -join ', ')) }