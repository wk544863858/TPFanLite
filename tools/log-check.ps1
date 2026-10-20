# ============ 日志体检：diag-v4 CSV 一致性与统计（复盘用） ============
# 用法:
#   pwsh -File tools\log-check.ps1                    # 自动找最新 diag-v4-*.csv
#   pwsh -File tools\log-check.ps1 -Path <csv>        # 指定文件
#   pwsh -File tools\log-check.ps1 -Top 20            # 列出最长的 N 段全速/高温事件
param(
    [string]$Path = '',
    [string]$Ini = '',
    [int]$Top = 10
)
$ErrorActionPreference = 'Stop'
$dir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TPFanLite'
if (-not $Path) {
    $Path = Get-ChildItem $dir -Filter 'diag-v4-*.csv' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $Path -or -not (Test-Path $Path)) { throw "找不到 diag-v4 CSV（先确认 ini DiagLog=True 且程序运行过）" }
if (-not $Ini) { $Ini = Join-Path $dir 'TPFanLite.ini' }

# ---- 读取当前 ini 阈值（用于核对阶梯级与 PL1 是否自洽）----
$cfg = @{ FanFull = 70; PowerLow = 70; PowerHigh = 98; Steps = 3; MaxW = 35.0; MinW = 20.0 }
if (Test-Path $Ini) {
    foreach ($line in Get-Content $Ini) {
        if ($line -match '^\s*([A-Za-z]+)\s*=\s*(.+?)\s*$') {
            $k = $Matches[1].ToLower(); $v = $Matches[2]
            switch ($k) {
                'tempfanfull'    { $cfg.FanFull = [double]$v }
                'temppowerlow'   { $cfg.PowerLow = [double]$v }
                'temppowerhigh'  { $cfg.PowerHigh = [double]$v }
                'powersteps'     { $cfg.Steps = [int]$v }
                'pinpl1maxw'     { $cfg.MaxW = [double]$v }
                'pinpl1minw'     { $cfg.MinW = [double]$v }
            }
        }
    }
}

$rows = New-Object System.Collections.ArrayList
$bad = 0; $lineNo = 0; $header = $null
foreach ($line in Get-Content $Path) {
    $lineNo++
    if ($lineNo -eq 1) { $header = $line; continue }
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $p = $line -split ','
    if ($p.Count -ne 20) { $bad++; continue }
    $ts = 0L
    [void][long]::TryParse($p[1], [ref]$ts)
    [void]$rows.Add([pscustomobject]@{
        t = $p[0]; ts = $ts
        cpu = [int]$p[2]; rpm = [int]$p[6]; fc = $p[7]
        pl1 = [double]$p[8]; pin = [double]$p[9]; pinState = [int]$p[10]
        level = [int]$p[11]; ema = [double]$p[12]; busy = [double]$p[13]
        fan = [int]$p[14]; base = $p[15]; applied = $p[16]
        status = [int]$p[17]; onAc = [int]$p[18]
    })
}
if ($rows.Count -eq 0) { throw "CSV 没有有效数据行" }

"===== 体检报告 ====="
"文件      : $Path"
"表头      : $header"
"行数      : {0}（坏行 {1}）" -f $rows.Count, $bad
$span = ($rows[0].t) + " ~ " + ($rows[-1].t)
$dur = $rows[-1].ts - $rows[0].ts
"时间范围  : $span  （{0:N1} 分钟）" -f ($dur / 60.0)
"采样率    : {0:N2} 条/秒" -f ($rows.Count / [Math]::Max(1, $dur))
$dup = ($rows | Group-Object ts | Where-Object { $_.Count -gt 1 }).Count
$gaps = 0
for ($i = 1; $i -lt $rows.Count; $i++) { if (($rows[$i].ts - $rows[$i - 1].ts) -gt 3) { $gaps++ } }
"重复秒    : $dup     >3s 断档: $gaps"

