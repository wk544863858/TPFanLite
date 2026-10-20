# ThinkPad T14 Gen 2i「用一会儿就卡到 0.4–0.8GHz」根因调查与根治方案

> 机型：LENOVO 20W0A007CD / ThinkPad T14 Gen 2i（i7-1165G7，Iris Xe + MX450，4K 内屏）
> BIOS 1.71（N34ET71W），EC 固件 1.46（N34HT46W），Windows 10 IoT LTSC 19044
> 记录时间：2026-10-16 ~ 2026-10-20
>
> 本文分两部分：**§2 简要记录被否决的探查路径**（别人不用再走一遍），**§3 详细记录根治方案如何被分析出来**（含可复现步骤）。

---

## 0. 摘要

**现象**：中等以上负载一段时间后 CPU 被压到 0.4–0.8GHz，系统卡顿；重启或空闲后恢复，周而复始。温度并不高（55–71°C 时就已发生），热节流计数为 0。

**根因**：EC 固件维护"热预算"，以约 **0.1 W/s** 把封装功耗上限 **PL1 从 35W 线性削减到 8W 地板**。PL1 的实际寄存器视图在 **MCHBAR+0x59A0**（RAPL `PKG_POWER_LIMIT` 的 MMIO 视图，低 15 位 = PL1，单位 1/8W）。PL1=8W 时 i7-1165G7 满载频率只剩 0.4–0.8GHz。

**根治**：每 **100ms** 把 PL1 重写回目标值，压住固件（固件 <200ms 内会改回）。配合统一智能控制：风扇提前全速、功率按温度 N 级下压，PL1 在 **35W↔20W** 之间收放，永不落到 8W。

**效果（固定 8 线程算力基准）**

| 配置 | 耗时 | 算力 |
|---|---|---|
| 钉住关闭（交给固件） | 22,372 ms | 1,072.8 Mops/s |
| 钉住开启（PL1 35W） | 12,969 ms | 1,850.6 Mops/s |

**1.73 倍**，且不需要 PawnIO / 测试签名 / 任何新软件——只用机器上本来就有的 TVicPort 驱动把 MCHBAR 映射进用户态。

---

## 1. 现象与影响

- **触发**：编译、多线程计算、转码、长期多标签浏览；轻负载也会触发（预热阶段 35W 会把温度顶到 95–100°C）。
- **表征**：任务管理器显示 CPU 占用不低，但频率只有 0.4–0.8GHz，吞吐腰斩。
- **量化**（2026-10-19 全天 26,220 个 1s 样本）：卡顿合计 358 秒 / 7.3 小时活跃时间 ≈ 1.4%，单次 3–80 秒。
- 4K 内屏直连 iGPU，CPU 与 GPU 共享封装功耗；**iGPU 抢功耗已单独排除**（WebGL 把 iGPU 打到 46–57% 时 CPU 仍 3.95GHz、PL1 不变）。

---

## 2. 被否决的探查路径（简要，实测依据）

> 目的：记录哪些路已走死、为什么死，避免重复投入。

### 2.1 系统性排除（常规假设）

| # | 假设 / 路径 | 做法 | 结论与依据 |
|---|---|---|---|
| 1 | 过热降频 | 记录封装温度 / 热节流事件 | **否**。限功率发生时温度仅 55–71°C，热节流 0/120，PLE 120/120 |
| 2 | PROCHOT/外部拉低 | 查 PROCHOT 状态、BIOS 热模式 | **否**。无 PROCHOT 事件；BIOS 热模式已是 MaximizePerformance |
| 3 | 风扇不够 | 强制全速（0x40，6423rpm） | **不治本**。仅把平均频率 +16%；全速时温度 66→55°C，PL1 仍被压到 8W |
| 4 | 电池 / 适配器 | 插电/电池对照 | **否**。一直插电；社区"插电锁 10W"现象本机不成立 |
| 5 | Windows 电源计划 / 电源覆盖层 | 切"最佳性能"（`ded574b5-…`） | **否**。已是最佳性能；误切最左档会复现另一个已知 bug（0.39GHz 锁频） |
| 6 | BIOS `CPUPowerManagement=Disable` | Lenovo WMI 写入 → 保存 → 重启生效，回读确认 `Disable` | **证伪**。设置成功且重启后仍掉频（这条别再试了） |
| 7 | Lenovo ITS / DPTF 服务 | 停/启服务、查进程 | **不治本**。DPTF 确实在跑（见 2.3），但不是限值最终执行者；停掉不掉频问题不变 |
| 8 | 支架/平放姿态（lap mode） | 支架 vs 平放 8 线程 170s 对照 | **否**。支架末 80s 32.0W vs 平放 28.4W，姿态不是限功耗因素 |
| 9 | 限频/睿频档位 | 40%–70% 全档位扫描 | **不可行**。各档位仍触发钳制 |
| 10 | ThrottleStop / XTU 类工具 | - | **放弃**。要求不引入新软件；火绒 HIPS 对驱动敏感 |

