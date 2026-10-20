# assets/

本目录用于存放 **TVicPort** 的二进制文件，**内容不随仓库分发**（已由 `.gitignore` 排除）。

## 需要的文件

| 文件 | 位数 | 用途 | 放置位置 |
|---|---|---|---|
| `TVicPort.dll` | 32 位 | 用户态库（EC 端口 / MCHBAR 访问） | 程序目录（与 `TPFanLite.exe` 同级） |
| `TVicPort64.sys` | 64 位 | 内核驱动 | `assets\`（完整构建会内嵌进 exe）或程序目录（`-NoDriver` 构建） |

## 获取方式

从 EnTech Taiwan 官网下载（免费）：

- 产品页：<https://www.entechtaiwan.com/dev/port/index.shtm>
- 下载：<http://www.entechtaiwan.com/files/tvicport.zip>

解压后取出 `TVicPort.dll` 与 64 位驱动 `TVicPort64.sys`。

## 关于再分发

EnTech 官方声明允许将驱动和 DLL 随软件免费分发（原文：*"We also allow redistribution of drivers and DLL's as part of your software without having to pay royalties."*，见 <https://www.entechtaiwan.com/dev/index.shtm>）。本仓库仍选择**不分发二进制**，由使用者自行获取，避免版本与授权歧义。