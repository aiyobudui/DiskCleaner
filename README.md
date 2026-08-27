# DiskCleaner

[![.NET](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D4.svg)](https://www.microsoft.com/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

一款面向 Windows 10 / 11 的系统清理与优化工具，采用 WPF + MVVM 架构，全部功能调用 **Windows 原生 API** 实现，不依赖 WMI，启动快、体积小。

---

## 功能特性

### 1. 垃圾清理（树形分组，50+ 清理项）

每项标注 **安全 / 谨慎** 风险等级，默认仅勾选安全级；扫描采用并行调度（并发度 4），结果按"是否有内容"自动过滤，0B 项隐藏不显示。

#### 系统清理
- 系统临时文件、用户临时文件（`%temp%`）、预读取（Prefetch）、内存转储（`MEMORY.DMP`）
- Windows 错误报告（WER）、Windows 日志（`Logs\CBS` 等）、Minidump 崩溃转储
- 缩略图缓存（`thumbcache_*.db` / `iconcache_*.db`）、图标缓存（`IconCache.db`）
- Internet 临时文件（INetCache）、DirectX 着色器缓存、Windows 小组件缓存
- 传递优化文件（`SoftwareDistribution\Download`）、Windows Store 缓存
- Windows 升级临时文件（`$WINDOWS.~BT` / `~WS` / `~Q`，谨慎级，10 天内删除将无法回滚）
- Windows Defender 历史扫描、字体缓存（`FontCache\*.dat`）、实时内核报告（`LiveKernelReports`）

#### 应用清理
- **浏览器**：Edge / Chrome / Firefox / Brave / Opera / 360 / 搜狗 / QQ浏览器 / Vivaldi / 夸克 — 全部支持**多 Profile** 扫描（`Default` / `Profile 1..N` / `Guest Profile`），仅清 `Cache` / `Code Cache`，**不触碰** Cookies / 登录凭据 / 书签
- **即时通讯**：微信（仅 `FileStorage\Cache`）/ QQ（仅 `RichMedia`）/ 企业微信（仅 `Cache`）/ 网易邮箱大师 — **绝不清理**聊天记录、聊天图片视频、接收文件
- **音乐**：网易云音乐 / QQ音乐 / 酷狗 / 酷我 — 仅清 `Cache`，**不触碰**已下载歌曲
- **视频**：腾讯视频 / 优酷 / 爱奇艺 — 仅清 `Cache`，**不触碰**已下载视频
- **网盘/下载**：迅雷（仅 `Cache` + `XLLiveUD`，**保留** `Torrents` 种子收藏）、Spotify（仅 `cache` + `Storage`，**保留** `Data` 离线音乐）、腾讯会议（仅 `Cache` + `AppCache`，**保留** `Recording` 录制视频）
- **办公游戏**：WPS Office（仅 `cache`，**保留** `backup` 文档备份）、Epic Games Launcher
- **开发工具**：VSCode / JetBrains 系列 / Visual Studio / Android Studio / npm / pip / Maven / Gradle / NuGet — 仅清缓存，**保留** config / projects / plugins / workspaceStorage
- **其他**：钉钉 / 剪映 / Telegram（扩展名白名单：`.cache` / `.blob` / `.bin`）/ Adobe（扩展名白名单：`.pek` / `.cfa` / `.ims`，**保留** `.mcdb` 索引数据库）

#### 注册表清理
- **无效启动项**：清理 Run/RunOnce、启动文件夹、任务计划程序中指向已卸载程序的残留
- **失效 COM/ActiveX 组件**：CLSID 中服务器文件（DLL/EXE）已删除的失效组件
- **孤立卸载残留**：软件已删但注册表残留的卸载信息
- **无效右键菜单**：文件/文件夹/桌面右键菜单中指向已卸载程序的失效项
- **无效文件关联**：文件属性「打开方式」中指向已卸载程序的残留项（扫描 `HKCR\Applications`，验证 exe 是否存在，系统关键程序白名单保护）

#### 回收站
- 清理完成后可选清空回收站（默认不勾选，由用户手动决定）

#### 安全设计
- **回收站优先**：所有文件删除先入回收站（可恢复），`UseRecycleBin = true`
- **重解析点跳过**：junction / symlink / mount point 不跟随、不删除目标
- **系统属性保护**：带 `System` 属性的文件/目录跳过
- **注册表严格判定**：WOW64 互查、未加引号路径解析，避免误判已安装软件
- **空文件夹清理**：只删真正为空的目录，跳过重解析点和系统目录，非递归删除

### 2. 极限清理（系统工具）

| 工具 | 说明 |
|------|------|
| 关闭休眠 | 删除 `hiberfil.sys` 释放数 GB；同时禁用「快速启动」，可一键恢复 |
| 系统修复 | `sfc /scannow` 扫描并修复受保护的系统文件 |
| DISM 组件清理 | **安全模式**（不带 `/resetbase`），清理 WinSxS 旧组件，不影响更新卸载 |
| 删除 Windows.old | 删除升级残留旧系统目录，释放十几到几十 GB |
| 清理 Delivery Optimization | 自动检测 Windows Update 活动，临时暂停 BITS 服务防止更新损坏，清理后自动恢复 |
| 刷新 DNS 缓存 | `ipconfig /flushdns` 清空本地 DNS 缓存 |

每个工具卡片中标题与说明之间有分割线，执行状态实时回流到界面。

### 3. 右键管理

- **文件右键管理** / **桌面右键管理** / **新建菜单管理** 三个分类
- **显示系统项开关**：默认关闭（过滤系统项），勾选后显示全部项（含系统项）
- 扫描注册表 shell 动词、Shell 扩展、新建菜单，提取程序图标
- 支持禁用/启用/删除右键菜单项

### 4. 空间分析
- **目录统计**：分析 C 盘各目录占用
- **大文件扫描**：找出大于指定大小（默认 100 MB）的文件
- **旧文件扫描**：找出超过指定天数（默认 30 天）未访问的文件
- **空文件夹**：扫描并可批量删除空目录（跳过重解析点）

### 5. 系统监控
侧边栏圆形仪表盘实时显示 **CPU / 内存 / 磁盘** 占用率，基于原生 API 高速采样：
- CPU：`GetSystemTimes`（200 ms 双采样，替代 1 秒轮询）
- 内存：`GlobalMemoryStatusEx`（瞬时，替代 WMI）
- 磁盘：`GetDiskFreeSpaceEx`

### 6. 系统设置
- 系统优化开关（关闭休眠、禁用 Defender 等）
- 用户文件夹搬迁：将桌面 / 文档 / 下载 / 图片 / 音乐 / 视频重定向到新位置（`robocopy /E /MOVE` + `SHSetKnownFolderPath`）

---

## 技术亮点

- **纯原生 API**：所有底层操作直接调用 `kernel32` / `shell32` / `psapi` / `ole32`，**零 WMI 依赖**（系统硬件信息采集除外），启动与采集速度快。
- **并行扫描**：垃圾清理扫描采用 `SemaphoreSlim` 限制并发度 4，避免磁盘 I/O 争抢，总耗时 ≈ 最慢项的扫描时间。
- **安全删除**：开启"回收站模式"时，文件通过 `SHFileOperation` 送入回收站而非直接删除；配合谨慎级确认，降低误删风险。
- **实时反馈**：系统优化命令通过 `IProgress<string>` 逐行回流到 UI 日志。
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
├── ContextMenuMapping.cs             # 右键管理中文名映射（CLSID/动词/系统项静态字典 + 统一查询接口）
├── .gitignore
│
├── Converters/                       # WPF 值转换器（DiskCleaner.Converters）
│   └── Converters.cs                 #   可见性 / 布尔取反 / 百分比等转换器
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
│   ├── CleanService.cs               #   垃圾文件扫描与删除（回收站优先 + 重解析点跳过）
│   ├── DiskUtils.cs                  #   磁盘相关工具方法（DirSize 跳过重解析点）
│   ├── SpaceAnalyzer.cs              #   目录统计 / 大文件 / 旧文件 / 空文件夹
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
│   ├── MainViewModel.DeepClean.cs    #   深度清理（50+ 清理项 + 并行扫描 + 注册表清理）
│   ├── MainViewModel.SystemSettings.cs       # 系统设置页
│   └── MainViewModel.SoftwareSettings.cs     # 软件设置页
│
└── Views/                            # 窗口与界面（DiskCleaner.Views）
    ├── MainWindow.xaml(.cs)          #   主窗口（XAML + 后台代码）
    └── SettingsWindow.xaml(.cs)      #   设置窗口
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
- 所有文件删除默认先移入回收站，误删可随时恢复；「清空回收站」项需用户手动勾选。
- 用户文件夹搬迁会改变系统已知文件夹路径，请确保目标磁盘空间充足，搬迁过程中不要关闭程序。
- 本工具仅供学习与个人使用，使用风险由使用者自行承担。