### 2.2 MSR 层（用户态可读写的寄存器，均排除）

用 PawnIO（自编译模块 + unrestricted 驱动 + 测试签名全链路验证可行）读取：

| MSR | 观察 | 结论 |
|---|---|---|
| `0x610` PKG_POWER_LIMIT | 全程恒 `0x0042820000DD8200`（PL1=PL2=64W，lock=0） | 静态值，**不是**实际生效的墙；写它无效 |
| `0x64C` | 恒 `0xB`（PL1 字段=1.375W、enable=0） | 域未实现/未启用 |
| `0x64D` | 递增但速率与负载无关（idle 37.9W/load 36.0W/rec 38.2W 折算） | 不是能量计数，无意义 |
| `0x64E` | 随机跳变 | 域未实现（垃圾值） |
| `0x64F` | 先递增后冻结 | 无解释力 |
| `0x601` VR 配置 | 恒 `0x3C8`（钳制/正常两态一样） | 排除 |
| `0x150/0x1A4/0x607/0x608/0x1B1/0x198/0x606/0x611/0x1A2` | 双态对比均无跟随 | 排除 |
| `0x613` | 钳制发生时递增、满速时归零 | 是**状态指示器**（钳制累计时间），不是控制源 |

补充实验：677 个样本、8 线程满载 10 分钟 PKG 恒 19.9W（busy=100%）也没掉到地板——说明"动态 PL1"不在 MSR 空间，由 EC 经 PECI 私有通道下发 PCU。

### 2.3 ACPI / DPTF / WMI 层

- **DPTF 确实存在且在跑**（`dptf_acpi/dptf_cpu/esif_lf/IntcOED` + Intel Dynamic Tuning Manager + Lenovo ITS `LEN0100`），推翻早期"DPTF 设备不存在"的错误结论。
- 反编译 `DptfTabl` SSDT：`\_SB.PC00.TCPU` 定义 `OperationRegion (CPWR, SystemMemory, MHBAR+0x5000, 0x1000)`，`_DSM` 暴露功率限制能力；`_OSC` 调 `HKEY.DYTC(0x800F0001)` 切 Lenovo 性能模式。
- DSDT 里的 `PL10..PL22/PLW0..2` 是 `PNVS`（`0x949FE000`）里的 **ACPI 变量**，DPTF 读它们，但真正落到硬件的是 `CPWR` 区字段（见 §3.2）。
- Windows 侧没有任何可读的 DYTC/DPTF 功率限制 WMI 类；`Lenovo_WmiOpcodeInterface` 需盲猜参数，未采用。

### 2.4 EC 直连层（TVicPort 端口读 EC）

- 端口：CTRL `0x1604`、DATA `0x1600`（读 0x80 / 写 0x81）。风扇 `0x2F`（`0x80` 自动 / `0x40` 全速 / 1–7 手动）、温度 `0x78/0x79/0x7A/0x7C`、RPM 走 `0x31`+`0x84/0x85`。
- EC RAM `0x00–0xFF` 全扫：唯一与钳制相关的是 `0xC6`（钳制态 0x01 / 正常态 0x03）。**写入 0x03 不生效**（EC 不覆盖也不改变行为）→ 它是状态输出，不是控制源。
- 结论：EC 的限值不在 RAM/端口可见范围，在固件私有层（需 SMAPI/SCU 才能触碰，Windows 不可达）。

### 2.5 早期 MCHBAR 扫描为什么失败（重要教训）

