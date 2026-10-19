# DiskCleaner

[![.NET](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D4.svg)](https://www.microsoft.com/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Version](https://img.shields.io/badge/Version-0.9.3-blue.svg)](#)

一款面向 Windows 10 / 11 的系统清理与优化工具，采用 WPF + MVVM 架构，全部功能调用 **Windows 原生 API** 实现，不依赖 WMI，启动快、体积小。

> **文档说明**：源码中的全部注释已迁出至 `项目说明.md`（仅本地保留，**不推送 GitHub**），集中记录各模块的设计说明、安全护栏与行为注解。本 README 聚焦功能与架构概览。

---

## 功能特性

### 1. 仪表盘

- **系统信息**：系统版本 / CPU / 内存 / 显卡信息
- **性能监控**：CPU / 内存 / GPU 实时占用（原生 API 高速采样），支持一键整理系统内存
- **磁盘空间**：各磁盘分区容量与可用空间卡片，双击可打开分区

### 2. 深度清理

深度清理页包含 **垃圾清理**、**极限清理**、**空文件夹扫描** 三大块。

#### 垃圾清理（树形分组，100+ 清理项）

每项标注 **安全 / 谨慎** 风险等级，默认仅勾选安全级；扫描采用并行调度（并发度 4），结果按"是否有内容"自动过滤，0B 项隐藏不显示。

**系统清理**
- 系统临时文件、用户临时文件（`%temp%`）、预读取（Prefetch）、内存转储（`MEMORY.DMP`）
- Windows 错误报告（WER）、Windows 日志（`Logs\CBS` 等）、Minidump 崩溃转储
- 缩略图缓存（`thumbcache_*.db` / `iconcache_*.db`）、图标缓存（`IconCache.db`）
- Internet 临时文件（INetCache）、显卡着色器缓存（D3DSCache + NVIDIA DXCache/GLCache + AMD DxcCache/DxCache/VkCache/OglCache，删除后游戏首次加载自动重建）、Windows 小组件缓存
- 传递优化文件（`SoftwareDistribution\Download`）、Windows Store 缓存
- Windows 升级临时文件（`$WINDOWS.~BT` / `~WS` / `~Q`，谨慎级，10 天内删除将无法回滚；**清理前先检测系统是否正在升级/维护，升级中自动跳过**）
- 设备驱动程序包（仅删未被任何设备引用的孤立 OEM 驱动包；**清理前同样检测系统是否正在升级，升级中自动跳过**）
- Windows Defender 历史扫描、字体缓存（`FontCache\*.dat`）、实时内核报告（`LiveKernelReports`）

**应用清理**
- **浏览器**：Edge / Chrome / Firefox / Brave / Opera / 360 / 搜狗 / QQ浏览器 / Vivaldi / 夸克 — 全部支持**多 Profile** 扫描（`Default` / `Profile 1..N` / `Guest Profile`），仅清 `Cache` / `Code Cache`，**不触碰** Cookies / 登录凭据 / 书签
- **即时通讯**：微信（仅 `FileStorage\Cache`）/ QQ（`RichMedia` 缩略图 + QQ NT 缓存：`Cache\Cache_Data` 等 Chromium 缓存 + `nt_data` 聊天媒体缓存 Pic/Emoji/Video/avatar，**绝不触碰** `nt_db` 聊天记录库）/ 企业微信（仅 `Cache`）/ 网易邮箱大师 — **绝不清理**聊天记录、聊天图片视频、接收文件
- **音乐**：网易云音乐 / QQ音乐 / 酷狗 / 酷我 — 仅清 `Cache`，**不触碰**已下载歌曲
- **视频**：腾讯视频 / 优酷 / 爱奇艺 — 仅清 `Cache`，**不触碰**已下载视频
- **网盘/下载**：迅雷（仅 `Cache` + `XLLiveUD`，**保留** `Torrents` 种子收藏）、Spotify（仅 `cache` + `Storage`，**保留** `Data` 离线音乐）、腾讯会议（仅 `Cache` + `AppCache`，**保留** `Recording` 录制视频）
- **办公游戏**：WPS Office（仅 `cache`，**保留** `backup` 文档备份）、Epic Games Launcher、暴雪战网 Battle.net（Overlay 网页缓存 `webview_cache_*` + Electron 缓存，均为纯缓存按需重建，清理前建议先关闭战网）
- **开发工具**：VSCode / JetBrains 系列 / Visual Studio / Android Studio / npm / pip / Maven / Gradle / NuGet — 仅清缓存，**保留** config / projects / plugins / workspaceStorage
- **其他**：钉钉 / 剪映 / Telegram（扩展名白名单：`.cache` / `.blob` / `.bin`）/ Adobe（扩展名白名单：`.pek` / `.cfa` / `.ims`，**保留** `.mcdb` 索引数据库）

**应用日志清理**
独立的「应用日志清理」分组，仅清各软件约定俗成的**日志 / 崩溃报告**目录（`Logs` / `Crashpad\reports` / `xlog` / `_logs` / `Saved\Logs` 等），**绝不触碰**用户数据、缓存、配置。覆盖约 40 款常用软件：
- **浏览器**：Edge / Chrome / Firefox / Brave / Opera / 360 / 搜狗 / QQ浏览器 / Vivaldi / 夸克
- **即时通讯**：微信（xlog）/ QQ / 企业微信 / 钉钉 / 飞书 / Discord / Telegram / 网易邮箱大师
- **开发工具**：VSCode / npm
- **影音**：网易云音乐 / QQ音乐 / 酷狗 / 酷我 / 腾讯视频 / 优酷 / 爱奇艺 / 哔哩哔哩 / 快手
- **网盘 / 工具**：迅雷 / 百度网盘 / Steam / Spotify / 腾讯会议 / 剪映 / Epic
- 路径不存在时自动 0B 隐藏；所有项均为**安全级**，有内容默认勾选

**电脑使用痕迹**
- **Windows 事件跟踪日志**：ETL 实时日志缓冲
- **Windows 最近访问**：「最近使用的项目」快捷方式与跳转列表
- **Windows 剪贴板**：剪贴板历史记录
- **资源管理器使用痕迹**：`HKCU\Explorer\` 下的纯使用历史——最近文档(`RecentDocs`)、运行框历史(`RunMRU`)、地址栏键入路径(`TypedPaths`)、程序使用统计(`UserAssist`)、打开/保存对话框历史(`ComDlg32\OpenSaveMRU` 等)；清空后系统自动重建，不影响功能

**注册表清理**
- **无效启动项**：清理 Run/RunOnce、启动文件夹、任务计划程序中指向已卸载程序的残留
- **失效 COM/ActiveX 组件**：CLSID 中服务器文件（DLL/EXE）已删除的失效组件
- **孤立卸载残留**：软件已删但注册表残留的卸载信息
- **无效右键菜单**：文件/文件夹/桌面右键菜单中指向已卸载程序的失效项
- **无效文件关联**：文件属性「打开方式」中指向已卸载程序的残留项（扫描 `HKCR\Applications`，验证 exe 是否存在，系统关键程序白名单保护）
- **失效兼容性设置**：清理 `AppCompatFlags\Layers` 中为已删除 exe 保留的手动兼容性设置（如以管理员运行、XP 兼容模式等）
- **失效兼容性助手记录**：清理 `AppCompatFlags\Compatibility Assistant\Store` 中指向已删除 exe 的 Windows 兼容性助手(PCA)运行台账残留
- **失效防火墙规则**：清理 Windows 防火墙（`HKLM\...\FirewallPolicy\FirewallRules`）中指向已卸载程序（`App=` 路径不存在）的孤儿规则；写入 HKLM 需管理员权限，非管理员时自动跳过

**回收站**
- 清理完成后可选清空回收站（默认不勾选，由用户手动决定）
- **孤儿回收站检测**：扫描各盘 `$RECYCLE.BIN` 中索引已丢失/损坏的孤儿数据（`$R` 无对应 `$I` 索引），这类数据系统界面与“清空回收站”命令都看不见也删不掉，但真实占用磁盘且永远无法通过回收站恢复——删除不损失可恢复性

**安全设计**
- **直接删除**：所有文件删除直接删除（不再移入回收站）
- **重解析点跳过**：junction / symlink / mount point 不跟随、不删除目标
- **系统属性保护**：带 `System` 属性的文件/目录跳过
- **注册表严格判定**：WOW64 互查、未加引号路径解析，避免误判已安装软件
- **空文件夹清理**：只删真正为空的目录，跳过重解析点和系统目录，非递归删除
- **系统权限文件兜底**：普通删除/截断失败的系统保护文件（如仅 SYSTEM 可删的系统日志）自动尝试 `takeown` + `icacls` 夺取所有权后删除；仍被占用则走“重启后删除”

#### 极限清理（系统工具）

| 工具 | 说明 |
|------|------|
| 关闭休眠 | 删除 `hiberfil.sys` 释放数 GB；同时禁用「快速启动」，可一键恢复 |
| 系统修复 | `sfc /scannow` 扫描并修复受保护的系统文件 |
| DISM 组件清理 | **安全模式**（不带 `/resetbase`），清理 WinSxS 旧组件，不影响更新卸载 |
| 删除 Windows.old | 删除升级残留旧系统目录，释放十几到几十 GB |
| 清理 Delivery Optimization | 自动检测 Windows Update 活动，临时暂停 BITS 服务防止更新损坏，清理后自动恢复 |
| 刷新 DNS 缓存 | `ipconfig /flushdns` 清空本地 DNS 缓存 |

每个工具卡片中标题与说明之间有分割线，执行状态实时回流到界面。

#### 空文件夹扫描

- 扫描指定位置的空目录，支持批量删除（跳过重解析点与系统目录）

### 3. 右键管理

- **文件右键管理** / **桌面右键管理** / **新建菜单管理** 三个分类
- **显示系统项开关**：默认关闭（过滤系统项），勾选后显示全部项（含系统项）
- 扫描注册表 shell 动词、Shell 扩展、新建菜单，提取程序图标
- 支持禁用/启用/删除右键菜单项（删除前自动备份，可跨会话撤销恢复）
- **中文名映射**：内置 120+ 项 CLSID/动词中文映射 + 动态解析链（注册表 LocalizedString → DLL 产品名 → DLL 文件名兜底），未识别项不再显示裸 GUID

### 4. 系统设置

- **UAC 通知级别**：调节用户账户控制的通知强度（始终通知 / 默认 / 不调暗 / 从不通知）
- **SmartScreen 筛选器**：开关 Windows SmartScreen 应用防护
- **任务栏与开始菜单**：任务栏按钮合并方式、开始菜单居中
- **桌面图标**：控制「此电脑 / 回收站 / 网络 / 控制面板 / 用户文件夹」是否在桌面显示
- **文件资源管理器**：显示隐藏文件、显示文件扩展名、显示受保护的系统文件等
- **记事本设置**：面向新记事本（Win11 多标签页）——打开文件方式（新选项卡 / 新窗口）、启动方式（新会话 / 继续上一个会话）；应用设置时不再强杀运行中的记事本（避免丢失未保存内容），有实例则仅改注册表下次生效
- **用户文件夹迁移**：将桌面 / 文档 / 下载 / 图片 / 音乐 / 视频重定向到新位置（`robocopy` 两阶段迁移：先纯复制不删源、验证成功再移除源、失败自动回滚 + `SHSetKnownFolderPath` 系统级重定向 + 迁移后自验证源目录残留），释放 C 盘或重装系统后文件不丢失
- **HOSTS 文件**：内置编辑 `C:\Windows\System32\drivers\etc\hosts`，支持备份与还原

---

## 技术亮点

- **纯原生 API**：所有底层操作直接调用 `kernel32` / `shell32` / `psapi` / `ole32`，**零 WMI 依赖**（系统硬件信息采集除外），启动与采集速度快。
- **并行扫描**：垃圾清理扫描采用 `SemaphoreSlim` 限制并发度 4，避免磁盘 I/O 争抢，总耗时 ≈ 最慢项的扫描时间。
- **直接删除**：垃圾清理一律直接删除（不再移入回收站）；谨慎级项目默认不勾选并需二次确认，降低误删风险。
- **实时反馈**：系统优化命令通过 `IProgress<string>` 逐行回流到 UI 日志。
- **删除四级降级链**：直接删除 → 截断 → `takeown`+`icacls` 夺取所有权 → 重启后删除，系统权限保护文件也能清理。
- **MVVM 分层架构**：职责清晰，代码模块化。

### 项目结构

采用 **WPF + MVVM** 分层架构，全部代码置于单一工程 `DiskCleaner` 下，按职责划分命名空间（生成目录 `bin/`、`obj/`、`stage_build` 不纳入结构）：

```
DiskCleaner/
├── App.xaml / App.xaml.cs            # 程序入口，StartupUri 指向主窗口
├── DiskCleaner.csproj                # 项目文件（目标框架 net8.0-windows）
├── Directory.Build.props             # 共享编译属性（版本号、发布配置）
├── app.manifest                      # 应用清单
├── build.bat                         # 一键发布脚本（依赖框架单文件）
├── ContextMenuMapping.cs             # 右键管理中文名映射（CLSID/动词/系统项静态字典 + 统一查询接口 + 进程级缓存）
├── .gitignore
│
├── Behaviors/                        # 附加行为（DiskCleaner.Behaviors）
│   └── ScrollBubbleBehavior.cs       #   滚轮事件在嵌套滚动视图间冒泡
│
├── Converters/                       # WPF 值转换器（DiskCleaner.Converters）
│   ├── Converters.cs                 #   可见性 / 布尔取反 / 百分比等转换器
│   └── BoolToBorderThicknessConverter.cs # 布尔 → 边框厚度（分组展开高亮）
│
├── Interop/                          # P/Invoke 原生 API 封装（DiskCleaner.Interop）
│   ├── NativeMethods.cs              #   kernel32 / shell32 / psapi / ole32 声明
│   ├── ShellIconHelper.cs            #   系统图像列表取图标（JUMBO/EXTRALARGE/LARGE）
│   └── ShortcutHelper.cs            #   原生 IShellLinkW+IPersistFile 解析 .lnk 目标
│
├── Models/                           # 数据模型（DiskCleaner.Models）
│   ├── CleanGroup.cs / CleanItem.cs  #   清理分组与具体清理项
│   ├── ICleanNode.cs                 #   清理节点统一接口
│   ├── ContextMenuItem.cs            #   右键菜单项（含 Icon 图标）
│   ├── OptimizeTool.cs               #   系统优化工具项
│   ├── UserFolderInfo.cs             #   可搬迁的用户文件夹定义
│   ├── DriveCardInfo.cs              #   磁盘卡片信息
│   ├── SystemInfo.cs                 #   系统信息聚合模型
│   └── Config.cs                     #   全局配置模型
│
├── Services/                         # 业务服务（DiskCleaner.Services）
│   ├── CleanService.cs               #   垃圾文件扫描与删除（直接删除 + 重解析点跳过）
│   ├── DiskUtils.cs                  #   磁盘相关工具方法（DirSize 跳过重解析点）
│   ├── SpaceAnalyzer.cs              #   空文件夹扫描
│   ├── SystemMonitorService.cs       #   原生 API 采样 CPU / 内存 / 磁盘 / GPU
│   ├── SystemInfoService.cs          #   系统硬件信息（WMI 带超时）
│   ├── SystemOptimizeService.cs      #   系统优化命令逐行回流（DISM 安全模式 + BITS 服务管理）
│   ├── UserFolderService.cs          #   用户文件夹搬迁与还原
│   ├── WindowsTweakService.cs        #   系统设置 / 注册表优化
│   ├── ConfigService.cs              #   配置读写持久化
│   ├── ThemeService.cs               #   主题切换
│   └── ILogger.cs                    #   日志接口
│
├── ViewModels/                       # 视图模型（DiskCleaner.ViewModels）
│   ├── ViewModelBase.cs              #   INotifyPropertyChanged 基类
│   ├── RelayCommand.cs               #   ICommand 实现
│   ├── MainViewModel.cs              #   主窗口核心逻辑与导航
│   ├── MainViewModel.Dashboard.cs    #   仪表盘 / 系统监控
│   ├── MainViewModel.ContextMenu.cs  #   右键菜单管理（扫描 / 图标提取 / 系统项开关）
│   ├── MainViewModel.DeepClean.cs    #   深度清理（100+ 清理项 + 并行扫描 + 注册表清理）
│   ├── MainViewModel.SystemSettings.cs       # 系统设置页
│   └── MainViewModel.SoftwareSettings.cs     # 软件设置页
│
└── Views/                            # 窗口与界面（DiskCleaner.Views）
    ├── MainWindow.xaml(.cs)          #   主窗口（XAML + 后台代码）
    ├── SettingsWindow.xaml(.cs)      #   设置窗口
    └── FailureDetailsWindow.xaml(.cs) #   清理项 / 失败详情窗口
```

> **分层说明**：`Views` 仅负责界面呈现，通过绑定 `ViewModels` 中的属性与命令交互；`ViewModels` 编排业务逻辑并调用 `Services`；`Services` 执行具体任务（清理、扫描、优化、监控），涉及系统调用时统一走 `Interop` 的 P/Invoke 声明；`Models` 为各层共享的纯数据结构；`Converters` 处理界面层的数值/状态转换。

---

## 编译与运行

### 环境要求
- Windows 10 或 Windows 11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（目标机器运行需安装）

### 本地编译
```bash
# 方式一：使用内置脚本（依赖框架单文件发布）
build.bat

# 方式二：使用 .NET CLI
dotnet publish -c Release -r win-x64 --self-contained false
```

输出位于 `bin/Release/net8.0-windows/win-x64/publish/DiskCleaner.exe`。

---

## 使用提示与免责声明

- 清理、删除、搬迁等操作涉及系统文件与数据，**具有一定风险**。
- 标注为 **谨慎** 级别的项目（如 Windows.old、Windows 升级临时文件、Maven/Gradle 缓存）默认不勾选，执行前会要求二次确认。
- 垃圾清理默认直接删除（不再移入回收站）；「清空回收站」项需用户手动勾选。
- 用户文件夹搬迁会改变系统已知文件夹路径，请确保目标磁盘空间充足，搬迁过程中不要关闭程序。
- 本工具仅供学习与个人使用，使用风险由使用者自行承担。