# ---- 一致性 ----
$checks = [ordered]@{
    'fan=1 但 applied≠0x40'          = @($rows | Where-Object { $_.fan -eq 1 -and $_.applied -ne '0x40' }).Count
    'fan=0 但 applied≠0x80'          = @($rows | Where-Object { $_.fan -eq 0 -and $_.applied -ne '0x80' }).Count
    'status=MAX 但 base≠0x40'        = @($rows | Where-Object { $_.status -eq 1 -and $_.base -ne '0x40' }).Count
    'status=释放 但 pin_state≠2'     = @($rows | Where-Object { $_.status -eq 3 -and $_.pinState -ne 2 }).Count
    'pin_state=1 但 |PL1-目标|>0.5W' = @($rows | Where-Object { $_.pinState -eq 1 -and [Math]::Abs($_.pl1 - $_.pin) -gt 0.5 }).Count
    'pl_level 越界'                  = @($rows | Where-Object { $_.level -lt 0 -or $_.level -gt $cfg.Steps }).Count
    'pin_w 超出 [min,max]'           = @($rows | Where-Object { $_.pin -lt $cfg.MinW - 0.1 -or $_.pin -gt $cfg.MaxW + 0.1 }).Count
}
""
"----- 一致性 -----"
$allOk = $true
foreach ($k in $checks.Keys) {
    $v = $checks[$k]
    if ($v -gt 0) { $allOk = $false }
    "{0,-32} {1,6}" -f $k, $v
}
if ($allOk) { "全部通过 ✓" } else { "存在不一致，需要查看原始行 ⚠" }

# ---- 阶梯分布 ----
""
"----- 功率阶梯占用 -----"
$total = $rows.Count
foreach ($g in ($rows | Group-Object level | Sort-Object { [int]$_.Name })) {
    $lv = [int]$g.Name
    $w = $cfg.MaxW - ($cfg.MaxW - $cfg.MinW) * $lv / [Math]::Max(1, $cfg.Steps)
    $pct = $g.Count * 100.0 / $total
    $temps = ($g.Group | Measure-Object cpu -Minimum -Maximum -Average)
    "{0} 级  {1,5:0.0}W  {2,6:N1}%  温度 {3:0}-{4:0}°C(均{5:0})" -f $lv, $w, $pct, $temps.Minimum, $temps.Maximum, $temps.Average
}

# ---- 风扇段 ----
""
"----- 风扇全速 -----"
$full = @($rows | Where-Object { $_.fan -eq 1 })
"全速样本: {0}（{1:N1}%）" -f $full.Count, ($full.Count * 100.0 / $total)
$episodes = New-Object System.Collections.ArrayList
$start = $null
for ($i = 0; $i -lt $rows.Count; $i++) {
    if ($rows[$i].fan -eq 1 -and $null -eq $start) { $start = $i }
    elseif ($rows[$i].fan -eq 0 -and $null -ne $start) { [void]$episodes.Add([pscustomobject]@{ i0 = $start; i1 = $i - 1 }); $start = $null }
}
if ($null -ne $start) { [void]$episodes.Add([pscustomobject]@{ i0 = $start; i1 = $rows.Count - 1 }) }
"全速段数: {0}" -f $episodes.Count
$episodes | Sort-Object { ($rows[$_.i1].ts - $rows[$_.i0].ts) } -Descending | Select-Object -First $Top | ForEach-Object {
    $a = $rows[$_.i0]; $b = $rows[$_.i1]
    "  {0} → {1}  {2,4}s  温度 {3}→{4}°C  PL1 {5}→{6}W  进入时 busy {7:0}%" -f $a.t.Substring(11), $b.t.Substring(11), ($b.ts - $a.ts), $a.cpu, $b.cpu, $a.pl1, $b.pl1, $a.busy
}

# ---- 高温/释放 ----
""
"----- 温度与保护 -----"
$tm = $rows | Measure-Object cpu -Minimum -Maximum -Average
"CPU 温度: 最低 {0}°C  平均 {1:0.0}°C  最高 {2}°C" -f $tm.Minimum, $tm.Average, $tm.Maximum
"≥95°C 样本: {0}" -f @($rows | Where-Object { $_.cpu -ge 95 }).Count
"释放(pin_state=2) 样本: {0}" -f @($rows | Where-Object { $_.pinState -eq 2 }).Count
"状态分布: " + (($rows | Group-Object status | Sort-Object Name | ForEach-Object { "status$($_.Name)=$($_.Count)" }) -join '  ')
"供电: AC {0} 条 / 电池 {1} 条" -f @($rows | Where-Object { $_.onAc -eq 1 }).Count, @($rows | Where-Object { $_.onAc -eq 0 }).Count

# ---- 相关事件日志 ----
""
"----- 事件日志（smart.log 最近 15 条）-----"
$sl = Join-Path $dir 'smart.log'
if (Test-Path $sl) { Get-Content $sl -Tail 15 | ForEach-Object { "  $_" } } else { "  （无 smart.log）" }