第一轮（10-18）用 PawnIO 的 MCHBAR 读模块扫 `0x5900–0x5A00`，结论"RAPL 区恒定"，**漏掉了 0x59A0**。原因有三：

1. **扫描目标是"会变化的寄存器"**：当时 PL1 已进 8W 平台期（长时间不变），"找变化"自然找不到；而它恰恰是**被固件慢慢改写**的那个值。
2. **映射尺寸不够**：4KB 映射读 `0x5930` 得到错误值 `0x0203CFC1`，说明小映射不可靠（正确值应为 `0xE0` 量级）；后来改用 ≥64KB 才与另一路读数一致。
3. **没有偏移先验**：RAPL `PKG_POWER_LIMIT` 的 MMIO 偏移在不同代际/BIOS 下不同，纯扫描容易误判。

→ 后来改用"**先从 ACPI 静态逆向拿偏移，再直读验证**"的顺序，一次命中（§3.2）。

---

## 3. 根治方案是怎么分析出来的（详细、可复现）

### 3.0 推导链条总览

```
现象：卡顿时温度不高、热节流=0，但 PKG 被压到 7–16W
   ↓ 排除常规（§2.1）
   ↓
限值在"功耗控制单元（PCU）"的 PL1 —— 但用户态 MSR 里看不到（§2.2）
   ↓ 换思路：PCU 的限制值一定来自 somewhere 的写入（EC/SMM/ACPI）
   ↓
静态逆向 BIOS/ACPI：找到 Lenovo 自己施加限制的路径（§3.2）
   ↓ 得到精确偏移：MCHBAR+0x5000 的 CPWR 区，PPL1 在 offset 0x9A0
   ↓
直读验证：0x59A0 低 15 位 = 当前 PL1，随时间线性下滑，与历史观测完全吻合（§3.3）
   ↓
写入验证：能写进去 → 固件 <200ms 覆盖 → 100ms 重写压住（§3.4）
   ↓
驱动选择：TVicPort MapPhysToLinear，无需 PawnIO（§3.5）
   ↓
落地：Pl1Pin 执行器 + SmartCtrl 调度（§4–§6）
```

### 3.1 为什么想到"去 MCHBAR 找"

- MSR 空间已排除（§2.2），但**PCU 的限制值必然存在于某处可写存储**：MSR 或 MMIO。
- Intel 的 RAPL 有两条访问路径：**MSR 接口**（`0x610`）和 **MMIO 接口**（MCHBAR 内 `PKG_POWER_LIMIT` 视图）。本机 MSR 那条是"静态配置值"，但 MMIO 那条是 **PCU 实际生效值的镜像**——这是当时的核心假设。
- 另一个线索：早期 PawnIO 的 MCHBAR 模块读数与 MSR 读数在"正常态"能对上 PL2=64W，说明 MMIO 视图是活的。

### 3.2 关键突破①：从 ACPI 静态逆向拿到精确偏移

做法（完全可复现）：

1. 取 BIOS 包 `n34uj36w.exe`（联想官网，1.71-1.46 = N34ET71W-N34HT46W），Inno Setup 打包，`innoextract` 解出：
   - `$0AN3400.FL1` 17.9MB：BIOS 全镜像（39 个 UEFI 卷）
   - `$0AN3400.FL2` 256KB：**EC 固件**（`_EC` 魔数 + IBM/Lenovo 版权串）
2. 从 `HKLM\HARDWARE\ACPI` 导出全部 19 张 ACPI 表（Reg 里 `DSDT/SSDT` 的二进制），用 `iasl` 反编译成 ASL。
3. 在 `DptfTabl` SSDT 里找到：

   ```asl
   OperationRegion (CPWR, SystemMemory, MHBAR + 0x5000, 0x1000)
   ```

4. 在 DSDT 里找到 Lenovo 自己施加/解除限制的方法：

   ```asl
   Field (CPWR, AnyAcc, Lock, Preserve)
   {
       Offset (0x9A0),
       PPL1, 15,   // PL1 值，单位 1/8 W
       PL1E,  1,   // bit15 enable
       CLP1,  1    // bit16 clamp
   }

   Method (SPL1) { PPL1 = PLVL * PPU / 1000; PL1E = 1; CLP1 = 1 }  // 施加
   Method (RPL1) { 恢复保存值 }                                      // 解除
   ```
   其中 `MHBAR = 0xFEDC0000`（DSDT 声明），`CPWR` 起始 = `0xFEDC5000`，`PPL1` 绝对地址 = `0xFEDC0000 + 0x5000 + 0x9A0 = 0xFEDC59A0` → **MCHBAR+0x59A0**。

5. 旁证：`PLVL/PLCS` 在 GNVS、`PL10..PL22/PLW0..2` 在 `PNVS(0x949FE000)`，是 DPTF 使用的 ACPI 变量；`PL2` 视图在 `0x59A4`（实测恒 64W）。

> **为什么这条路能成**：不是靠"猜偏移扫内存"，而是找到了**固件自己写入的代码路径**（`SPL1`），偏移是从代码里读出来的，因此可靠性极高。

### 3.3 关键突破②：读值验证（确认 0x59A0 就是动态 PL1）

用 PawnIO `IntelMCHBAR.bin`（当时还没用 TVicPort）以 1Hz 直读 `MCHBAR+0x59A0`，同时 8 线程负载：

```
t=0s     PL1=33.75W
t=50s    PL1=28.50W
t=100s   PL1=23.25W
t=130s   PL1=20.25W      ← 线性滑落 ≈ 0.104 W/s
```

- 与历史"0.09–0.1 W/s 滑落"的观测完全吻合 → **动态 PL1 就在这里**。
- 同时段：`0x59A4`（PL2）= 64W 恒定；`0x5930`、`0x5F40`、`0x5F48` 恒定；只有 `0x59A0` 跟随。
- 字段解码：`value & 0x7FFF` = PL1 原始值（×0.125W）；bit15 = enable；bit16 = clamp。
  读到的典型值：`0x00 DD 81 18` → PL1=`0x118`(280)×0.125=**35.0W**、enable=1。

### 3.4 关键突破③：写入验证与「覆盖竞速」

用 `MapPhysToLinear` 映射后直接写 `0x59A0 = 0x00DD8118`（PL1=35W、enable=1、保留高 16 位）：

```
立即读回：35.0W  ✓
+50ms   ：35.0W  ✓
+200ms  ：24.5W  ← 固件已改回（按自己的衰减值改写）
```

- 结论：**固件覆盖延迟 <200ms**；
- **每 100ms 重写一次 → 60/60 个采样保持 35W**（时序上偶见 11.75W/9W 的瞬间值，是两次写入之间的固件写入窗口，不影响均值）；
- 功能对照（8 线程，PowerShell 原型）：

| 阶段 | PKG | 频率 | 温度 |
|---|---|---|---|
| A 不钉 30s | 15.3 W | 1.95 GHz | 73°C |
| B 每 100ms 钉 35W，60s | 30.6 W | 3.09 GHz | 89°C |
| C 停钉 40s | 16.6 W | 1.38 GHz | 81°C |

写值构造（与实现一致）：

```
raw  = round(W * 8)                 // 1/8W 单位
new  = (old & 0xFFFF0000)           // 保留高 16 位（PL2/clamp 等）
       | 0x8000                     // PL1E = 1
       | raw                        // PL1 值
write(new, MCHBAR + 0x59A0)
```

### 3.5 关键突破④：驱动选择（为什么不用 PawnIO）

PawnIO 直读 MSR 需要：unrestricted 版驱动（自编译 `-DPAWNIO_UNRESTRICTED=ON` 或旧版安装器）+ 测试签名 + 重启换装。已验证可行但成本高、且对普通用户风险大。

后来发现 **TVicPort**（TPFanLite 本来就依赖的 32 位 EC 驱动）导出 `MapPhysToLinear`：
- `dumpbin` 反汇编确认签名：`PVOID MapPhysToLinear(DWORD PhysAddr, DWORD Size)`（`ret 8`，内部 `DeviceIoControl 0x80002008`）；
- 映射一次 MCHBAR（**≥64KB**），用户态按普通内存读写；
- **无需管理员、无需测试签名、无新增软件**；
- 于是 PawnIO 与 MSR 相关代码整体移除。

---

### 3.6 可复现步骤（最小验证路径）

> 目标：在一台 TGL-U ThinkPad 上确认"动态 PL1 在 MCHBAR+0x59A0 且可钉住"。约 10 分钟。

**准备**

1. 确认 TVicPort 驱动在运行：`Get-Service TVicPort64` → Running；未装则用 `restore\install-tvicport.cmd`（管理员）。
2. 用 **32 位** PowerShell 运行（`TVicPort.dll` 只有 x86）：
   ```
   %WINDIR%\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile
   ```
3. 拷贝 `TVicPort.dll` 到当前目录或系统目录（DLL 搜索路径）。

**步骤 A：读 MCHBAR+0x59A0**

```powershell
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class Mch {
  [DllImport("TVicPort.dll")] public static extern bool OpenTVicPort();
  [DllImport("TVicPort.dll")] public static extern IntPtr MapPhysToLinear(uint a, uint s);
  [DllImport("TVicPort.dll")] public static extern bool UnmapMemory(IntPtr p);
}
"@
[Mch]::OpenTVicPort()
$map = [Mch]::MapPhysToLinear(0xFEDC0000, 0x10000)   # 必须 64KB
$raw = [Runtime.InteropServices.Marshal]::ReadInt32($map, 0x59A0)
$pl1 = ($raw -band 0x7FFF) / 8.0
$pl2 = ([Runtime.InteropServices.Marshal]::ReadInt32($map, 0x59A4) -band 0x7FFF) / 8.0
"PL1={0:N2}W PL2={1:N2}W raw=0x{2:X8}" -f $pl1, $pl2, $raw
```

预期：正常态 `PL1=35.00W PL2=64.00W raw=0x00DD8118`；如果 `PL1` 明显偏小（8–27W）→ 正处在掉频中。

**步骤 B：验证它跟随掉频**（起 8 线程负载，1 秒读一次）

```powershell
1..300 | ForEach-Object {
  $raw = [Runtime.InteropServices.Marshal]::ReadInt32($map, 0x59A0)
  "{0}  PL1={1:N2}W" -f (Get-Date -Format 'HH:mm:ss'), (($raw -band 0x7FFF)/8.0)
  Start-Sleep -Milliseconds 1000
}
```

预期：约 0.05–0.15 W/s 线性下降；长时间后停在地板 8W 附近。

**步骤 C：验证写入与覆盖竞速**

```powershell
$rd = { ([Runtime.InteropServices.Marshal]::ReadInt32($map, 0x59A0) -band 0x7FFF) / 8.0 }
$raw = [Runtime.InteropServices.Marshal]::ReadInt32($map, 0x59A0)
$new = ($raw -band 0xFFFF0000) -bor 0x8000 -bor ([int](35*8))   # 35W + enable
[Runtime.InteropServices.Marshal]::WriteInt32($map, 0x59A0, $new)
"写后立即读回: {0:N2}W" -f (& $rd)
"50ms 后: {0:N2}W" -f (Start-Sleep -Milliseconds 50; & $rd)
"100ms 后: {0:N2}W" -f (Start-Sleep -Milliseconds 50; & $rd)
"200ms 后: {0:N2}W" -f (Start-Sleep -Milliseconds 100; & $rd)
```

预期：立即回读 35.00W；**200ms 左右回落到固件值**（说明被覆盖，这是正常现象）。

**步骤 D：钉住（100ms 重写循环）**

```powershell
$deadline = (Get-Date).AddSeconds(60)
$n = 0; $hold = 0
while ((Get-Date) -lt $deadline) {
  $raw = [Runtime.InteropServices.Marshal]::ReadInt32($map, 0x59A0)
  $new = ($raw -band 0xFFFF0000) -bor 0x8000 -bor ([int](35*8))
  [Runtime.InteropServices.Marshal]::WriteInt32($map, 0x59A0, $new)
  Start-Sleep -Milliseconds 100
  $n++
  if (((([Runtime.InteropServices.Marshal]::ReadInt32($map,0x59A0) -band 0x7FFF)/8.0)) -ge 34.9) { $hold++ }
}
"保持目标比例: {0:P0} ({1}/{2})" -f ($hold/$n), $hold, $n
```

预期：保持比例 ≥95%；8 线程负载下 PKG 从 ~15W 升到 ~30W+，频率从 1.9GHz 升到 3GHz 左右。

**判定标准**

| 检查 | 通过条件 | 失败含义 |
|---|---|---|
| 映射 | `$map -ne [IntPtr]::Zero` | TVicPort 驱动未跑 / 权限问题 |
| 读数 | PL1 在 8–45W、PL2≈64W | 偏移或映射尺寸不对（必须 64KB） |
| 跟随 | 负载中 PL1 单调下降 | 不是动态 PL1（换偏移再找） |
| 可写 | 写入后立即读回=目标 | 寄存器只读或 PCU 拒写 |
| 竞速 | ~200ms 被改回 | 说明被固件覆盖（符合预期，需要 100ms 重写） |
| 钉住 | 100ms 循环下保持目标 | 覆盖更快 → 提高重写频率或换通道 |

**注意**

- 必须 **32 位** PowerShell；64 位进程加载 x86 DLL 会失败。
- 映射尺寸 **≥64KB**；4KB 映射会返回错误数据（实测 `0x5930` 读到 `0x0203CFC1`）。
- 其他机型/BIOS 的偏移可能不同：必须按 §3.2 的方式从 ACPI 表确认，不要照抄偏移。
- 写之前先读：**保留高 16 位**，只改 PL1 字段和 enable 位，避免踩到 clamp/其他配置位。

---

## 4. 根因描述

```
         热负载 / 热事件
              │
              ▼
  EC 固件"热预算积分器"（N34HT46W）
  以 ~0.1 W/s 下调 PL1，目标地板 8W
              │（经 PECI/私有通道写入 PCU；MCHBAR+0x59A0 是可见镜像）
              ▼
  MCHBAR+0x59A0  PPL1[14:0] = 当前生效 PL1
              │
              ▼
  PCU 按 PL1 限功耗 → PKG 掉到 ~8W → CPU 0.4–0.8GHz
```

- **ACPI/DPTF 只是前端**：`SPL1/RPL1/GUAM` 是 Lenovo 的施加/解除路径，DPTF 读 `PNVS` 变量；最终决策与持续衰减在 EC 固件内。
- **为什么是"越来越卡"**：限值是慢速线性下滑（~0.1W/s），不是阶梯跳变。
- **自愈**：封装温度降到约 54–59°C 后 EC 自行放宽；S3 唤醒可清空热预算（满性能约 4 分钟后墙回归）。这解释了"重启就好一会儿"。

---

## 5. 解决方案设计（统一智能控制）

### 5.1 总原则

旧版有 4 套独立状态机（智能温控 / 预热全速 / 掉频自救 / 功率钉住），重叠且参数分散。现合并为**一个调度器 `SmartCtrl`**：

| 执行器 | 实现 | 语义 |
|---|---|---|
| 风扇 | EC 0x2F：`0x80` 自动 / `0x40` 全速 | **先行**：提前全速（成本只有噪音），压住峰值温度 |
| 功率 | MCHBAR+0x59A0，每 100ms 重写 | **后收**：风扇顶不住时按温度分档下压 PL1（成本是性能） |

本质：**用噪音换性能**。风扇爬升需 ~20 秒，必须提前于功率收缩触发。

### 5.2 风扇调度（纯温度驱动）

```
温度(EMA) ≥ TempStart(70°C)，持续 FanEngageDwellMs(3s)  → 全速
温度(EMA) ≤ TempFanRelease(65°C)，持续 2s                → 回 EC 自动
```

- **不看 CPU 占用**：早期带 `busy≥40%` 门槛，实测 574 个高温样本中 63% 的 busy<40%，导致"轻载高温"永不升速，已删除。
- 3s 进入确认避免忙闲抖动；65°C 释放回差避免闪回。

### 5.3 功率调度（温度 → PL1 目标，N 级阶梯）

`TempStart(70°C)` → `TempPowerHigh(98°C)` 等分 `PowerSteps(3)` 级：0 级 = 35W，末级 = 20W，每级降 5W。

| 温度区间（3 档时） | 目标 PL1 |
|---|---|
| ≤ 79.3°C | 35W（`PinPl1MaxW`） |
| 79.3 – 88.7°C | 30W |
| 88.7 – 98°C | 25W |
| ≥ 98°C | 20W（`PinPl1MinW`，≈2.5GHz，远离 0.4G 地板） |

- 平滑：温度 EMA（0.6/0.4）
- 回差：降档需超边界 +0.5°C、回档需低于边界 −1.5°C
- 确认：候选级持续 `PowerStepDwellMs`(5s) 才生效

### 5.4 保护与兜底

| 条件 | 动作 |
|---|---|
| ≥ 97°C 持续 10s | 暂停钉住，交回固件（安全第一） |
| ≤ 90°C 持续 15s | 重新接管钉住 |
| 钉住失效/关闭时 PL1 ≤ 18W 且 busy ≥ 25% 持续 5s | 兜底自救：全速风扇加速解封；PL1 ≥ 28W 持续 12s 或 busy < 20% 退出 |

### 5.5 安全边界

- **仅 AC 供电时钉住**（电池模式不干预）
- **启动自检**：映射后校验 PL1 原始值 ∈ [8,2048]（1–256W）、PL2 ∈ [64,2048]（8–256W）；不符（BIOS 布局变化）→ 禁用钉住并记日志，**不盲写**
- **退出即停**：关闭程序立即停止重写，固件 <200ms 自行接管，无残留状态
- **降级**：映射失败只影响钉住，风扇与 EC 功能照常

---

## 6. 实测数据

### 6.1 寄存器覆盖时序

| 重写周期 | 效果 |
|---|---|
| 单次写入 | <200ms 被固件覆盖 |
| 1 秒 | 60/60 采样保持 35W（瞬间仍会被抢改） |
| **100 毫秒** | 稳定保持；偶见 11.75W/9W 是两次写之间的固件窗口 |

### 6.2 应用级基准（固定 8 线程算力，起始 ~70–78°C）

| 配置 | 耗时 | 算力 |
|---|---|---|
| `PinPl1=False` | 22,372 ms | 1,072.8 Mops/s |
| `PinPl1=True` | 12,969 ms | 1,850.6 Mops/s |

### 6.3 调度现场（8 线程 90s）

```
负载段：温度 81→85°C 稳定，PL1 恒 35.0W，风扇 6423rpm，status=2（智能全速）
撤载后：风扇 OFF（busy 13%）→ 35s 内 6423→4070rpm 回 EC 自动；PL1 全程 35.0W（pin_state=1）
```

90 秒满载未出现任何 PL1 衰减；旧版同场景 2–4 分钟内滑到 8W。

---

## 7. 工程实现

### 7.1 代码结构（`src/`）

| 文件 | 职责 |
|---|---|
| `SmartCtrl.cs` | 统一调度器（风扇温度锚点 + 功率阶梯 + 释放/接管 + 兜底自救） |
| `Pl1Pin.cs` | 执行器：TVicPort 映射 MCHBAR、100ms 读-写 `0x59A0`、启动自检、日志 |
| `CpuBusy.cs` | `GetSystemTimes` 差分 busy |
| `Cfg.cs` | 全部参数与旧 ini 键迁移 |
| `MainForm.cs` | 界面（两档风扇 + 智能控制开关 + 参数行）、托盘、diag 日志 |
| `Ec.cs` | EC 端口层 |
| `Bios.cs` | 仅保留托盘"睡眠模式"（`SleepState`）写入 |
| `Program.cs` | 入口与 `--metertest` 等调试参数 |

### 7.2 关键常量（`Pl1Pin.cs`）

| 常量 | 值 | 说明 |
|---|---|---|
| MCHBAR base | `0xFEDC0000` | TGL-U 固定；DSDT 声明 + 实测 |
| PL1 寄存器 | `+0x59A0` | `PPL1[14:0]`（1/8W）、`PL1E`=bit15、`CLP1`=bit16 |
| PL2 参考 | `+0x59A4` | 自检用（≈64W） |
| 映射尺寸 | `0x10000` | **必须 ≥64KB** |
| 重写周期 | 100ms | 压住 <200ms 的固件覆盖 |
| 写值 | `(old & 0xFFFF0000) \| 0x8000 \| raw` | 保留高位，置 enable |

### 7.3 ini 参数（`%LOCALAPPDATA%\TPFanLite\TPFanLite.ini`）

```ini
Mode=auto                  ; auto（智能控制）| full（MAX 全速）

SmartEnabled=True
TempStart=70               ; 调度起点：风扇全速 + 功率阶梯共同起点
TempFanRelease=65          ; 风扇全速释放回差
FanEngageDwellMs=3000
TempPowerHigh=98           ; 功率高点（=释放点）
PowerSteps=3               ; 阶梯档位（1..8）
PowerStepDwellMs=5000
PowerStepDownC=0.5
PowerStepUpC=1.5

PinPl1=True                ; 钉住总开关（仅 AC 生效）
PinPl1MaxW=35.0            ; 上限（ini only）
PinPl1MinW=20.0            ; 下限（ini only）
PinTempRelease=97          ; 达到并持续 → 暂停钉住
PinTempReengage=90         ; 降到并持续 → 重新接管
PinReleaseDwellMs=10000
PinReengageDwellMs=15000

RescueEnabled=True
RescuePl1W=18.0
RescuePl1DwellMs=5000
RescueBusyMinPct=25
RescueExitPl1W=28.0
RescueExitDwellMs=12000
RescueExitBusyPct=20

DiagLog=True
```

### 7.4 日志（`%LOCALAPPDATA%\TPFanLite\`）

| 文件 | 内容 |
|---|---|
| `diag-v4-YYYYMMDD.csv` | 1Hz 快照，20 列：`time,ts,cpu,aps,crd,bat,rpm,fanctrl,pl1_w,pin_w,pin_state,pl_level,temp_ema,busy_pct,fan,base,applied,status,on_ac,battery_pct` |
| `smart.log` | 配置快照 + 风扇 ON/OFF、阶梯切换、释放/接管、自救事件 |
| `pl1pin.log` | MCHBAR 映射、自检、异常（`在线 MCHBAR=…` / `FAIL …`） |
| `bios-op.log` | 睡眠模式写入结果 |

`pin_state`：0=未启用/不可用、1=钉住、2=高温释放。`status`：0 正常、1 MAX、2 智能全速、3 高温释放、4 兜底自救。

---

## 8. 未解问题与边界

1. **EC 固件为何持续衰减**：FL2（N34HT46W）已提取，未做 8051/专有核逆向，热预算算法未知。
2. **长期高温**：治理器把长期负载压在 84–85°C / 35W；想更凉可下调 `PinPl1MaxW` 或前移 `TempStart`。
3. **电池模式**：当前完全不干预；如需可另设目标。
4. **BIOS 升级兼容**：布局变化时自检会禁用钉住（不盲写），需按 §3.2 重新核对偏移。
5. **钉住是治标**：它对抗固件衰减，并未改变 EC 的热预算逻辑；断电/退出后限制立即回归。
6. **0.39GHz 幽灵锁频**：另一现象（Windows 电源覆盖层切最左档触发），与本问题无关。

---

## 附录 A：证据文件

| 内容 | 路径 |
|---|---|
| BIOS 包解出物、ACPI 表与反编译 ASL | `data/bios-1.71/acpi/` |
| PL1 寄存器 1Hz 采样 | `archive/evidence/pl1-load.csv` |
| 写入验证 | `archive/evidence/pl1-write-test.txt` |
| 8 线程钉住对照 | `archive/evidence/pl1-pin-load.txt` |
| 钉住功能基准 | `archive/evidence/bench-pin-on.txt`、`archive/evidence/bench-pin-off.txt` |
| MCHBAR/RAPL 校验 | `archive/evidence/rapl-mmio-validate.csv` |
| 完整开发/实验记录 | `设计日志.md` |
| 参考实现 | `src/Pl1Pin.cs`、`src/SmartCtrl.cs` |

## 附录 B：术语

- **PL1 / PL2**：封装长时间 / 短时功耗上限
- **PKG**：封装功耗（CPU + iGPU + uncore）
- **MCHBAR**：内存控制器寄存器基址（本机 `0xFEDC0000`）
- **RAPL / PCU**：运行平均功率限制接口 / 功耗控制单元
- **DPTF / DTT**：Intel 动态平台与散热框架 / 动态调优技术
- **DYTC**：Lenovo 在 EC 上实现的动态性能模式接口
- **PECI**：Intel 平台环境控制接口（EC ↔ PCU 的私有通道）
