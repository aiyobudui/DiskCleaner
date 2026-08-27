using DiskCleaner.Interop;
using DiskCleaner.Models;
using DiskCleaner.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace DiskCleaner.ViewModels
{
    /// <summary>
    /// 深度清理标签页：垃圾清理（树形分组）、极限清理（系统工具）、空文件夹扫描/删除。
    /// 直接调用共享核心中的服务（_cleanService 通过 CleanService 静态方法）、工具与状态方法。
    /// </summary>
    public partial class MainViewModel : ViewModelBase
    {
        // ---------- 集合 ----------
        private ObservableCollection<CleanGroup> _cleanGroups = new();
        private ObservableCollection<OptimizeTool> _optimizeTools = new();
        private ObservableCollection<string> _optimizeLogList = new();
        private ObservableCollection<string> _emptyFolderList = new();

        // ---------- 展开状态（默认收起，点击标题展开） ----------
        private bool _isGarbageCleanupExpanded;
        private bool _isSystemToolsExpanded;
        private bool _isEmptyFolderExpanded;

        // ---------- 清理进度 ----------
        private bool _isCleaning;
        private string _cleanProgressText = "";
        private int _cleanFileCount;
        private string _recommendedTotalText = "—";

        // ---------- 空文件夹 ----------
        private string _emptyFolderReportText = "";
        private string _emptyFolderStatusText = "";

        // ---------- 属性 ----------
        public ObservableCollection<CleanGroup> CleanGroups
        {
            get => _cleanGroups;
            set => SetProperty(ref _cleanGroups, value);
        }

        public ObservableCollection<OptimizeTool> OptimizeTools
        {
            get => _optimizeTools;
            set => SetProperty(ref _optimizeTools, value);
        }

        public ObservableCollection<string> OptimizeLogList
        {
            get => _optimizeLogList;
            private set => SetProperty(ref _optimizeLogList, value);
        }

        public ObservableCollection<string> EmptyFolderList
        {
            get => _emptyFolderList;
            private set => SetProperty(ref _emptyFolderList, value);
        }

        public bool IsGarbageCleanupExpanded { get => _isGarbageCleanupExpanded; set => SetProperty(ref _isGarbageCleanupExpanded, value); }
        public bool IsSystemToolsExpanded { get => _isSystemToolsExpanded; set => SetProperty(ref _isSystemToolsExpanded, value); }
        public bool IsEmptyFolderExpanded { get => _isEmptyFolderExpanded; set => SetProperty(ref _isEmptyFolderExpanded, value); }

        public bool IsCleaning { get => _isCleaning; private set => SetProperty(ref _isCleaning, value); }

        /// <summary>是否已执行过扫描。扫描前为 false（列表区域整体隐藏），扫描完成后为 true 才显示结果。</summary>
        private bool _hasScanned;
        public bool HasScanned { get => _hasScanned; private set => SetProperty(ref _hasScanned, value); }

        /// <summary>是否存在已勾选且非禁用的清理项（用于清理按钮可用性，无需先扫描）。</summary>
        public bool HasCleanableSelection =>
            AllCleanItems(CleanGroups).Any(c => c.IsChecked == true && c.Risk != RiskLevel.Forbidden);

        public string CleanProgressText { get => _cleanProgressText; set => SetProperty(ref _cleanProgressText, value); }
        public int CleanFileCount { get => _cleanFileCount; set => SetProperty(ref _cleanFileCount, value); }
        public string RecommendedTotalText { get => _recommendedTotalText; set => SetProperty(ref _recommendedTotalText, value); }

        // ---------- 扫描进度（确定性进度条 + 当前扫描项 + 百分比） ----------
        private bool _isScanning;
        private int _scanProgressValue;
        private string _scanCurrentItem = "";
        /// <summary>是否正在扫描（驱动扫描进度条可见性，区别于清理 IsCleaning）。</summary>
        public bool IsScanning { get => _isScanning; private set => SetProperty(ref _isScanning, value); }
        /// <summary>扫描进度百分比 0–100，用于确定性进度条。</summary>
        public int ScanProgressValue { get => _scanProgressValue; private set => SetProperty(ref _scanProgressValue, value); }
        /// <summary>当前正在扫描的清理项名称，如「微信缓存」，便于用户实时了解进度。</summary>
        public string ScanCurrentItem { get => _scanCurrentItem; private set => SetProperty(ref _scanCurrentItem, value); }

        public string EmptyFolderReportText { get => _emptyFolderReportText; set => SetProperty(ref _emptyFolderReportText, value); }
        public string EmptyFolderStatusText { get => _emptyFolderStatusText; set => SetProperty(ref _emptyFolderStatusText, value); }

        // ---------- 命令 ----------
        public ICommand ScanCommand { get; private set; }
        public ICommand CleanCommand { get; private set; }
        public ICommand RefreshCommand { get; private set; }
        public ICommand EmptyFolderScanCommand { get; private set; }
        public ICommand EmptyFolderDeleteCommand { get; private set; }

        // 每个极限清理工具自带 Command（在 AddOptimizeTool 中赋值），无需在此暴露独立命令。

        // ---------- 初始化（保证树形清理项只构建一次） ----------
        private bool _cleanItemsInitialized;
        internal void EnsureCleanItemsInitialized()
        {
            if (_cleanItemsInitialized) return;
            _cleanItemsInitialized = true;
            InitCleanItems();
            AddOptimizeTools();
            // 标签内容刚生成，强制 WPF 重新评估所有以「非忙碌」为条件的命令（如清理按钮），
            // 避免按钮停留在加载期的禁用态、必须等一次交互（如扫描）才刷新的问题。
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            // 订阅每个清理项的勾选变化，实时刷新「清理」按钮可用态（HasCleanableSelection 派生属性）。
            foreach (var item in AllCleanItems(CleanGroups))
            {
                item.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(CleanItem.IsChecked))
                        OnPropertyChanged(nameof(HasCleanableSelection));
                };
            }
        }

        /// <summary>构建垃圾清理树（顶层直接为：系统清理 / 应用清理 / 电脑使用痕迹）。</summary>
        private void InitCleanItems()
        {
            var systemGroup = new CleanGroup("系统清理") { IsExpanded = true };
            systemGroup.AddRange(new List<ICleanNode>
            {
                MakeCleanItem("temp_files", "临时文件", "清理 Windows 与用户临时文件目录", RiskLevel.Safe, CleanTempFiles, () => true),
                MakeCleanItem("minidumps", "系统错误小型转储文件", "删除 C:\\Windows\\Minidump 中的小型转储文件", RiskLevel.Safe, CleanMinidumps, () => true),
                MakeCleanItem("windows_logs", "Windows 日志文件", "清理 C:\\Windows\\Logs 目录", RiskLevel.Safe,
                    () => CleanService.DeleteDirContents(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs"), _logger),
                    () => Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs"))),
                MakeCleanItem("driver_packages", "设备驱动程序包", "仅清理未被任何设备使用的旧驱动包（如历史显卡/声卡残留），绝不删除正在使用的驱动", RiskLevel.Caution, CleanOldDriverPackages, () => true),
                MakeCleanItem("thumbnail_cache", "缩略图缓存", "清理 Windows 缩略图缓存文件", RiskLevel.Safe, CleanThumbnailCache, () => true),
                MakeCleanItem("inet_cache", "Internet 临时文件", "清理 Internet Explorer/系统 INetCache 缓存", RiskLevel.Safe, CleanInternetCache, () => true),
                MakeCleanItem("wer", "Windows 错误报告和反馈", "清理 Windows 错误报告与反馈文件", RiskLevel.Safe,
                    () => CleanService.DeleteDirContents(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER"), _logger),
                    () => Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER"))),
                MakeCleanItem("delivery_optimization", "传递优化文件", "清理 Windows 传递优化与更新下载缓存", RiskLevel.Safe, CleanDeliveryOptimization, () => true),
                MakeCleanItem("windows_widgets", "Windows 小组件", "清理 Windows 小组件（WebExperience）缓存", RiskLevel.Safe, CleanWindowsWidgets, () => true),
                MakeCleanItem("directx_shader_cache", "DirectX 着色器缓存", "清理 DirectX 着色器缓存（D3DSCache）", RiskLevel.Safe, CleanDirectXShaderCache, () => true),
                MakeCleanItem("cert_cache", "Windows 网络证书缓存", "清理用于证书验证的网络缓存（CryptnetUrlCache，如 CRL/OCSP 响应）", RiskLevel.Safe, CleanCertCache, () => true),
                MakeCleanItem("prefetch", "预读取文件", "清理 C:\\Windows\\Prefetch 中的预读取缓存（不影响已安装程序运行）", RiskLevel.Safe, CleanPrefetch, () => true),
                MakeCleanItem("memory_dump", "内存转储文件", "删除 C:\\Windows\\MEMORY.DMP（系统蓝屏后生成的大型转储，可释放数 GB）", RiskLevel.Safe, CleanMemoryDump, () => true),
                MakeCleanItem("store_cache", "应用商店缓存", "清理 Microsoft Store 的本地缓存（LocalCache），不改卸载已装应用", RiskLevel.Safe, CleanStoreCache, () => true),
                MakeCleanItem("windows_upgrade_temp", "Windows 升级临时文件", "清理 Windows 大版本升级遗留的临时文件（C:\\$WINDOWS.~BT、$WINDOWS.~WS、$WINDOWS.~Q）。注意：升级后 10 天内删除将无法回滚到旧版本系统！", RiskLevel.Caution, CleanWindowsUpgradeTemp, () => true),
                MakeCleanItem("defender_history", "Windows Defender 历史扫描", "清理 Windows Defender 扫描历史与隔离区元数据（不删除已隔离的恶意文件本体）", RiskLevel.Safe, CleanDefenderHistory, () => true),
                MakeCleanItem("font_cache", "Windows 字体缓存", "清理系统字体缓存服务（FontCache）的临时数据，删除后系统会自动重建", RiskLevel.Safe, CleanFontCache, () => true),
                MakeCleanItem("live_kernel_reports", "实时内核报告", "清理 C:\\Windows\\LiveKernelReports 中的实时内核崩溃报告", RiskLevel.Safe, CleanLiveKernelReports, () => true),
                MakeCleanItem("icon_cache", "Windows 图标缓存", "清理 %LocalAppData%\\IconCache.db 图标缓存数据库，删除后系统会自动重建图标缓存", RiskLevel.Safe, CleanIconCache, () => true)
            });

            var appGroup = new CleanGroup("应用清理") { IsExpanded = true };
            appGroup.AddRange(new List<ICleanNode>
            {
                MakeCleanItem("edge_cache", "Edge 缓存", "清理 Microsoft Edge 浏览器缓存", RiskLevel.Safe, CleanEdgeCache, () => true),
                MakeCleanItem("chrome_cache", "Chrome 缓存", "清理 Google Chrome 浏览器缓存与代码缓存", RiskLevel.Safe, CleanChromeCache, () => true),
                MakeCleanItem("firefox_cache", "Firefox 缓存", "清理 Mozilla Firefox 浏览器缓存", RiskLevel.Safe, CleanFirefoxCache, () => true),
                MakeCleanItem("brave_cache", "Brave 缓存", "清理 Brave 浏览器缓存与代码缓存", RiskLevel.Safe, CleanBraveCache, () => true),
                MakeCleanItem("opera_cache", "Opera 缓存", "清理 Opera 浏览器缓存与代码缓存", RiskLevel.Safe, CleanOperaCache, () => true),
                MakeCleanItem("quark_cache", "Quark浏览器缓存", "清理 Quark 浏览器缓存", RiskLevel.Safe, CleanQuarkCache, () => true),
                MakeCleanItem("dingtalk_cache", "钉钉缓存", "清理钉钉缓存目录", RiskLevel.Safe, CleanDingTalkCache, () => true),
                MakeCleanItem("netease_cache", "网易云音乐缓存", "清理网易云音乐缓存与 web 数据", RiskLevel.Safe, CleanNetEaseMusicCache, () => true),
                MakeCleanItem("steam_cache", "Steam 缓存", "清理 Steam 下载缓存与 HTTP 缓存", RiskLevel.Safe, CleanSteamCache, () => true),
                MakeCleanItem("baidu_pan_cache", "百度网盘缓存", "清理百度网盘客户端缓存目录", RiskLevel.Safe, CleanBaiduPanCache, () => true),
                MakeCleanItem("thunder_cache", "迅雷缓存", "清理迅雷客户端缓存与种子缓存", RiskLevel.Safe, CleanThunderCache, () => true),
                MakeCleanItem("bilibili_cache", "哔哩哔哩缓存", "清理哔哩哔哩客户端缓存", RiskLevel.Safe, CleanBilibiliCache, () => true),
                MakeCleanItem("iqiyi_cache", "爱奇艺缓存", "清理爱奇艺客户端缓存", RiskLevel.Safe, CleanIqiyiCache, () => true),
                MakeCleanItem("discord_cache", "Discord 缓存", "清理 Discord 客户端缓存与代码缓存", RiskLevel.Safe, CleanDiscordCache, () => true),
                MakeCleanItem("browser360_cache", "360 浏览器缓存", "清理 360 极速浏览器缓存与代码缓存", RiskLevel.Safe, CleanBrowser360Cache, () => true),
                MakeCleanItem("sogou_cache", "搜狗浏览器缓存", "清理搜狗浏览器缓存", RiskLevel.Safe, CleanSogouCache, () => true),
                MakeCleanItem("feishu_cache", "飞书缓存", "清理飞书客户端缓存与用户数据缓存", RiskLevel.Safe, CleanFeishuCache, () => true),
                MakeCleanItem("wemeet_cache", "腾讯会议缓存", "清理腾讯会议客户端缓存与录制临时文件", RiskLevel.Safe, CleanWeMeetCache, () => true),
                MakeCleanItem("jianying_cache", "剪映缓存", "清理剪映专业版缓存与草稿渲染临时文件", RiskLevel.Safe, CleanJianyingCache, () => true),
                MakeCleanItem("telegram_cache", "Telegram 缓存", "清理 Telegram 桌面端缓存数据（保留登录信息）", RiskLevel.Safe, CleanTelegramCache, () => true),
                MakeCleanItem("spotify_cache", "Spotify 缓存", "清理 Spotify 客户端离线缓存与存储", RiskLevel.Safe, CleanSpotifyCache, () => true),
                MakeCleanItem("adobe_cache", "Adobe 媒体缓存", "清理 Adobe 系列（PR/AE/PS）的媒体缓存文件", RiskLevel.Safe, CleanAdobeCache, () => true),
                MakeCleanItem("kuaishou_cache", "快手缓存", "清理快手 PC 客户端缓存", RiskLevel.Safe, CleanKuaishouCache, () => true),

                // ===== 即时通讯（国内主流，原列表遗漏） =====
                MakeCleanItem("wechat_cache", "微信缓存", "仅清理微信 FileStorage\\Cache 临时缓存，绝不触碰聊天记录、聊天图片视频与接收的文件", RiskLevel.Safe, CleanWeChatCache, () => true),
                MakeCleanItem("qq_cache", "QQ 缓存", "仅清理 QQ RichMedia 富媒体缩略图缓存，绝不触碰聊天图片视频与接收的文件", RiskLevel.Safe, CleanQQCache, () => true),
                MakeCleanItem("wxwork_cache", "企业微信缓存", "仅清理企业微信 Cache 临时缓存，保留聊天记录、图片视频与接收的文件", RiskLevel.Safe, CleanWXWorkCache, () => true),
                MakeCleanItem("mailmaster_cache", "网易邮箱大师缓存", "清理网易邮箱大师客户端缓存", RiskLevel.Safe, CleanMailMasterCache, () => true),

                // ===== 浏览器（原列表遗漏） =====
                MakeCleanItem("qq_browser_cache", "QQ浏览器缓存", "清理 QQ 浏览器缓存与代码缓存", RiskLevel.Safe, CleanQQBrowserCache, () => true),
                MakeCleanItem("vivaldi_cache", "Vivaldi 缓存", "清理 Vivaldi 浏览器缓存与代码缓存", RiskLevel.Safe, CleanVivaldiCache, () => true),

                // ===== 音视频（原列表遗漏） =====
                MakeCleanItem("qq_music_cache", "QQ音乐缓存", "清理 QQ 音乐客户端缓存", RiskLevel.Safe, CleanQQMusicCache, () => true),
                MakeCleanItem("kugou_cache", "酷狗音乐缓存", "清理酷狗音乐客户端缓存", RiskLevel.Safe, CleanKugouCache, () => true),
                MakeCleanItem("kuwo_cache", "酷我音乐缓存", "清理酷我音乐客户端缓存", RiskLevel.Safe, CleanKuwoCache, () => true),
                MakeCleanItem("qqlive_cache", "腾讯视频缓存", "清理腾讯视频客户端缓存", RiskLevel.Safe, CleanQQLiveCache, () => true),
                MakeCleanItem("youku_cache", "优酷缓存", "清理优酷客户端缓存", RiskLevel.Safe, CleanYoukuCache, () => true),

                // ===== 办公与游戏（原列表遗漏） =====
                MakeCleanItem("wps_cache", "WPS 缓存", "清理 WPS Office 备份与缓存文件", RiskLevel.Safe, CleanWPSCache, () => true),
                MakeCleanItem("epic_cache", "Epic Games Launcher 缓存", "清理 Epic Games 启动器缓存（保留已下载游戏库）", RiskLevel.Safe, CleanEpicCache, () => true),

                // ===== 开发工具缓存（原列表遗漏） =====
                MakeCleanItem("vscode_cache", "VSCode 缓存", "清理 Visual Studio Code 缓存、CachedData 与 Code Cache", RiskLevel.Safe, CleanVSCodeCache, () => true),
                MakeCleanItem("jetbrains_cache", "JetBrains 系列缓存", "清理 IDEA/PyCharm/WebStorm/Android Studio 等的 caches、log、tmp 目录（保留配置与项目）", RiskLevel.Safe, CleanJetBrainsCache, () => true),
                MakeCleanItem("vs_component_cache", "Visual Studio 组件缓存", "清理 Visual Studio ComponentModelCache 与 IDE 缓存（不影响已建项目）", RiskLevel.Safe, CleanVSComponentCache, () => true),
                MakeCleanItem("npm_cache", "npm 缓存", "清理 npm 包管理器缓存（C:\\Users\\{user}\\AppData\\Local\\npm-cache），下次 install 会重新下载", RiskLevel.Safe, CleanNpmCache, () => true),
                MakeCleanItem("pip_cache", "pip 缓存", "清理 Python pip 包管理器缓存（C:\\Users\\{user}\\AppData\\Local\\pip\\cache）", RiskLevel.Safe, CleanPipCache, () => true),
                MakeCleanItem("maven_cache", "Maven 本地仓库", "清理 Maven 本地依赖仓库（~/.m2/repository），下次构建会重新下载依赖", RiskLevel.Caution, CleanMavenCache, () => true),
                MakeCleanItem("gradle_cache", "Gradle 缓存", "清理 Gradle 构建缓存（~/.gradle/caches），下次构建会重新下载依赖", RiskLevel.Caution, CleanGradleCache, () => true),
                MakeCleanItem("nuget_cache", "NuGet 缓存", "清理 NuGet 包管理器 HTTP 缓存（不删除已安装包，仅删下载临时缓存）", RiskLevel.Safe, CleanNuGetCache, () => true)
            });

            var usageTracesGroup = new CleanGroup("电脑使用痕迹") { IsExpanded = true };
            usageTracesGroup.AddRange(new List<ICleanNode>
            {
                MakeCleanItem("etw_logs", "Windows 事件跟踪日志", "清理 Windows 事件跟踪（ETL）实时日志缓冲", RiskLevel.Safe, CleanEtwLogs, () => true),
                MakeCleanItem("recent_items", "Windows 最近访问", "清理“最近使用的项目”快捷方式与跳转列表", RiskLevel.Safe, CleanRecentItems, () => true),
                MakeCleanItem("clipboard_history", "Windows 剪贴板", "清理 Windows 剪贴板历史记录", RiskLevel.Safe, CleanClipboardHistory, () => true)
            });

            var registryCleanGroup = new CleanGroup("注册表清理") { IsExpanded = true };
            var invalidStartupItem = MakeCleanItem("invalid_startup", "无效启动项",
                "清理“开机启动”里指向已卸载程序的残留项，覆盖三处：① 注册表 Run/RunOnce（exe 已不存在）；② 启动文件夹（shell:startup / shell:common startup 中指向已删除程序的 .lnk 快捷方式，删除即移入回收站）；③ 任务计划程序（动作命令指向已删除 exe 的计划任务，删除前自动备份 XML）。仅删除失效的启动项，不影响任何仍有效的启动项。",
                RiskLevel.Safe, CleanInvalidStartup, () => true);
            var invalidComItem = MakeCleanItem("invalid_com", "失效 COM/ActiveX 组件",
                "清理类标识符（CLSID）中服务器文件（DLL/EXE）已被删除的失效组件。仅当服务器文件经校验确实不存在时才删除该 CLSID，不触碰仍在使用的组件。",
                RiskLevel.Safe, CleanInvalidCom, () => true);
            var invalidUninstallItem = MakeCleanItem("invalid_uninstall", "孤立卸载残留",
                "清理“程序卸载”信息中卸载程序已丢失的残留项（软件已删但注册表残留）。仅当卸载程序文件确实不存在时才删除，不影响可正常卸载的软件。",
                RiskLevel.Safe, CleanInvalidUninstall, () => true);
            var invalidContextMenuItem = MakeCleanItem("invalid_context_menu", "无效右键菜单",
                "清理文件/文件夹/桌面右键菜单中指向已卸载程序（exe/dll 已不存在）的失效项，包括 shell 动词与 Shell 扩展（ContextMenuHandlers）。仅删除服务器文件经校验确实不存在的项，不影响任何仍有效的右键菜单。",
                RiskLevel.Safe, CleanInvalidContextMenu, () => true);
            registryCleanGroup.AddRange(new List<ICleanNode> { invalidStartupItem, invalidComItem, invalidUninstallItem, invalidContextMenuItem });

            // 无效文件关联清理（打开方式残留）
            var invalidFileAssociationItem = MakeCleanItem("invalid_file_association", "无效文件关联",
                "清理文件属性「打开方式」中指向已卸载程序的残留项。扫描 HKCR\\Applications 下所有已注册应用，验证其 exe 是否存在，仅删除文件确实不存在的关联注册，确保不影响仍有效的打开方式。",
                RiskLevel.Safe, CleanInvalidFileAssociation, () => true);
            registryCleanGroup.AddRange(new[] { invalidFileAssociationItem });

            // 失效共享 DLL 引用
            var invalidSharedDllsItem = MakeCleanItem("invalid_shared_dlls", "失效共享 DLL 引用",
                "清理 SharedDLLs 中指向已删除 DLL 的引用计数。这些计数只用于卸载时判断 DLL 是否可安全删除，文件已不存在时是纯残留，删除不影响任何仍在使用的程序。",
                RiskLevel.Safe, CleanInvalidSharedDlls, () => true);

            // 失效 MUI 缓存
            var invalidMuiCacheItem = MakeCleanItem("invalid_mui_cache", "失效 MUI 程序名缓存",
                "清理 Windows Shell MuiCache 中指向已删除 exe 的程序名/厂商名缓存。属纯历史缓存，删除后系统会按需重建，不影响任何程序运行。",
                RiskLevel.Safe, CleanInvalidMuiCache, () => true);

            // 失效兼容性设置
            var invalidAppCompatLayersItem = MakeCleanItem("invalid_appcompat_layers", "失效兼容性设置",
                "清理 AppCompatFlags\\Layers 中为已删除 exe 保留的兼容性设置（如以管理员运行、禁用 DPI 缩放等）。文件已不存在时该设置为纯残留，删除后不影响现有程序。",
                RiskLevel.Safe, CleanInvalidAppCompatLayers, () => true);

            registryCleanGroup.AddRange(new[] { invalidSharedDllsItem, invalidMuiCacheItem, invalidAppCompatLayersItem });

            var recycleGroup = new CleanGroup("回收站") { IsExpanded = true };
            var emptyRecycleItem = MakeCleanItem("empty_recycle_bin", "清空回收站", "⚠ 将清空【所有磁盘】的回收站，包括您手动删除的文件，不可恢复。其余清理项默认先移入回收站以便误删恢复；仅当您确认不再需要回收站内任何内容时，才勾选此项。", RiskLevel.Caution, () => CleanService.EmptyRecycleBin(_logger), () => true);
            emptyRecycleItem.AlwaysShow = true; // 动作型项，不随「0B 隐藏」过滤
            emptyRecycleItem.IsChecked = false; // 回收站始终不自动勾选，由用户手动决定
            recycleGroup.AddRange(new[] { emptyRecycleItem }); // 用 AddRange 以注册 PropertyChanged，确保大小同步

            CleanGroups = new ObservableCollection<CleanGroup> { systemGroup, appGroup, usageTracesGroup, registryCleanGroup, recycleGroup };
        }

        private static CleanItem MakeCleanItem(string id, string name, string desc, RiskLevel risk, Func<CleanService.CleanResult> clean, Func<bool> canClean)
        {
            var item = new CleanItem
            {
                Id = id,
                Name = name,
                Description = desc,
                Risk = risk,
                CanClean = canClean,
                IsEnabled = risk != RiskLevel.Forbidden,
                IsChecked = risk == RiskLevel.Safe
            };
            // 新路径：直接使用返回 CleanResult 的清理动作，提供细粒度部分成功报告
            item.CleanActionWithResult = clean;
            return item;
        }

        /// <summary>
        /// 重载：接受传统的不返回值清理动作（void 方法组会自动匹配此重载，因为
        /// void 方法无法转换为 Func&lt;CleanResult&gt;）。此类项不提供细粒度部分成功报告，
        /// CleanAsync 会回退为“执行 + 异常捕获”模式：成功标 Cleaned，抛异常标 Error。
        /// </summary>
        private static CleanItem MakeCleanItem(string id, string name, string desc, RiskLevel risk, Action clean, Func<bool> canClean)
        {
            var item = new CleanItem
            {
                Id = id,
                Name = name,
                Description = desc,
                Risk = risk,
                CanClean = canClean,
                IsEnabled = risk != RiskLevel.Forbidden,
                IsChecked = risk == RiskLevel.Safe
            };
            item.CleanAction = clean;
            item.CleanActionWithResult = null;
            return item;
        }

        // ---------- 叶子项清理动作 ----------

        /// <summary>聚合多个清理结果为一个。</summary>
        private static CleanService.CleanResult Aggregate(params CleanService.CleanResult[] results)
        {
            var r = new CleanService.CleanResult();
            foreach (var res in results)
            {
                r = r with
                {
                    FilesSucceeded = r.FilesSucceeded + res.FilesSucceeded,
                    FilesFailed = r.FilesFailed + res.FilesFailed,
                    BytesFreed = r.BytesFreed + res.BytesFreed,
                    ScheduledForReboot = r.ScheduledForReboot + res.ScheduledForReboot
                };
            }
            return r;
        }

        private CleanService.CleanResult CleanTempFiles()
        {
            var winTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
            // 临时文件属“可安全永久删除”的垃圾，业界工具（CCleaner 等）均直接删除，无需回收站。
            // 直接删除可彻底绕开 SHFileOperation 在 STA 线程上的慢/卡路径，速度数量级提升，
            // 也避免“回收站模式”下数万文件逐批串行发送导致清理长时间停在“清理中”。
            var r1 = CleanService.DeleteDirContents(winTemp, _logger, false, null);
            var r2 = CleanService.DeleteDirContents(Path.GetTempPath(), _logger, false, null);
            return Aggregate(r1, r2);
        }

        private CleanService.CleanResult CleanMinidumps()
        {
            var result = new CleanService.CleanResult();
            var miniDumpDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");
            if (Directory.Exists(miniDumpDir))
                foreach (var f in Directory.GetFiles(miniDumpDir, "*.dmp"))
                    result = Aggregate(result, TryDeleteFile(f));
            foreach (var f in Directory.GetFiles(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "*.dmp"))
            {
                // MEMORY.DMP 由独立的 memory_dump 清理项处理，避免重复删除与重复计数
                if (string.Equals(Path.GetFileName(f), "MEMORY.DMP", StringComparison.OrdinalIgnoreCase)) continue;
                result = Aggregate(result, TryDeleteFile(f));
            }
            return result;
        }

        private CleanService.CleanResult TryDeleteFile(string path)
        {
            try { CleanService.DeleteFile(path, _logger); return new CleanService.CleanResult { FilesSucceeded = 1 }; }
            catch { return new CleanService.CleanResult { FilesFailed = 1 }; }
        }

        private CleanService.CleanResult CleanInternetCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return CleanService.DeleteDirContents(Path.Combine(local, @"Microsoft\Windows\INetCache"), _logger);
        }

        /// <summary>
        /// 通用浏览器多 Profile 缓存清理。
        /// Chromium 内核浏览器（Chrome/Edge/Brave/360/...）的 User Data 目录下通常包含多个 Profile：
        /// Default、Profile 1、Profile 2、...、Profile N、Guest Profile、System Profile。
        /// 每个 Profile 内有独立的 Cache/Code Cache/Media Cache/GPUCache 目录，
        /// 而 Local State、Login Data、Cookies 等用户核心数据位于 User Data 根目录（本函数绝不触碰）。
        /// 仅当目录名符合 Profile 命名规范（或 flatten=true 时把传入目录本身当一个 profile）时，
        /// 才在其下查找并清理传入的 cacheSubdir 子目录。
        /// </summary>
        /// <param name="userDataRoot">Chromium User Data 根目录</param>
        /// <param name="cacheSubdirs">每个 Profile 下要清理的缓存子目录名（如 "Cache"、"Code Cache"）</param>
        /// <param name="flatten">true 时把 userDataRoot 本身视为一个 Profile 目录（用于 Opera 这种不嵌套 User Data 的布局）</param>
        private static CleanService.CleanResult CleanBrowserProfileCaches(string userDataRoot, string[] cacheSubdirs, bool flatten = false)
        {
            if (!Directory.Exists(userDataRoot)) return default;
            if (cacheSubdirs == null || cacheSubdirs.Length == 0) return default;

            var profileDirs = new List<string>();
            if (flatten)
            {
                profileDirs.Add(userDataRoot);
            }
            else
            {
                foreach (var d in Directory.GetDirectories(userDataRoot))
                {
                    var name = Path.GetFileName(d);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (name.Equals("Default", StringComparison.Ordinal))
                        profileDirs.Add(d);
                    else if (name.StartsWith("Profile ", StringComparison.Ordinal))
                        profileDirs.Add(d);
                    else if (name.Equals("Guest Profile", StringComparison.Ordinal))
                        profileDirs.Add(d);
                    else if (name.Equals("System Profile", StringComparison.Ordinal))
                        profileDirs.Add(d);
                }
            }

            var results = new List<CleanService.CleanResult>();
            foreach (var profileDir in profileDirs)
            {
                foreach (var cacheName in cacheSubdirs)
                {
                    var p = Path.Combine(profileDir, cacheName);
                    if (Directory.Exists(p))
                        results.Add(CleanService.DeleteDirContents(p, null));
                }
            }
            return results.Count > 0 ? Aggregate(results.ToArray()) : default;
        }

        /// <summary>
        /// 按扩展名白名单清理目录内容（递归删除文件，空子目录收尾清理）。
        /// 只有文件扩展名在 allowedExtensions 中的才会被删除，其余文件一律保留。
        /// 相比 DeleteDirContents 的全量清理，更安全——用于 Telegram/Adobe 这类缓存目录
        /// 里混杂了用户数据/索引数据库的场景。
        /// 重解析点（junction/symlink/mount point）仍然跳过，不跟随进入。
        /// </summary>
        private static void CleanDirByExtensionWhitelist(string path, HashSet<string> allowedExtensions, ILogger logger)
        {
            if (!Directory.Exists(path)) return;
            try
            {
                var allFiles = new List<string>();
                CollectFilesRecursivelySafe(path, allFiles, logger);
                foreach (var f in allFiles)
                {
                    try
                    {
                        string ext = Path.GetExtension(f);
                        if (string.IsNullOrWhiteSpace(ext)) continue;
                        if (!allowedExtensions.Contains(ext)) continue;
                        CleanService.DeleteFile(f, logger);
                    }
                    catch (Exception ex)
                    {
                        logger?.Error($"白名单清理文件失败: {f}", ex);
                    }
                }
                // 最后清理因删除文件变空的子目录（非递归，从底层向上）
                TryDeleteEmptySubdirs(path);
            }
            catch (Exception ex)
            {
                logger?.Error($"白名单清理目录失败: {path}", ex);
            }
        }

        /// <summary>
        /// 安全递归收集文件（跳过重解析点子目录），与 CleanService 内部逻辑一致。
        /// </summary>
        private static void CollectFilesRecursivelySafe(string path, List<string> files, ILogger logger)
        {
            try
            {
                // 单次枚举 + 原生跳过 reparse 点（junction/symlink/mount），避免
                // 逐文件 EnumerateFiles + 每子目录单独 IsReparsePoint 各做一次 stat。
                // FileSystemInfo.Length/Attributes 在 Windows 目录枚举时已由 FindFirstFile 缓存，无需再次 stat。
                var opts = new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", opts))
                {
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                        CollectFilesRecursivelySafe(entry.FullName, files, logger);
                    else
                        files.Add(entry.FullName);
                }
            }
            catch (Exception ex)
            {
                logger?.Error($"枚举目录失败(白名单): {path}", ex);
            }
        }

        /// <summary>
        /// 估算目录中指定扩展名文件的总大小（与 CleanDirByExtensionWhitelist 的清理范围一致）。
        /// </summary>
        private static long DirSizeByExtensionWhitelist(string path, HashSet<string> allowedExtensions)
        {
            if (!Directory.Exists(path)) return 0;
            long size = 0;
            try
            {
                var files = new List<string>();
                CollectFilesRecursivelySafe(path, files, null);
                foreach (var f in files)
                {
                    try
                    {
                        string ext = Path.GetExtension(f);
                        if (string.IsNullOrWhiteSpace(ext)) continue;
                        if (!allowedExtensions.Contains(ext)) continue;
                        size += new FileInfo(f).Length;
                    }
                    catch { }
                }
            }
            catch { }
            return size;
        }

        /// <summary>
        /// 自下而上清理空的子目录（不碰根目录本身）。
        /// 安全保护与 CleanService.DeleteEmptySubdirectories 保持一致：
        ///   - 只删真正为空的目录（GetFileSystemEntries 返回 0）
        ///   - 跳过重解析点（junction/symlink/mount point）
        ///   - 跳过系统属性目录
        ///   - 清除只读属性后再删
        ///   - 使用 Directory.Delete(dir, false) 非递归删除（仅删空目录，非空会抛异常被 catch）
        /// </summary>
        private static void TryDeleteEmptySubdirs(string root)
        {
            try
            {
                // 单次枚举 + 原生跳过 reparse 点（junction/symlink/mount），避免
                // Directory.GetDirectories 全量数组 + 每目录单独 IsReparsePoint 再做一次 GetAttributes stat。
                var emptyOpts = new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                foreach (var dirInfo in new DirectoryInfo(root).EnumerateDirectories("*", emptyOpts))
                {
                    var dir = dirInfo.FullName;
                    TryDeleteEmptySubdirs(dir);
                    try
                    {
                        if (!Directory.Exists(dir)) continue;
                        // 一次属性读取同时用于 reparse / 系统 / 只读判断，避免重复 stat。
                        var attr = dirInfo.Attributes;
                        // 跳过重解析点（junction/symlink/mount point），避免跟随链接误删目标
                        if ((attr & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                            continue;
                        // 跳过系统属性目录，与 CleanService 一致
                        if ((attr & FileAttributes.System) == FileAttributes.System)
                            continue;
                        // 仅删除真正为空的目录（无文件、无子目录）
                        if (Directory.GetFileSystemEntries(dir, "*", SearchOption.TopDirectoryOnly).Length == 0)
                        {
                            // 清除只读属性（只读目录无法直接删除）
                            if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                                File.SetAttributes(dir, attr & ~FileAttributes.ReadOnly);
                            // recursive:false —— 仅删空目录本身，不递归
                            Directory.Delete(dir, false);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private CleanService.CleanResult CleanEdgeCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userData = Path.Combine(local, @"Microsoft\Edge\User Data");
            return CleanBrowserProfileCaches(userData, new[] { "Cache", "Code Cache" });
        }

        private CleanService.CleanResult CleanQuarkCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var r1 = CleanBrowserProfileCaches(Path.Combine(local, @"Quark\Quark\User Data"), new[] { "Cache", "Code Cache" });
            var r2 = CleanBrowserProfileCaches(Path.Combine(local, @"夸克浏览器\User Data"), new[] { "Cache", "Code Cache" });
            var appCache = Path.Combine(local, @"Quark\Application\Cache");
            var r3 = Directory.Exists(appCache) ? CleanService.DeleteDirContents(appCache, _logger) : default;
            return Aggregate(r1, r2, r3);
        }

        private CleanService.CleanResult CleanChromeCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userData = Path.Combine(local, @"Google\Chrome\User Data");
            return CleanBrowserProfileCaches(userData, new[] { "Cache", "Code Cache", "Media Cache" });
        }

        private CleanService.CleanResult CleanFirefoxCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var profilesRoot = Path.Combine(local, @"Mozilla\Firefox\Profiles");
            if (!Directory.Exists(profilesRoot)) return default;
            var results = new List<CleanService.CleanResult>();
            foreach (var profile in Directory.GetDirectories(profilesRoot))
            {
                results.Add(CleanService.DeleteDirContents(Path.Combine(profile, "cache2"), _logger));
                results.Add(CleanService.DeleteDirContents(Path.Combine(profile, "thumbnails"), _logger));
                results.Add(CleanService.DeleteDirContents(Path.Combine(profile, "cache"), _logger));
            }
            return results.Count > 0 ? Aggregate(results.ToArray()) : default;
        }

        private CleanService.CleanResult CleanBraveCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userData = Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data");
            return CleanBrowserProfileCaches(userData, new[] { "Cache", "Code Cache" });
        }

        private CleanService.CleanResult CleanOperaCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userData = Path.Combine(local, @"Opera Software");
            if (!Directory.Exists(userData)) return default;
            var results = new List<CleanService.CleanResult>();
            foreach (var browserDir in Directory.GetDirectories(userData))
                results.Add(CleanBrowserProfileCaches(browserDir, new[] { "Cache", "Code Cache" }, flatten: true));
            return results.Count > 0 ? Aggregate(results.ToArray()) : default;
        }

        private CleanService.CleanResult CleanDingTalkCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(local, @"DingTalk\Cache"),
                Path.Combine(local, @"DingTalk\update")
            };
            var results = new List<CleanService.CleanResult>();
            foreach (var c in candidates)
                if (Directory.Exists(c)) results.Add(CleanService.DeleteDirContents(c, _logger));
            return results.Count > 0 ? Aggregate(results.ToArray()) : default;
        }

        private CleanService.CleanResult CleanNetEaseMusicCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var cache = Path.Combine(local, @"NetEase\CloudMusic\Cache");
            return Directory.Exists(cache) ? CleanService.DeleteDirContents(cache, _logger) : default;
        }

        private CleanService.CleanResult CleanSteamCache()
        {
            var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var candidates = new[]
            {
                Path.Combine(x86, @"Steam\appcache"),
                Path.Combine(x86, @"Steam\httpcache"),
                Path.Combine(x86, @"Steam\config\htmlcache")
            };
            var results = new List<CleanService.CleanResult>();
            foreach (var c in candidates)
                if (Directory.Exists(c)) results.Add(CleanService.DeleteDirContents(c, _logger));
            return results.Count > 0 ? Aggregate(results.ToArray()) : default;
        }

        private CleanService.CleanResult CleanPrefetch()
        {
            var prefetch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
            return Directory.Exists(prefetch) ? CleanService.DeleteDirContents(prefetch, _logger) : default;
        }

        private CleanService.CleanResult CleanMemoryDump()
        {
            var dump = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "MEMORY.DMP");
            return File.Exists(dump) ? TryDeleteFile(dump) : default;
        }

        private CleanService.CleanResult CleanStoreCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var packages = Path.Combine(local, "Packages");
            if (!Directory.Exists(packages)) return default;
            var results = new List<CleanService.CleanResult>();
            foreach (var pkg in Directory.GetDirectories(packages))
            {
                if (Path.GetFileName(pkg).StartsWith("Microsoft.WindowsStore", StringComparison.OrdinalIgnoreCase))
                {
                    var cache = Path.Combine(pkg, "LocalCache");
                    if (Directory.Exists(cache)) results.Add(CleanService.DeleteDirContents(cache, _logger));
                }
            }
            return results.Count > 0 ? Aggregate(results.ToArray()) : default;
        }

        private CleanService.CleanResult CleanBaiduPanCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(local, @"Baidu\BaiduNetdisk\cache"),
                Path.Combine(local, @"Baidu\BaiduNetdisk\tmp")
            };
            var results = new List<CleanService.CleanResult>();
            foreach (var c in candidates)
                if (Directory.Exists(c)) results.Add(CleanService.DeleteDirContents(c, _logger));
            return results.Count > 0 ? Aggregate(results.ToArray()) : default;
        }

        private CleanService.CleanResult CleanThunderCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(local, @"Thunder Network\Thunder\Profiles\Cache"),
                Path.Combine(local, @"Thunder Network\XLLiveUD\Cache")
            };
            var results = new List<CleanService.CleanResult>();
            foreach (var c in candidates)
                if (Directory.Exists(c)) results.Add(CleanService.DeleteDirContents(c, _logger));
            return results.Count > 0 ? Aggregate(results.ToArray()) : default;
        }

        private CleanService.CleanResult CleanBilibiliCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[] { Path.Combine(local, @"bilibili\Cache"), Path.Combine(local, @"Bilibili\Cache") };
            var results = new List<CleanService.CleanResult>();
            foreach (var c in candidates) if (Directory.Exists(c)) results.Add(CleanService.DeleteDirContents(c, _logger));
            return results.Count > 0 ? Aggregate(results.ToArray()) : default;
        }

        private CleanService.CleanResult CleanIqiyiCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[] { Path.Combine(local, @"Qiyi\QiyiClient\cache"), Path.Combine(local, @"Qiyi\QiyiClient\webcache") };
            var results = new List<CleanService.CleanResult>();
            foreach (var c in candidates) if (Directory.Exists(c)) results.Add(CleanService.DeleteDirContents(c, _logger));
            return results.Count > 0 ? Aggregate(results.ToArray()) : default;
        }

        private CleanService.CleanResult CleanDiscordCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var stable = Path.Combine(local, @"Discord");
            var results = new List<CleanService.CleanResult>();
            foreach (var c in new[] { "Cache", "Code Cache", "GPUCache" })
            {
                var p = Path.Combine(stable, c);
                if (Directory.Exists(p)) results.Add(CleanService.DeleteDirContents(p, _logger));
            }
            return results.Count > 0 ? Aggregate(results.ToArray()) : default;
        }

        private CleanService.CleanResult CleanBrowser360Cache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userData = Path.Combine(local, @"360Chrome\Chrome\User Data");
            return CleanBrowserProfileCaches(userData, new[] { "Cache", "Code Cache" });
        }

        private CleanService.CleanResult CleanSogouCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[] { Path.Combine(local, @"SogouExplorer\Cache"), Path.Combine(local, @"SogouExplorer\WebkitCache") };
            var results = new List<CleanService.CleanResult>();
            foreach (var c in candidates) if (Directory.Exists(c)) results.Add(CleanService.DeleteDirContents(c, _logger));
            return results.Count > 0 ? Aggregate(results.ToArray()) : default;
        }

        private CleanService.CleanResult CleanFeishuCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[] { Path.Combine(local, @"Feishu\Cache"), Path.Combine(local, @"Feishu\Application\Cache"), Path.Combine(local, @"Feishu\user_data\cache") };
            var results = new List<CleanService.CleanResult>();
            foreach (var c in candidates) if (Directory.Exists(c)) results.Add(CleanService.DeleteDirContents(c, _logger));
            return results.Count > 0 ? Aggregate(results.ToArray()) : default;
        }

        private CleanService.CleanResult CleanWeMeetCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[] { Path.Combine(local, @"Tencent\WeMeet\Cache"), Path.Combine(local, @"Tencent\WeMeet\AppCache") };
            var results = new List<CleanService.CleanResult>();
            foreach (var c in candidates) if (Directory.Exists(c)) results.Add(CleanService.DeleteDirContents(c, _logger));
            return results.Count > 0 ? Aggregate(results.ToArray()) : default;
        }

        private CleanService.CleanResult CleanJianyingCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(local, @"JianyingPro\Cache"),
                Path.Combine(local, @"JianyingPro\User Data\Cache"),
                Path.Combine(local, @"JianyingPro\User Data\GPUCache")
            };
            var results = new List<CleanService.CleanResult>();
            foreach (var c in candidates) if (Directory.Exists(c)) results.Add(CleanService.DeleteDirContents(c, _logger));
            return results.Count > 0 ? Aggregate(results.ToArray()) : default;
        }

        private void CleanTelegramCache()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var candidates = new[]
            {
                Path.Combine(appData, @"Telegram Desktop\tdata\cache"),
                Path.Combine(appData, @"Telegram Desktop\tdata\media_cache")
            };
            // 只删确定为缓存性质的文件扩展名；tdata 根目录含 map/key/哈希名目录是用户会话数据库，绝不触碰
            var safeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".cache", ".blob", ".bin", ".tmp", ".dat", ".jpg", ".jpeg", ".png", ".webp", ".gif",
                ".mp4", ".mp3", ".ogg", ".webm", ".mov", ".mkv", ".avi"
            };
            foreach (var c in candidates)
                if (Directory.Exists(c))
                    CleanDirByExtensionWhitelist(c, safeExts, _logger);
        }

        private void CleanSpotifyCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            // 仅清理 cache 与 Storage（真正的临时缓存）；
            // 严禁清理 Data 目录 —— 包含已下载的离线播放音乐，删除会导致离线播放列表全部丢失！
            var candidates = new[]
            {
                Path.Combine(local, @"Spotify\cache"),
                Path.Combine(appData, @"Spotify\Storage")
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) CleanService.DeleteDirContents(c, _logger);
        }

        private void CleanAdobeCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // 媒体缓存文件扩展名：仅删确认是媒体预渲染/波形缓存的文件，
            // .mcdb 是索引数据库（删了 PR/AE 下次会重新扫描所有素材耗时长），保留
            var adobeMediaCacheExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".pek", ".cfa", ".ims", ".xmp"
            };
            var candidates = new[]
            {
                Path.Combine(local, @"Adobe\Common\Media Cache Files"),
                Path.Combine(local, @"Adobe\Common\Media Cache")
            };
            foreach (var c in candidates)
                if (Directory.Exists(c))
                    CleanDirByExtensionWhitelist(c, adobeMediaCacheExts, _logger);
            // Premiere Pro 媒体缓存位于版本化子目录下，需枚举
            var premiereRoot = Path.Combine(local, @"Adobe\Premiere Pro");
            if (Directory.Exists(premiereRoot))
                foreach (var ver in Directory.GetDirectories(premiereRoot))
                {
                    var mc = Path.Combine(ver, "Media Cache Files");
                    if (Directory.Exists(mc))
                        CleanDirByExtensionWhitelist(mc, adobeMediaCacheExts, _logger);
                }
        }

        private void CleanKuaishouCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(local, @"Kuaishou\Cache"),
                Path.Combine(local, @"Kuaishou\Application\Cache")
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) CleanService.DeleteDirContents(c, _logger);
        }

        // ═══════════════════════════════════════════════════════════════
        //  新增：系统级遗漏清理项
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 清理 Windows 大版本升级遗留的临时文件（C:\$WINDOWS.~BT、$WINDOWS.~WS、$WINDOWS.~Q）。
        /// 这些目录在 Windows 10/11 大版本升级完成后会残留数 GB 到数十 GB 的安装文件，
        /// 升级已完成且用户确认无需回滚旧系统时即可安全删除。
        /// </summary>
        private void CleanWindowsUpgradeTemp()
        {
            var sysDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (string.IsNullOrEmpty(sysDrive)) return;
            var candidates = new[]
            {
                Path.Combine(sysDrive, "$WINDOWS.~BT"),
                Path.Combine(sysDrive, "$WINDOWS.~WS"),
                Path.Combine(sysDrive, "$WINDOWS.~Q")
            };
            foreach (var c in candidates)
                if (Directory.Exists(c))
                    CleanService.DeleteDir(c, _logger);
        }

        /// <summary>
        /// 清理 Windows Defender 扫描历史与隔离区元数据。
        /// 仅清理 Scans\History\Service\DetectionHistory 与 Scans\History\Service\DtlQuarantine 的元数据，
        /// 不删除 ProgramData\Microsoft\Windows Defender 平台目录本身（Defender 服务运行所需）。
        /// </summary>
        private void CleanDefenderHistory()
        {
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var scansRoot = Path.Combine(common, @"Microsoft\Windows Defender\Scans\History");
            if (!Directory.Exists(scansRoot)) return;
            var candidates = new[]
            {
                Path.Combine(scansRoot, @"Service\DetectionHistory"), // 历史检测记录
                Path.Combine(scansRoot, @"Service\DtlQuarantine"),    // 隔离文件元数据
                Path.Combine(scansRoot, @"Store"),                    // 旧版存储
                Path.Combine(scansRoot, @"ParserStore")               // 解析缓存
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) CleanService.DeleteDirContents(c, _logger);
        }

        /// <summary>
        /// 清理 Windows 字体缓存服务（FontCache）的临时数据。
        /// 位于 C:\Windows\ServiceProfiles\LocalService\AppData\Local\FontCache。
        /// 删除后系统会自动重建（FontCache 服务会重新生成 .dat 缓存数据库）。
        /// </summary>
        private void CleanFontCache()
        {
            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var fontCacheDir = Path.Combine(winDir, @"ServiceProfiles\LocalService\AppData\Local\FontCache");
            if (!Directory.Exists(fontCacheDir)) return;
            // 仅删除 .dat 缓存文件，保留目录本身与服务运行时锁定的活动文件
            foreach (var f in Directory.GetFiles(fontCacheDir, "*.dat"))
            {
                try { CleanService.DeleteFile(f, _logger); }
                catch (Exception ex) { _logger?.Error($"清理字体缓存失败: {f}", ex); }
            }
        }

        /// <summary>
        /// 清理 C:\Windows\LiveKernelReports 中的实时内核崩溃报告。
        /// 这些 .dmp / .cab 文件由 Windows 在驱动崩溃时生成，体积可达数百 MB。
        /// </summary>
        private void CleanLiveKernelReports()
        {
            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var dir = Path.Combine(winDir, "LiveKernelReports");
            if (!Directory.Exists(dir)) return;
            CleanService.DeleteDirContents(dir, _logger);
        }

        /// <summary>
        /// 清理 %LocalAppData%\IconCache.db 图标缓存数据库。
        /// 注意：这是 LocalAppData 根目录下的单个文件，与 thumbnail_cache 清理的
        /// Microsoft\Windows\Explorer\iconcache_*.db 是不同的文件，二者互补。
        /// 删除后系统会自动重建。
        /// </summary>
        private void CleanIconCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var iconCache = Path.Combine(local, "IconCache.db");
            if (File.Exists(iconCache))
                CleanService.DeleteFile(iconCache, _logger);
        }

        // ═══════════════════════════════════════════════════════════════
        //  新增：即时通讯
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 清理微信 PC 客户端缓存。
        /// 仅清理 FileStorage\Cache（真正的临时缓存目录）。
        /// 严禁清理以下目录（均为用户数据，删除会导致聊天内容丢失）：
        ///   - FileStorage\Msg      : 文字聊天记录数据库（删除=丢聊天记录）
        ///   - FileStorage\MsgAttach : 聊天附件（图片/视频/语音原文件）
        ///   - FileStorage\Image     : 聊天图片原文件（删除=图片显示"已过期"）
        ///   - FileStorage\Video     : 聊天视频原文件（删除=视频无法播放）
        ///   - FileStorage\File      : 接收的文件
        /// 微信不像浏览器有"纯缓存"概念，Image/Video 存的就是聊天原文件！
        /// </summary>
        private void CleanWeChatCache()
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var wechatRoot = Path.Combine(documents, "WeChat Files");
            if (!Directory.Exists(wechatRoot))
            {
                // 兼容自定义 Documents 路径失败的情况，回退到 LocalAppData
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                wechatRoot = Path.Combine(local, @"Tencent\WeChat");
            }
            if (!Directory.Exists(wechatRoot)) return;

            foreach (var wxidDir in Directory.GetDirectories(wechatRoot))
            {
                // 跳过 All Users 等系统目录
                var dirName = Path.GetFileName(wxidDir);
                if (dirName.Equals("All Users", StringComparison.OrdinalIgnoreCase)) continue;

                var fileStorage = Path.Combine(wxidDir, "FileStorage");
                if (!Directory.Exists(fileStorage)) continue;

                // 仅清理 Cache（真正的临时缓存）。
                // 绝不清理 Image/Video/MsgAttach/File/Msg —— 这些是聊天原文件与消息数据库！
                var cacheOnly = Path.Combine(fileStorage, "Cache");
                if (Directory.Exists(cacheOnly))
                    CleanService.DeleteDirContents(cacheOnly, _logger);
            }
        }

        /// <summary>
        /// 清理 QQ PC 客户端缓存。
        /// 仅清理 RichMedia（富媒体消息的缩略图缓存），保留聊天原文件。
        /// 严禁清理以下目录（均为用户数据）：
        ///   - Image    : 聊天图片原文件（删除=图片无法查看）
        ///   - Video    : 聊天视频原文件（删除=视频无法播放）
        ///   - FileRecv : 接收的文件
        ///   - Emoji    : 自定义表情
        /// QQ 的 RichMedia 存的是富媒体缩略图缓存，清理相对安全。
        /// </summary>
        private void CleanQQCache()
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var tencentRoot = Path.Combine(documents, "Tencent Files");
            if (!Directory.Exists(tencentRoot)) return;
            foreach (var qqDir in Directory.GetDirectories(tencentRoot))
            {
                var dirName = Path.GetFileName(qqDir);
                // 跳过 All Users 等系统目录
                if (dirName.Equals("All Users", StringComparison.OrdinalIgnoreCase)) continue;

                // 仅清理 RichMedia（富媒体缩略图缓存）。
                // 绝不清理 Image/Video/FileRecv —— 这些是聊天原文件与接收文件！
                var richMedia = Path.Combine(qqDir, "RichMedia");
                if (Directory.Exists(richMedia))
                    CleanService.DeleteDirContents(richMedia, _logger);
            }
        }

        /// <summary>
        /// 清理企业微信客户端缓存。
        /// 仅清理 Cache 目录（真正的临时缓存），保留 Image/Video/Msg 等聊天原文件与消息数据库。
        /// 企业微信目录结构与微信类似：Image/Video 存的是聊天原文件，非纯缓存。
        /// </summary>
        private void CleanWXWorkCache()
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var wxworkRoot = Path.Combine(documents, "WXWork");
            if (!Directory.Exists(wxworkRoot)) return;
            foreach (var uidDir in Directory.GetDirectories(wxworkRoot))
            {
                // 仅清理 Cache（真正的临时缓存），绝不清理 Image/Video/Msg 等聊天原文件
                var cacheOnly = Path.Combine(uidDir, "Cache");
                if (Directory.Exists(cacheOnly))
                    CleanService.DeleteDirContents(cacheOnly, _logger);
            }
        }

        /// <summary>清理网易邮箱大师客户端缓存。</summary>
        private void CleanMailMasterCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(local, @"MailMaster\Cache"),
                Path.Combine(local, @"MailMaster\Application\Cache")
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) CleanService.DeleteDirContents(c, _logger);
        }

        // ═══════════════════════════════════════════════════════════════
        //  新增：浏览器
        // ═══════════════════════════════════════════════════════════════

        /// <summary>清理 QQ 浏览器缓存（Chromium 内核，多 Profile 支持）。</summary>
        private void CleanQQBrowserCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userData = Path.Combine(local, @"Tencent\QQBrowser\User Data");
            CleanBrowserProfileCaches(userData, new[] { "Cache", "Code Cache" });
        }

        /// <summary>清理 Vivaldi 浏览器缓存（Chromium 内核，多 Profile 支持）。</summary>
        private void CleanVivaldiCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userData = Path.Combine(local, @"Vivaldi\User Data");
            CleanBrowserProfileCaches(userData, new[] { "Cache", "Code Cache" });
        }

        // ═══════════════════════════════════════════════════════════════
        //  新增：音视频
        // ═══════════════════════════════════════════════════════════════

        /// <summary>清理 QQ 音乐客户端缓存。</summary>
        private void CleanQQMusicCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(local, @"Tencent\QQMusic\Cache"),
                Path.Combine(local, @"QQMusic\Cache")
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) CleanService.DeleteDirContents(c, _logger);
        }

        /// <summary>清理酷狗音乐客户端缓存。</summary>
        private void CleanKugouCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(local, @"KuGou\Cache"),
                Path.Combine(local, @"KuGou8\Cache")
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) CleanService.DeleteDirContents(c, _logger);
        }

        /// <summary>清理酷我音乐客户端缓存。</summary>
        private void CleanKuwoCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(local, @"KuwoMusic\Cache"),
                Path.Combine(local, @"Kuwo\Cache")
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) CleanService.DeleteDirContents(c, _logger);
        }

        /// <summary>清理腾讯视频客户端缓存。</summary>
        private void CleanQQLiveCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(local, @"Tencent\QQLive\Cache"),
                Path.Combine(local, @"QQLive\Cache")
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) CleanService.DeleteDirContents(c, _logger);
        }

        /// <summary>清理优酷客户端缓存。</summary>
        private void CleanYoukuCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(local, @"Youku\YoukuClient\cache"),
                Path.Combine(local, @"Youku\cache")
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) CleanService.DeleteDirContents(c, _logger);
        }

        // ═══════════════════════════════════════════════════════════════
        //  新增：办公与游戏
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 清理 WPS Office 缓存文件。
        /// 仅清理 cache 目录（真正的临时缓存），保留文档与配置。
        /// 严禁清理 backup 目录 —— 包含文档自动备份/恢复文件，类似 Office AutoRecover，
        /// 删除可能导致用户无法恢复未保存的文档！
        /// </summary>
        private void CleanWPSCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(local, @"Kingsoft\office6\cache"),
                Path.Combine(local, @"WPS\cache")
                // 注意：不清理 Kingsoft\office6\backup —— 包含文档自动备份/恢复文件，
                // 删除可能导致用户无法恢复未保存的文档！
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) CleanService.DeleteDirContents(c, _logger);
        }

        /// <summary>清理 Epic Games Launcher 缓存（保留已下载游戏库）。</summary>
        private void CleanEpicCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(local, @"Epic\EpicGamesLauncher\Data\EMS\cache"),
                Path.Combine(local, @"Epic\EpicGamesLauncher\Data\EMS\EPIC"),
                Path.Combine(local, @"Epic\EpicGamesLauncher\WebCache")
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) CleanService.DeleteDirContents(c, _logger);
        }

        // ═══════════════════════════════════════════════════════════════
        //  新增：开发工具缓存
        // ═══════════════════════════════════════════════════════════════

        /// <summary>清理 Visual Studio Code 缓存、CachedData、Code Cache、GPUCache。</summary>
        private void CleanVSCodeCache()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var candidates = new[]
            {
                Path.Combine(appData, @"Code\Cache"),
                Path.Combine(appData, @"Code\CachedData"),
                Path.Combine(appData, @"Code\Code Cache"),
                Path.Combine(appData, @"Code\GPUCache")
                // 注意：不清理 Code\User\workspaceStorage —— 该目录存储每个工作区的状态
                // （打开的文件、断点、折叠节点、运行配置等），删除会丢失工作区状态影响开发体验。
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) CleanService.DeleteDirContents(c, _logger);
        }

        /// <summary>
        /// 清理 JetBrains 系列 IDE（IntelliJ IDEA / PyCharm / WebStorm / Rider / CLion / Android Studio 等）的 caches、log、tmp 目录。
        /// 保留 config、projects、plugins 等用户配置与项目数据。
        /// </summary>
        private void CleanJetBrainsCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var jbRoot = Path.Combine(local, @"JetBrains");
            if (!Directory.Exists(jbRoot)) return;
            foreach (var productDir in Directory.GetDirectories(jbRoot))
            {
                // 每个产品目录下有 caches、log、tmp 等子目录
                var subDirs = new[] { "caches", "log", "tmp", "index" };
                foreach (var sub in subDirs)
                {
                    var p = Path.Combine(productDir, sub);
                    if (Directory.Exists(p)) CleanService.DeleteDirContents(p, _logger);
                }
            }
            // 兼容旧版本：Android Studio 在独立目录
            var androidStudioRoot = Path.Combine(local, @"Google\AndroidStudio");
            if (Directory.Exists(androidStudioRoot))
                foreach (var verDir in Directory.GetDirectories(androidStudioRoot))
                {
                    var subDirs = new[] { "caches", "log", "tmp", "index" };
                    foreach (var sub in subDirs)
                    {
                        var p = Path.Combine(verDir, sub);
                        if (Directory.Exists(p)) CleanService.DeleteDirContents(p, _logger);
                    }
                }
        }

        /// <summary>清理 Visual Studio 组件模型缓存与 IDE 缓存。</summary>
        private void CleanVSComponentCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var vsRoot = Path.Combine(local, @"Microsoft\VisualStudio");
            if (!Directory.Exists(vsRoot)) return;
            foreach (var verDir in Directory.GetDirectories(vsRoot))
            {
                var componentCache = Path.Combine(verDir, "ComponentModelCache");
                if (Directory.Exists(componentCache))
                    CleanService.DeleteDirContents(componentCache, _logger);
            }
            // 兼容 VS 2019+ 的私有注册表与 MEFCache；遇到不可访问的子目录不要中断整个清理
            try
            {
                var mefCacheDirs = Directory.GetDirectories(vsRoot, "MEFCache", SearchOption.AllDirectories);
                foreach (var d in mefCacheDirs)
                    if (Directory.Exists(d)) CleanService.DeleteDirContents(d, _logger);
            }
            catch (Exception ex) { _logger?.Error($"枚举 MEFCache 目录失败: {vsRoot}", ex); }
        }

        /// <summary>清理 npm 包管理器缓存（C:\Users\{user}\AppData\Local\npm-cache）。</summary>
        private void CleanNpmCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var cache = Path.Combine(local, "npm-cache");
            if (Directory.Exists(cache)) CleanService.DeleteDirContents(cache, _logger);
        }

        /// <summary>清理 Python pip 包管理器缓存。</summary>
        private void CleanPipCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // Windows 文件系统不区分大小写，pip\cache 与 pip\Cache 是同一目录，只清一次
            var cache = Path.Combine(local, @"pip\cache");
            if (Directory.Exists(cache)) CleanService.DeleteDirContents(cache, _logger);
        }

        /// <summary>
        /// 清理 Maven 本地依赖仓库（~/.m2/repository）。
        /// 下次构建项目时会重新从远程仓库下载依赖，仅影响构建速度，不影响代码。
        /// 标记为 Caution：清后重新下载可能耗时较长。
        /// </summary>
        private void CleanMavenCache()
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var repo = Path.Combine(userHome, @".m2\repository");
            if (Directory.Exists(repo))
                CleanService.DeleteDirContents(repo, _logger);
        }

        /// <summary>
        /// 清理 Gradle 构建缓存（~/.gradle/caches）。
        /// 下次构建项目时会重新下载依赖，仅影响构建速度，不影响代码。
        /// 标记为 Caution：清后重新下载可能耗时较长。
        /// </summary>
        private void CleanGradleCache()
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var caches = Path.Combine(userHome, @".gradle\caches");
            if (Directory.Exists(caches))
                CleanService.DeleteDirContents(caches, _logger);
        }

        /// <summary>清理 NuGet HTTP 缓存（不删除已安装包，仅删下载临时缓存）。</summary>
        private void CleanNuGetCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // v3 HTTP 缓存（.NET Core / .NET 5+）
            var httpCache = Path.Combine(local, @"NuGet\v3-cache");
            if (Directory.Exists(httpCache)) CleanService.DeleteDirContents(httpCache, _logger);
            // v2 HTTP 缓存（旧版 .NET Framework）
            var v2Cache = Path.Combine(local, @"NuGet\Cache");
            if (Directory.Exists(v2Cache)) CleanService.DeleteDirContents(v2Cache, _logger);
        }

        private void CleanThumbnailCache()
        {
            var explorerDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Explorer");
            if (!Directory.Exists(explorerDir)) return;
            foreach (var f in Directory.GetFiles(explorerDir, "thumbcache_*.db")
                .Concat(Directory.GetFiles(explorerDir, "iconcache_*.db")))
            {
                CleanService.DeleteFile(f, _logger);
            }
        }

        private void CleanDeliveryOptimization()
        {
            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            CleanService.DeleteDirContents(Path.Combine(winDir, @"SoftwareDistribution\DeliveryOptimization"), _logger);
            CleanService.DeleteDirContents(Path.Combine(winDir, @"SoftwareDistribution\Download"), _logger);
        }

        private void CleanWindowsWidgets()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            CleanService.DeleteDirContents(
                Path.Combine(local, @"Packages\MicrosoftWindows.Client.WebExperience_cw5n1h2txyewy\LocalCache"), _logger);
        }

        private void CleanDirectXShaderCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            CleanService.DeleteDirContents(Path.Combine(local, "D3DSCache"), _logger);
        }

        private void CleanCertCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            CleanService.DeleteDirContents(Path.Combine(local, @"Microsoft\CryptnetUrlCache"), _logger);
        }

        private CleanService.CleanResult CleanEtwLogs()
        {
            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var r1 = CleanService.DeleteDirContents(Path.Combine(winDir, @"System32\LogFiles\WMI\RtBackup"), _logger);
            var r2 = CleanService.DeleteDirContents(Path.Combine(winDir, @"Logs\MeasuredBoot"), _logger);
            return Aggregate(r1, r2);
        }

        private void CleanRecentItems()
        {
            var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (Directory.Exists(recent))
            {
                // 仅删除「最近使用的项目」.lnk 快捷方式；
                // AutomaticDestinations / CustomDestinations 保存的是 Quick Access 与跳转列表数据，
                // 删除会导致「此电脑」左侧导航栏 / 快速访问被重置为系统默认，故不再清理。
                foreach (var f in Directory.GetFiles(recent, "*.lnk"))
                    CleanService.DeleteFile(f, _logger);
            }
        }

        private void CleanClipboardHistory()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            CleanService.DeleteDirContents(Path.Combine(local, @"Microsoft\Windows\Clipboard"), _logger);
        }

        /// <summary>
        /// 仅清理 HKCU 下"纯历史 / 最近使用（MRU）"痕迹键的白名单。
        // =====================================================================
        // 注册表清理：仅清理“已失效 / 无效”的注册表项（安全）
        // 判定标准：注册表项引用了一个【磁盘上已不存在】的文件（开机启动项、COM/ActiveX
        // 服务器、软件卸载程序）。这类引用指向的软件早已不存在，删除孤立引用不会破坏任何
        // 仍在使用的功能，属于业界公认的“安全垃圾”。绝不扫描 / 删除文件关联、驱动、系统关键项。
        // =====================================================================

        /// <summary>
        /// 判断一个注册表“命令 / 路径”值是否指向磁盘上已不存在的文件（即失效引用）。
        /// 仅当：值是绝对路径、展开环境变量并兼顾 WOW64 重定向后仍不存在时，才判定为“失效”。
        /// 系统命令（如 msiexec.exe /X...、rundll32）、文件仍存在时一律返回 false。
        /// </summary>
        private static bool IsDeadFileReference(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            var s = command.Trim();

            // 安全护栏：下列命令即使无法逐字解析到存在的 exe，也绝不视为“失效”，
            // 避免误删 Windows 内置 / Microsoft Store(UWP) / 协议类 右键项（含 Win11 第一层正常项）。
            // 宁可漏清（保留一个失效残留），不可误删（毁掉用户正常功能）。
            if (LooksLikeSystemOrSafeCommand(s)) return false;

            string exe;
            if (s.StartsWith("\""))
            {
                var end = s.IndexOf('"', 1);
                exe = end > 1 ? s.Substring(1, end - 1) : s;
            }
            else
            {
                // 未加引号的命令：可执行文件路径本身可能包含空格
                // （如 C:\Program Files\WinRAR\Uninstall.exe 或 C:\Program Files\WinRAR\WinRAR.exe "%1"）。
                // 不能简单地按第一个空格截断，否则会把 "C:\Program" 误判为失效路径，
                // 进而把“已安装软件”的卸载项/右键菜单当成失效项删除（典型的误杀，如 WinRAR）。
                // 这里从前往后累加空格分隔的片段，找到第一个真实存在的可执行文件。
                exe = ResolveExecutableFromUnquoted(s);
            }

            exe = Environment.ExpandEnvironmentVariables(exe).Trim();
            if (string.IsNullOrWhiteSpace(exe) || !Path.IsPathRooted(exe)) return false;
            // 用 WOW64 重定向安全的检查：32 位宿主进程也能正确识别 64 位 Program Files 下的真实文件，
            // 避免把 Bandizip/火绒 等已安装软件的 Win11 一级菜单误判为失效而删除。
            if (NativeMethods.FileExistsResilient(exe)) return false;

            // WOW64 重定向兜底：System32 与 SysWOW64 互查，避免把 32/64 位系统 DLL 误判为失效
            string alt = null;
            if (exe.IndexOf("System32", StringComparison.OrdinalIgnoreCase) >= 0)
                alt = ReplaceIgnoreCase(exe, "System32", "SysWOW64");
            else if (exe.IndexOf("SysWOW64", StringComparison.OrdinalIgnoreCase) >= 0)
                alt = ReplaceIgnoreCase(exe, "SysWOW64", "System32");
            if (alt != null && File.Exists(alt)) return false;
            return true;
        }

        private static string ReplaceIgnoreCase(string s, string oldValue, string newValue)
        {
            var idx = s.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
            return idx < 0 ? s : s.Remove(idx, oldValue.Length).Insert(idx, newValue);
        }

        /// <summary>
        /// 判断一个已注册命令是否为「空可执行路径」形式的损坏命令。
        /// 例如某个 ProgID 的 shell\open\command 默认值被写成 `"" "%1"` 或 `\`，
        /// 这类命令无法真正启动任何程序，是「建议的应用」里出现 `\` 残留的根因，
        /// 必须视为失效引用清理，否则会永远漏扫。
        /// </summary>
        private static bool IsEmptyExecutableCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            var s = command.Trim();
            if (s == "\\" || s == "\"" || s == "\"\"") return true;
            // 形如 `"" "%1"`：开头就是一对空引号，说明可执行文件路径为空
            if (s.StartsWith("\"\"", StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// 判定命令是否“明显属于系统或安全的非文件死链”。若是，则不应被当作失效引用删除。
        /// 用于右键/文件关联等清理的安全护栏：宁可漏清，不可误删。覆盖四类：
        ///   ① 资源引用形式（@shell32.dll,-123 / @%SystemRoot%\...\*.dll,-1）；
        ///   ② 指向 Windows 目录的可执行文件（rundll32 / shell32 / explorer 等系统组件实际路径）；
        ///   ③ Microsoft Store / UWP 应用（WindowsApps 目录 ACL 受限，File.Exists 不可靠）；
        ///   ④ 协议 / Shell 方案（ms-settings:、shell:、{GUID} 等）非文件系统死链；
        ///   ⑤ 仅文件名形式的已知系统可执行程序（无路径）。
        /// </summary>
        private static bool LooksLikeSystemOrSafeCommand(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            var expanded = Environment.ExpandEnvironmentVariables(s).Trim();
            if (string.IsNullOrWhiteSpace(expanded)) return false;

            // ① 资源引用形式
            if (expanded.StartsWith("@", StringComparison.Ordinal)) return true;

            // ② 指向 Windows 目录
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(win)
                && (expanded.StartsWith(win, StringComparison.OrdinalIgnoreCase)
                    || expanded.IndexOf(@"Windows\System32", StringComparison.OrdinalIgnoreCase) >= 0
                    || expanded.IndexOf(@"Windows\SysWOW64", StringComparison.OrdinalIgnoreCase) >= 0))
                return true;

            // ③ Microsoft Store / UWP 应用
            if (expanded.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            // ④ 协议 / Shell 方案（非文件系统路径）
            if ((expanded.IndexOf(':', StringComparison.Ordinal) >= 0 || expanded.StartsWith("{", StringComparison.Ordinal))
                && !expanded.StartsWith("\"", StringComparison.Ordinal)
                && !Path.IsPathRooted(expanded))
                return true;

            // ⑤ 仅文件名形式的已知系统可执行程序
            var first = expanded.Split(' ', '\t', '"')[0].Trim();
            if (_knownSystemExeNames.Contains(first)) return true;

            return false;
        }

        /// <summary>
        /// 收集文件关联中指向已卸载程序的残留项。
        /// 扫描 HKCR\Applications 下所有已注册应用，检查 shell\open\command 是否指向存在的 exe。
        /// </summary>
        private static List<(string exeName, string command)> GetInvalidFileAssociations()
        {
            var result = new List<(string exeName, string command)>();

            try
            {
                // 扫描 Applications 注册（HKCU/HKLM，含 32 位 WOW6432Node 视图）。
                // 注意：64 位进程直接读 Software\Classes\Applications 仅见原生(64 位)视图；
                // 32 位程序(如 Winamp)安装在 WOW6432Node\Classes\Applications，必须显式读取，否则漏判。
                var appPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                void CollectApps(RegistryKey baseKey, string classesRel)
                {
                    using var apps = baseKey.OpenSubKey(classesRel + @"\Applications", false);
                    if (apps == null) return;
                    foreach (var name in apps.GetSubKeyNames())
                        appPaths.Add(name);
                }

                CollectApps(Registry.LocalMachine, @"Software\Classes");
                CollectApps(Registry.LocalMachine, @"Software\WOW6432Node\Classes");
                CollectApps(Registry.CurrentUser, @"Software\Classes");
                CollectApps(Registry.CurrentUser, @"Software\WOW6432Node\Classes");

                foreach (var exeName in appPaths)
                {
                    try
                    {
                        string command = null;

                        // HKCU 覆盖 HKLM；原生与 WOW6432Node 两种视图都查
                        foreach (var baseKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
                        {
                            foreach (var classesRel in new[] { @"Software\Classes", @"Software\WOW6432Node\Classes" })
                            {
                                using var key = baseKey.OpenSubKey($@"{classesRel}\Applications\{exeName}\shell\open\command", false);
                                if (key != null)
                                {
                                    var c = key.GetValue(null) as string;
                                    if (!string.IsNullOrWhiteSpace(c))
                                        command = c; // 后查的 HKCU 覆盖 HKLM，符合注册表合并语义
                                }
                            }
                        }

                        if (string.IsNullOrWhiteSpace(command)) continue;

                        // 系统关键程序白名单，永不清理
                        if (IsSystemCriticalAssociation(exeName, command)) continue;

                        if (IsDeadFileReference(command))
                            result.Add((exeName, command));
                    }
                    catch { }
                }
            }
            catch { }

            return result;
        }

        /// <summary>
        /// 判断是否为系统关键的文件关联（永不清理）。
        /// </summary>
        private static bool IsSystemCriticalAssociation(string exeName, string command)
        {
            // 系统内置程序白名单
            var systemApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "notepad.exe",
                "notepad++.exe",
                "mspaint.exe",
                "paint.exe",
                "wordpad.exe",
                "cmd.exe",
                "powershell.exe",
                "pwsh.exe",
                "windows_media_player.exe",
                "wmplayer.exe",
                "vlc.exe",
                "mpv.exe",
                "explorer.exe",
                "iexplore.exe",
                "firefox.exe",
                "chrome.exe",
                "msedge.exe",
                "code.exe",
                "code-insiders.exe",
                "pycharm64.exe",
                "webstorm64.exe",
                "rider64.exe",
                "idea64.exe",
                "studio64.exe",
                "sublime_text.exe",
                "atom.exe",
                "winword.exe",
                "excel.exe",
                "powerpnt.exe",
                "outlook.exe",
                "onenote.exe",
                "visio.exe",
                "acad.exe",
                "devenv.exe",
                "ssms.exe",
                "photoshop.exe",
                "illustrator.exe",
                "lightroom.exe",
                "steam.exe",
                "epicgameslauncher.exe",
                "battle.net.exe",
                "discord.exe",
                "slack.exe",
                "teams.exe",
                "zoom.exe",
                "wechat.exe",
                "qq.exe",
                "tim.exe",
                "neteasecloudmusic.exe",
                "qqmusic.exe",
                "kugou.exe",
                "potplayer.exe",
                "kmplayer.exe",
                "theglow.exe",
                "neteaseu.exe",
                "aliim.exe",
                "wangwang.exe",
                "baidunetdisk.exe",
                "baiduansvc.exe",
                "bytedance.exe",
                "lixian.exe",
                "youku.exe",
                "iqiyi.exe",
                "tencentvideo.exe",
                "thunder.exe",
                "xunlei.exe",
                "Motrix.exe",
                "Motrix-beta.exe",
                "FileZilla.exe",
                "WinSCP.exe",
                "putty.exe",
                "WinRAR.exe",
                "WinZip.exe",
                "7z.exe",
                "7za.exe",
                "Bandizip.exe",
                "HaoZip.exe",
                "CiscoPacketTracer.exe"
            };

            if (systemApps.Contains(exeName)) return true;

            // 注意：旧版曾有 command.Contains("Program Files") / "System32" / "Microsoft" 等路径兜底白名单，
            // 但这会导致所有安装在 Program Files 下的第三方软件（即使 exe 已被卸载删除）也永远不被判定为失效，
            // 与 IsDeadFileReference 的 File.Exists 判定矛盾，使「无效文件关联」清理对大部分软件完全失效。
            // 显式 systemApps 集合已覆盖真正不应清理的系统关键程序，无需额外路径兜底。

            return false;
        }

        /// <summary>
        /// 估算无效文件关联的"大小"（以条目数为单位，每个关联约 1KB 注册表空间）。
        /// 覆盖五类来源，与 CleanInvalidFileAssociation 实际清理的范围完全一致：
        ///   ① Applications\*.exe\shell\open\command 中指向已删 exe 的死链（GetInvalidFileAssociations）
        ///   ② HKCU\...\FileExts 下 OpenWithProgids / OpenWithList 中的孤儿残留（CountOpenWithResiduals）
        ///   ③ HKCR\.ext 默认值 / OpenWithProgids / OpenWithList 中失效的 ProgID（CountInvalidExtensionAssociations）
        ///   ④ HKLM/HKCU\SOFTWARE\RegisteredApplications 中失效的能力键（CountInvalidRegisteredApplications）
        ///   ⑤ HKLM/HKCU\...\App Paths 中指向已删 exe 的失效条目（CountInvalidAppPaths）
        /// 旧实现只统计 ①②，导致「建议的应用 / 默认应用」里的残留（来自 ③④⑤）在扫描阶段永远为 0、
        /// 被 ApplyVisibilityFilter 隐藏，用户误以为"没有注册表问题"——但它们真实存在。
        /// </summary>
        private static long InvalidFileAssociationSize()
        {
            int count = GetInvalidFileAssociations().Count;
            count += CountOpenWithResiduals();
            count += CountInvalidExtensionAssociations();
            count += CountInvalidRegisteredApplications();
            count += CountInvalidAppPaths();
            // 每个无效关联约占用 500 字节注册表空间（估算值）
            return count * 500L;
        }

        /// <summary>
        /// 统计 HKCU 与 HKLM 下 Explorer\FileExts 中 OpenWithProgids / OpenWithList 的孤儿残留条目数。
        /// 旧实现只扫 HKCU，漏掉了机器级（HKLM）的残留（如 TencentDocs.doc / TencentDocs.md 等）。
        /// 判定与 CleanOpenWithResiduals 的删除逻辑完全一致（同样的 IsDeadProgId / IsDeadOpenWithExe）。
        /// </summary>
        private static int CountOpenWithResiduals()
        {
            int count = 0;
            foreach (var baseKey in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using var fileExtsKey = baseKey.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts", false);
                    if (fileExtsKey == null) continue;

                    foreach (var extName in fileExtsKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var extKey = fileExtsKey.OpenSubKey(extName, false);
                            if (extKey == null) continue;

                            // ① OpenWithProgids：值为 ProgID（如 "Winamp.File"）
                            using (var progidsKey = extKey.OpenSubKey("OpenWithProgids", false))
                            {
                                if (progidsKey != null)
                                {
                                    foreach (var valueName in progidsKey.GetValueNames())
                                    {
                                        try
                                        {
                                            // OpenWithProgids 的值名即 ProgID（如 "Winamp.AssocFile.MP3"），数据通常为空；
                                            // 必须以值名为 ProgID 判定，用数据会漏掉绝大多数孤儿关联（之前几乎全部漏判）。
                                            if (IsDeadProgId(valueName))
                                                count++;
                                        }
                                        catch { }
                                    }
                                }
                            }

                            // ② OpenWithList：值为 exe 名（如 "winamp.exe"），跳过 MRUList
                            using (var listKey = extKey.OpenSubKey("OpenWithList", false))
                            {
                                if (listKey != null)
                                {
                                    foreach (var valueName in listKey.GetValueNames())
                                    {
                                        if (valueName.Equals("MRUList", StringComparison.OrdinalIgnoreCase)) continue;
                                        try
                                        {
                                            var exeName = listKey.GetValue(valueName) as string;
                                            if (!string.IsNullOrWhiteSpace(exeName) && IsDeadOpenWithExe(exeName))
                                                count++;
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return count;
        }

        /// <summary>
        /// 清理无效文件关联（打开方式残留）。
        /// </summary>
        private void CleanInvalidFileAssociation()
        {
            var invalid = GetInvalidFileAssociations();
            int cleaned = 0;

            // 清理 Applications 下的无效关联（原生 + WOW6432Node 两种视图都要删，
            // 但必须在每个具体视图里重新判定命令是否失效，避免某视图存活时被一并误删）。
            foreach (var (exeName, _) in invalid)
            {
                try
                {
                    bool deletedAny = false;
                    foreach (var baseKey in new[] { Registry.CurrentUser, Registry.LocalMachine })
                    {
                        foreach (var classesRel in new[] { @"Software\Classes", @"Software\WOW6432Node\Classes" })
                        {
                            try
                            {
                                using var cmdKey = baseKey.OpenSubKey($@"{classesRel}\Applications\{exeName}\shell\open\command", false);
                                if (cmdKey == null) continue;
                                var command = cmdKey.GetValue(null) as string;
                                if (string.IsNullOrWhiteSpace(command) || !IsDeadFileReference(command)) continue;

                                using var apps = baseKey.OpenSubKey($@"{classesRel}\Applications", true);
                                if (apps == null) continue;
                                apps.DeleteSubKeyTree(exeName, false);
                                deletedAny = true;
                            }
                            catch { }
                        }
                    }

                    if (deletedAny)
                    {
                        cleaned++;
                        _logger.Info($"已清理无效文件关联: {exeName}");
                    }
                }
                catch { }
            }

            // 清理 HKCU\...\FileExts 中的 OpenWithProgids / OpenWithList 残留
            // 注意：即使 invalid.Count == 0，也要执行清理（FileExts 残留独立存在）
            CleanOpenWithResiduals();

            // 清理 HKCR\.ext 默认值 / OpenWithProgids / OpenWithList 中的失效 ProgID，
            // HKLM/HKCU\SOFTWARE\RegisteredApplications 中的失效能力键，
            // 以及 HKLM/HKCU\...\App Paths 中指向已删 exe 的失效条目。
            // 这几处正是 Windows「建议的应用 / 默认应用」对话框的主要数据源，
            // 旧实现完全未覆盖，导致 .flac/.mkv 等后缀的失效关联始终扫不到、清不掉。
            CleanInvalidExtensionAssociations();
            CleanInvalidRegisteredApplications();
            CleanInvalidAppPaths();

            // 通知 Windows 刷新文件关联缓存
            try
            {
                NativeMethods.SHChangeNotify(
                    NativeMethods.SHCNE_ASSOCCHANGED,
                    NativeMethods.SHCNF_IDLIST,
                    IntPtr.Zero,
                    IntPtr.Zero);
            }
            catch { }

            _logger.Info($"文件关联清理完成：Applications 清理 {cleaned} 个，扩展名/注册应用/App Paths/FileExts 清理见日志");
        }

        /// <summary>
        /// 清理 HKCU 与 HKLM 下 Explorer\FileExts 的 OpenWithProgids / OpenWithList 残留项。
        /// 旧实现只清 HKCU，漏掉机器级（HKLM）残留。
        ///
        /// OpenWithProgids：值为 ProgID（如 "Winamp.File"），需解析为 shell\open\command 再判定 exe 是否存在。
        /// OpenWithList：值为 exe 文件名（如 "winamp.exe"），需通过 Applications 注册表查找完整路径再判定。
        /// </summary>
        private void CleanOpenWithResiduals()
        {
            foreach (var baseKey in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using var fileExtsKey = baseKey.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts", true);
                    if (fileExtsKey == null) continue;

                    int progidsCleaned = 0;
                    int listCleaned = 0;

                    foreach (var extName in fileExtsKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var extKey = fileExtsKey.OpenSubKey(extName, true);
                            if (extKey == null) continue;

                            // ── ① OpenWithProgids：值是 ProgID ──
                            using (var progidsKey = extKey.OpenSubKey("OpenWithProgids", true))
                            {
                                if (progidsKey != null)
                                {
                                    var deadValues = new List<string>();
                                    foreach (var valueName in progidsKey.GetValueNames())
                                    {
                                        try
                                        {
                                            // 值名即 ProgID；数据通常为空，必须用值名判定
                                            if (IsDeadProgId(valueName))
                                                deadValues.Add(valueName);
                                        }
                                        catch { }
                                    }

                                    foreach (var vn in deadValues)
                                    {
                                        try { progidsKey.DeleteValue(vn, false); } catch { }
                                        progidsCleaned++;
                                    }

                                    // 默认值若指向死 ProgID 也清空
                                    try
                                    {
                                        var defVal = progidsKey.GetValue(null) as string;
                                        if (!string.IsNullOrWhiteSpace(defVal) && IsDeadProgId(defVal))
                                            progidsKey.SetValue(null, "", RegistryValueKind.String);
                                    }
                                    catch { }
                                }
                            }

                            // ── ② OpenWithList：值是 exe 文件名 ──
                            using (var listKey = extKey.OpenSubKey("OpenWithList", true))
                            {
                                if (listKey != null)
                                {
                                    var deadValues = new List<string>();
                                    foreach (var valueName in listKey.GetValueNames())
                                    {
                                        // 跳过 MRUList 等非 exe 条目
                                        if (valueName.Equals("MRUList", StringComparison.OrdinalIgnoreCase)) continue;
                                        try
                                        {
                                            var exeName = listKey.GetValue(valueName) as string;
                                            if (!string.IsNullOrWhiteSpace(exeName) && IsDeadOpenWithExe(exeName))
                                                deadValues.Add(valueName);
                                        }
                                        catch { }
                                    }

                                    foreach (var vn in deadValues)
                                    {
                                        try { listKey.DeleteValue(vn, false); } catch { }
                                        listCleaned++;
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    if (progidsCleaned > 0 || listCleaned > 0)
                    {
                        _logger?.Info($"已清理 FileExts 残留: OpenWithProgids={progidsCleaned} 条, OpenWithList={listCleaned} 条");
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// 判断一个 ProgID 是否指向不存在的程序（用于 OpenWithProgids 清理）。
        /// 通过 HKCR\<ProgID>\shell\open\command 取出命令行，再用 IsDeadFileReference 判定。
        /// </summary>
        private static bool IsDeadProgId(string progId)
        {
            if (string.IsNullOrWhiteSpace(progId)) return false;

            // 依次查 HKCU、HKLM、HKCR 的 Classes 根（含 32 位 WOW6432Node 视图）。
            // 不再遇到第一个“存在但无命令”的键就短路判存活——否则会漏掉 HKCR/另一视图里
            // 真实存在的死命令（例如某 ProgID 在 HKCU 只有空壳、在 HKCR 的 open command
            // 却指向已删除 exe 的情况）。现在只有确认任一视图存在【仍存活】的命令才判存活。
            var bases = new[] { Registry.CurrentUser, Registry.LocalMachine, Registry.ClassesRoot };
            var classPaths = new[] { @"Software\Classes", @"Software\WOW6432Node\Classes", "" };

            bool existsAnywhere = false;
            bool anyAliveCommand = false;
            bool foundDeadCommand = false;
            foreach (var baseKey in bases)
            {
                foreach (var cp in classPaths)
                {
                    try
                    {
                        string keyPath = string.IsNullOrEmpty(cp) ? progId : $@"{cp}\{progId}";
                        using var progKey = baseKey.OpenSubKey(keyPath, false);
                        if (progKey == null) continue;

                        existsAnywhere = true;

                        using var cmdKey = progKey.OpenSubKey(@"shell\open\command", false);
                        if (cmdKey != null)
                        {
                            var command = cmdKey.GetValue(null) as string;
                            if (!string.IsNullOrWhiteSpace(command))
                            {
                                // 指向已删除 exe，或空可执行路径（如 `"" "%1"` / `\`）都视为失效，
                                // 但必须等所有视图都查完再下结论：任一视图存在存活命令就应判存活。
                                if (IsDeadFileReference(command) || IsEmptyExecutableCommand(command))
                                    foundDeadCommand = true;
                                else
                                    anyAliveCommand = true;
                            }
                        }
                    }
                    catch { }
                }
            }

            // 只有确认没有任何存活命令、且确实发现失效命令时才判失效；
            // 避免 HKCU 有存活命令、HKLM 另一视图有死命令时被误删。
            if (anyAliveCommand) return false;
            if (foundDeadCommand) return true;

            // 键存在但没有可判定的命令（如 AppX 的 DelegateExecute）-> 保守视为存活，避免误删现代应用关联。
            // 键在所有视图都不存在 -> 孤儿引用，应清理。
            return !existsAnywhere;
        }

        /// <summary>
        /// 判断 OpenWithList 中的 exe 名是否对应已不存在的程序（用于清理「建议的应用/打开方式」残留）。
        /// 判定规则（与 Windows「打开方式」能否真正启动该程序一致）：
        ///   1) 在 Applications 与 App Paths 下查找该 exe 的注册命令；只要任一注册指向的 exe 仍存在 -> 视为存活（保留）。
        ///   2) 若找到命令但指向的 exe 已不存在 -> 失效（清理）。
        ///   3) 若完全没有任何 Applications / App Paths 注册 -> 该 exe 不再是已注册的处理程序，
        ///      OpenWithList 中的 MRU 条目属于孤儿残留（无法再启动该程序），判定为失效清理。
        /// 注：绿色/便携版程序（如装在 D:\绿色 下）往往不写 Applications 注册，
        /// 旧实现因查不到注册而误判存活，导致「建议的应用」里残留无法清理——此处按规则 3 清理。
        /// </summary>
        private static bool IsDeadOpenWithExe(string exeName)
        {
            if (string.IsNullOrWhiteSpace(exeName)) return false;

            var bases = new[] { Registry.LocalMachine, Registry.CurrentUser };
            var classRels = new[] { @"Software\Classes", @"Software\WOW6432Node\Classes" };

            foreach (var baseKey in bases)
            {
                foreach (var cr in classRels)
                {
                    // ① Applications\<exe>\shell\open\command
                    try
                    {
                        using var appKey = baseKey.OpenSubKey($@"{cr}\Applications\{exeName}\shell\open\command", false);
                        if (appKey != null)
                        {
                            var command = appKey.GetValue(null) as string;
                            if (!string.IsNullOrWhiteSpace(command))
                            {
                                if (!IsDeadFileReference(command))
                                    return false; // 任一注册指向的 exe 仍存在 -> 存活
                            }
                        }
                    }
                    catch { }
                }
            }

            // ② App Paths\<exe>（原生 + WOW6432Node 视图；默认值为 exe 完整路径，可能带 ",FriendlyName" 后缀）
            foreach (var baseKey in bases)
            {
                foreach (var appPathsRel in new[]
                {
                    @"Software\Microsoft\Windows\CurrentVersion\App Paths",
                    @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths"
                })
                {
                    try
                    {
                        using var appPathKey = baseKey.OpenSubKey($@"{appPathsRel}\{exeName}", false);
                        if (appPathKey != null)
                        {
                            var p = appPathKey.GetValue(null) as string;
                            if (!string.IsNullOrWhiteSpace(p))
                            {
                                var pathOnly = p.Split(',')[0].Trim();
                                if (!IsDeadFileReference(pathOnly))
                                    return false; // 存活
                            }
                        }
                    }
                    catch { }
                }
            }

            // 规则 3：无任何注册 -> 孤儿残留，清理
            return true;
        }

        /// <summary>
        /// 扩展名键扫描与清理：Windows「建议的应用 / 默认应用」对话框的另一主要数据源。
        /// 旧实现只扫 HKCR\Applications，完全漏掉：
        ///   ① HKCR\.ext 默认值（如 .flac 默认指向 Winamp.File.FLAC）；
        ///   ② HKCR\.ext\OpenWithProgids 中已失效的 ProgID（如 io.mpv.flac / io.umpv.flac）。
        /// 这些正是「打开方式」里残留程序的来源，必须纳入扫描与清理，否则永远扫不到。
        /// </summary>
        private static readonly string[] _classesRels = new[]
        {
            @"Software\Classes",
            @"Software\WOW6432Node\Classes"
        };

        /// <summary>统计扩展名键默认值 / OpenWithProgids / OpenWithList 中失效引用的条数。</summary>
        private static int CountInvalidExtensionAssociations()
        {
            int count = 0;
            foreach (var baseKey in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                foreach (var classesRel in _classesRels)
                {
                    using var classes = baseKey.OpenSubKey(classesRel, false);
                    if (classes == null) continue;
                    foreach (var extName in classes.GetSubKeyNames())
                    {
                        if (!extName.StartsWith(".", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            using var extKey = classes.OpenSubKey(extName, false);
                            if (extKey == null) continue;

                            var def = extKey.GetValue(null) as string;
                            if (!string.IsNullOrWhiteSpace(def) && IsDeadProgId(def))
                                count++;

                            using (var progids = extKey.OpenSubKey("OpenWithProgids", false))
                            {
                                if (progids != null)
                                    foreach (var vn in progids.GetValueNames())
                                        if (!string.IsNullOrWhiteSpace(vn) && IsDeadProgId(vn))
                                            count++;
                            }

                            // 类级 OpenWithList：值数据为 exe 文件名（如 winamp.exe）
                            using (var listKey = extKey.OpenSubKey("OpenWithList", false))
                            {
                                if (listKey != null)
                                    foreach (var vn in listKey.GetValueNames())
                                    {
                                        if (vn.Equals("MRUList", StringComparison.OrdinalIgnoreCase)) continue;
                                        var exeName = listKey.GetValue(vn) as string;
                                        if (!string.IsNullOrWhiteSpace(exeName) && IsDeadOpenWithExe(exeName))
                                            count++;
                                    }
                            }
                        }
                        catch { }
                    }
                }
            }
            return count;
        }

        /// <summary>清理扩展名键默认值 / OpenWithProgids / OpenWithList 中失效的引用。</summary>
        private void CleanInvalidExtensionAssociations()
        {
            int cleaned = 0;
            foreach (var baseKey in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                foreach (var classesRel in _classesRels)
                {
                    using var classes = baseKey.OpenSubKey(classesRel, true);
                    if (classes == null) continue;
                    foreach (var extName in classes.GetSubKeyNames())
                    {
                        if (!extName.StartsWith(".", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            using var extKey = classes.OpenSubKey(extName, true);
                            if (extKey == null) continue;

                            var def = extKey.GetValue(null) as string;
                            if (!string.IsNullOrWhiteSpace(def) && IsDeadProgId(def))
                            {
                                try { extKey.SetValue(null, "", RegistryValueKind.String); cleaned++; } catch { }
                            }

                            using (var progids = extKey.OpenSubKey("OpenWithProgids", true))
                            {
                                if (progids != null)
                                    foreach (var vn in progids.GetValueNames().ToList())
                                    {
                                        if (string.IsNullOrWhiteSpace(vn)) continue;
                                        if (IsDeadProgId(vn))
                                        {
                                            try { progids.DeleteValue(vn, false); cleaned++; } catch { }
                                        }
                                    }
                            }

                            using (var listKey = extKey.OpenSubKey("OpenWithList", true))
                            {
                                if (listKey != null)
                                    foreach (var vn in listKey.GetValueNames().ToList())
                                    {
                                        if (vn.Equals("MRUList", StringComparison.OrdinalIgnoreCase)) continue;
                                        var exeName = listKey.GetValue(vn) as string;
                                        if (!string.IsNullOrWhiteSpace(exeName) && IsDeadOpenWithExe(exeName))
                                        {
                                            try { listKey.DeleteValue(vn, false); cleaned++; } catch { }
                                        }
                                    }
                            }
                        }
                        catch { }
                    }
                }
            }

            if (cleaned > 0)
                _logger?.Info($"已清理扩展名关联残留: {cleaned} 条");
        }

        /// <summary>
        /// 注册应用能力键扫描与清理：HKLM/HKCU\SOFTWARE\RegisteredApplications 中
        /// 指向已卸载（exe 不存在）程序的条目。这些条目是 Windows「建议的应用」列表的
        /// 主要来源（如 AIMP、foobar2000、mpv 等），旧实现完全未覆盖。
        /// </summary>
        private static int CountInvalidRegisteredApplications()
        {
            int count = 0;
            foreach (var baseKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var regApps = baseKey.OpenSubKey(@"SOFTWARE\RegisteredApplications", false);
                if (regApps == null) continue;
                foreach (var name in regApps.GetValueNames())
                {
                    var capRel = regApps.GetValue(name) as string;
                    if (string.IsNullOrWhiteSpace(capRel)) continue;
                    try { if (IsDeadCapabilityApp(baseKey, capRel)) count++; } catch { }
                }
            }
            return count;
        }

        /// <summary>清理 HKLM/HKCU\SOFTWARE\RegisteredApplications 中的失效条目。</summary>
        private void CleanInvalidRegisteredApplications()
        {
            int cleaned = 0;
            foreach (var baseKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var regApps = baseKey.OpenSubKey(@"SOFTWARE\RegisteredApplications", true);
                if (regApps == null) continue;
                foreach (var name in regApps.GetValueNames().ToList())
                {
                    var capRel = regApps.GetValue(name) as string;
                    if (string.IsNullOrWhiteSpace(capRel)) continue;
                    try
                    {
                        if (IsDeadCapabilityApp(baseKey, capRel))
                        {
                            regApps.DeleteValue(name, false);
                            cleaned++;
                            _logger?.Info($"已清理失效注册应用: {name} ({capRel})");
                        }
                    }
                    catch { }
                }
            }

            if (cleaned > 0)
                _logger?.Info($"已清理失效注册应用: {cleaned} 个");
        }

        /// <summary>
        /// 统计 HKLM/HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths 中
        /// 指向已删除 exe 的失效条目。这类条目正是「建议的应用」里直接显示完整路径
        /// （如 D:\绿色\foobar2000\foobar2000.exe）的来源，旧实现完全未覆盖。
        /// </summary>
        private static int CountInvalidAppPaths()
        {
            int count = 0;
            foreach (var baseKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var appPaths = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", false);
                if (appPaths == null) continue;
                foreach (var exeName in appPaths.GetSubKeyNames())
                {
                    try
                    {
                        using var k = appPaths.OpenSubKey(exeName, false);
                        if (k == null) continue;
                        var path = k.GetValue(null) as string;
                        if (!string.IsNullOrWhiteSpace(path) && IsDeadFileReference(path))
                            count++;
                    }
                    catch { }
                }
            }
            return count;
        }

        /// <summary>清理 App Paths 中指向已删除 exe 的失效条目。</summary>
        private void CleanInvalidAppPaths()
        {
            int cleaned = 0;
            foreach (var baseKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var appPaths = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", true);
                if (appPaths == null) continue;
                foreach (var exeName in appPaths.GetSubKeyNames().ToList())
                {
                    try
                    {
                        using var k = appPaths.OpenSubKey(exeName, false);
                        if (k == null) continue;
                        var path = k.GetValue(null) as string;
                        if (!string.IsNullOrWhiteSpace(path) && IsDeadFileReference(path))
                        {
                            appPaths.DeleteSubKeyTree(exeName, false);
                            cleaned++;
                            _logger?.Info($"已清理失效 App Paths: {exeName} ({path})");
                        }
                    }
                    catch { }
                }
            }

            if (cleaned > 0)
                _logger?.Info($"已清理失效 App Paths: {cleaned} 个");
        }

        /// <summary>
        /// 统计 SharedDLLs 中指向已删除 DLL 的失效引用计数。
        /// 位置：HKLM/HKCU 的 SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs
        /// （含 WOW6432Node 32 位视图）。值名即 DLL 完整路径。
        /// </summary>
        private static int CountInvalidSharedDlls()
        {
            int count = 0;
            foreach (var baseKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var rel in new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs"
                })
                {
                    using var key = baseKey.OpenSubKey(rel, false);
                    if (key == null) continue;
                    foreach (var name in key.GetValueNames())
                    {
                        try { if (IsDeadFileReference(name)) count++; } catch { }
                    }
                }
            }
            return count;
        }

        private static long InvalidSharedDllsSize() => CountInvalidSharedDlls() * 200L;

        private void CleanInvalidSharedDlls()
        {
            int cleaned = 0;
            foreach (var baseKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var rel in new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs"
                })
                {
                    using var key = baseKey.OpenSubKey(rel, true);
                    if (key == null) continue;
                    foreach (var name in key.GetValueNames().ToList())
                    {
                        try
                        {
                            if (IsDeadFileReference(name))
                            {
                                key.DeleteValue(name, false);
                                cleaned++;
                            }
                        }
                        catch { }
                    }
                }
            }

            if (cleaned > 0)
                _logger?.Info($"已清理失效 SharedDLLs: {cleaned} 条");
        }

        /// <summary>从 MuiCache 值名中提取文件路径（去掉 .FriendlyAppName / .ApplicationCompany 后缀）。</summary>
        private static string ExtractMuiCachePath(string valueName)
        {
            if (string.IsNullOrWhiteSpace(valueName)) return null;
            int idx = valueName.IndexOf(".FriendlyAppName", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return valueName.Substring(0, idx);
            idx = valueName.IndexOf(".ApplicationCompany", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return valueName.Substring(0, idx);
            return null;
        }

        /// <summary>统计 MuiCache 中指向已删除 exe 的程序名/厂商名缓存条数。</summary>
        private static int CountInvalidMuiCache()
        {
            int count = 0;
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", false);
            if (key == null) return 0;
            foreach (var name in key.GetValueNames())
            {
                var path = ExtractMuiCachePath(name);
                if (string.IsNullOrWhiteSpace(path)) continue;
                try { if (!File.Exists(path)) count++; } catch { }
            }
            return count;
        }

        private static long InvalidMuiCacheSize() => CountInvalidMuiCache() * 100L;

        private void CleanInvalidMuiCache()
        {
            int cleaned = 0;
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", true);
            if (key == null) return;
            foreach (var name in key.GetValueNames().ToList())
            {
                var path = ExtractMuiCachePath(name);
                if (string.IsNullOrWhiteSpace(path)) continue;
                try
                {
                    if (!File.Exists(path))
                    {
                        key.DeleteValue(name, false);
                        cleaned++;
                    }
                }
                catch { }
            }

            if (cleaned > 0)
                _logger?.Info($"已清理失效 MUI 缓存: {cleaned} 条");
        }

        /// <summary>
        /// 统计 AppCompatFlags\Layers 中为已删除 exe 保留的兼容性设置条数。
        /// 位置：HKLM/HKCU 的 SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers
        /// （含 WOW6432Node 32 位视图）。值名即 exe 完整路径。
        /// </summary>
        private static int CountInvalidAppCompatLayers()
        {
            int count = 0;
            foreach (var baseKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var rel in new[]
                {
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"
                })
                {
                    using var key = baseKey.OpenSubKey(rel, false);
                    if (key == null) continue;
                    foreach (var name in key.GetValueNames())
                    {
                        try { if (IsDeadFileReference(name)) count++; } catch { }
                    }
                }
            }
            return count;
        }

        private static long InvalidAppCompatLayersSize() => CountInvalidAppCompatLayers() * 100L;

        private void CleanInvalidAppCompatLayers()
        {
            int cleaned = 0;
            foreach (var baseKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var rel in new[]
                {
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"
                })
                {
                    using var key = baseKey.OpenSubKey(rel, true);
                    if (key == null) continue;
                    foreach (var name in key.GetValueNames().ToList())
                    {
                        try
                        {
                            if (IsDeadFileReference(name))
                            {
                                key.DeleteValue(name, false);
                                cleaned++;
                            }
                        }
                        catch { }
                    }
                }
            }

            if (cleaned > 0)
                _logger?.Info($"已清理失效兼容性设置: {cleaned} 条");
        }

        /// <summary>
        /// 判断 RegisteredApplications 指向的能力键是否已失效。
        /// 判定优先级：
        ///   ① ApplicationIcon 指向的 exe 不存在 -> 失效（绿色/便携程序卸载后最常见）；
        ///   ② 无 ApplicationIcon 时，检查 FileAssociations/URLAssociations 的 ProgID，
        ///      任一仍存活 -> 应用存活；全部失效 -> 应用失效；没有任何关联 -> 保守存活。
        /// </summary>
        private static bool IsDeadCapabilityApp(RegistryKey baseKey, string capRel)
        {
            using var cap = baseKey.OpenSubKey(capRel, false);
            // 能力键缺失时无法进一步确认 exe 是否仍在（如 MusicBee 只注册了 ProgID 而没有
            // 完整的 Capabilities 键），保守视为存活，避免误删仍可正常使用的默认应用。
            if (cap == null) return false;

            var icon = cap.GetValue("ApplicationIcon") as string;
            if (!string.IsNullOrWhiteSpace(icon))
            {
                var exe = icon.Split(',')[0].Trim();
                // @ 开头为间接字符串（如 @shell32.dll,-123），属于系统资源，保守视为存活
                if (exe.StartsWith("@", StringComparison.Ordinal)) return false;
                exe = Environment.ExpandEnvironmentVariables(exe);
                // 非根路径无法可靠判定文件是否存在，保守视为存活，避免误删默认应用
                if (!Path.IsPathRooted(exe)) return false;
                if (!IsDeadFileReference(exe)) return false; // 图标对应 exe 仍存在 -> 应用已安装
                return true; // exe 已删除 -> 失效
            }

            bool anyAssociation = false;
            foreach (var sub in new[] { "FileAssociations", "URLAssociations" })
            {
                using var assoc = cap.OpenSubKey(sub, false);
                if (assoc == null) continue;
                foreach (var vn in assoc.GetValueNames())
                {
                    var progId = assoc.GetValue(vn) as string;
                    if (string.IsNullOrWhiteSpace(progId)) continue;
                    anyAssociation = true;
                    if (!IsDeadProgId(progId)) return false; // 有存活关联 -> 应用存活
                }
            }

            return anyAssociation; // 有关联但全部失效 -> 失效；无关联 -> 保守存活
        }

        /// <summary>
        /// 从“未加引号”的命令行中解析出真正的可执行文件路径。
        /// 路径可能含空格，因此从前向后累加空格分隔的片段，
        /// 第一个在文件系统上真实存在的文件即为可执行文件。
        /// </summary>
        private static string ResolveExecutableFromUnquoted(string s)
        {
            var parts = s.Split(' ');
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(parts[i]);
                var candidate = sb.ToString();
                var expanded = Environment.ExpandEnvironmentVariables(candidate);
                if (NativeMethods.FileExistsResilient(expanded)) return expanded;
                if (NativeMethods.FileExistsResilient(expanded + ".exe")) return expanded + ".exe";
            }
            // 未匹配到真实存在的可执行文件：返回原始字符串（含参数），
            // 交由上层 IsDeadFileReference 判定（路径不存在→失效；非根路径的系统命令→保留）。
            // 注意：绝不能退回“第一个空格之前”的截断逻辑——那会把
            // "C:\Program Files\X\Y.exe" 误断为 "C:\Program" 而误判为失效（即此前修复的 bug）。
            return s;
        }

        private static readonly string[] _startupKeys = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce"
        };

        /// <summary>
        /// “启动”文件夹（用户级 + 公共级）。其中的 .lnk 快捷方式若指向已删除的程序，即为无效启动项。
        /// </summary>
        private static readonly string[] _startupFolders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
        };

        /// <summary>任务计划程序删除前的 XML 备份目录。</summary>
        private static readonly string _taskBackupDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskCleaner", "TaskBackups");

        /// <summary>仅文件名形式的已知系统可执行程序：无路径时一律视为安全，不参与失效判定。</summary>
        private static readonly HashSet<string> _knownSystemExeNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "rundll32.exe", "rundll.exe", "regsvr32.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
            "explorer.exe", "mshta.exe", "wscript.exe", "cscript.exe", "dllhost.exe", "notepad.exe",
            "rundll32", "regsvr32", "cmd", "powershell"
        };

        private static readonly string[] _clsidRoots = new[]
        {
            @"SOFTWARE\Classes\CLSID",
            @"SOFTWARE\WOW6432Node\Classes\CLSID"
        };

        private static readonly string[] _uninstallRoots = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        /// <summary>
        /// 枚举所有需要扫描"无效右键菜单"的类键相对路径（相对 Classes 根）。
        /// 覆盖三层，确保不遗漏任何程序为特定文件类型注册的右键菜单：
        /// ① 通用类键（ContextMenuMapping.InvalidContextMenuGenericClasses）；
        /// ② 所有扩展名键（.docx、.txt 等）；
        /// ③ 扩展名键下的 ProgID 子键（嵌套向下一层）。
        /// 结构性常量全部从 ContextMenuMapping 共享，避免重复定义。
        /// </summary>
        private static IEnumerable<string> EnumerateContextMenuClassKeys(RegistryKey classesKey)
        {
            // ① 通用类键（ContextMenuMapping 统一配置）
            foreach (var cls in ContextMenuMapping.InvalidContextMenuGenericClasses)
                yield return cls;

            // ②③ 扩展名键及其 ProgID 子键
            string[] extNames;
            try { extNames = classesKey.GetSubKeyNames(); }
            catch { yield break; }

            foreach (var name in extNames)
            {
                if (!name.StartsWith(".", StringComparison.OrdinalIgnoreCase)) continue;
                yield return name;

                // 嵌套向下：扩展名键的 ProgID 子键
                // 注意：yield 不能在带 catch 的 try 块内使用，故先收集到列表再 yield
                var progIdPaths = new List<string>();
                try
                {
                    using var extKey = classesKey.OpenSubKey(name, false);
                    if (extKey != null)
                    {
                        foreach (var subName in extKey.GetSubKeyNames())
                        {
                            if (ContextMenuMapping.NonProgIdSubKeysOfExt.Contains(subName)) continue;
                            progIdPaths.Add($@"{name}\{subName}");
                        }
                    }
                }
                catch { }

                foreach (var p in progIdPaths)
                    yield return p;
            }

            // ④ 顶层 ProgID 键（如 WinRAR、foobar2000.MKV 等未直接挂在扩展名下的 ProgID）
            var topLevelProgIds = new List<string>();
            try
            {
                foreach (var name in extNames)
                {
                    if (name.StartsWith(".", StringComparison.OrdinalIgnoreCase)) continue;
                    if (ContextMenuMapping.InvalidContextMenuGenericClasses.Contains(name)) continue;
                    if (name.StartsWith("{", StringComparison.Ordinal)) continue; // CLSID 由 shellex 扫描处理
                    if (name.IndexOf('\\') >= 0) continue;
                    using var shellKey = classesKey.OpenSubKey(name + @"\shell", false);
                    if (shellKey != null) topLevelProgIds.Add(name);
                }
            }
            catch { }

            foreach (var p in topLevelProgIds)
                yield return p;
        }

        /// <summary>
        /// 清理“无效启动项”：Run/RunOnce 中指向已卸载程序（exe 已不存在）的残留启动值。
        /// 仅删除失效的值，不影响任何仍有效的启动项。HKCU + HKLM。
        /// </summary>
        private void CleanInvalidStartup()
        {
            CleanInvalidStartupIn(Registry.CurrentUser);
            CleanInvalidStartupIn(Registry.LocalMachine);
            CleanInvalidStartupFolder();
            CleanInvalidScheduledTask();
        }

        private void CleanInvalidStartupIn(RegistryKey baseKey)
        {
            foreach (var rel in _startupKeys)
            {
                try
                {
                    using var key = baseKey.OpenSubKey(rel, true);
                    if (key == null) continue;
                    foreach (var name in key.GetValueNames().ToList())
                    {
                        try
                        {
                            var val = key.GetValue(name) as string;
                            if (IsDeadFileReference(val))
                            {
                                key.DeleteValue(name, false);
                                _logger?.Info($"已清理无效启动项: {rel}\\{name}");
                            }
                        }
                        catch (Exception ex) { _logger?.Error($"清理启动项值失败: {name}", ex); }
                    }
                }
                catch (Exception ex) { _logger?.Error($"打开启动项键失败: {rel}", ex); }
            }
        }

        /// <summary>
        /// 清理“启动”文件夹中指向已卸载程序的失效 .lnk 快捷方式。
        /// 仅当快捷方式的目标文件 / 目录确实不存在时才删除该 .lnk（移入回收站，可恢复），
        /// 不影响任何仍有效的启动快捷方式。
        /// </summary>
        private void CleanInvalidStartupFolder()
        {
            foreach (var folder in _startupFolders)
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
                try
                {
                    foreach (var lnk in Directory.GetFiles(folder, "*.lnk"))
                    {
                        try
                        {
                            var target = GetShortcutTarget(lnk);
                            // 目标为空（如 UWP/商店应用快捷方式 TargetPath 为空、靠 AppID 启动）属于
                            // “无法确定”，绝不能判定为失效而删除，否则会误删合法开机启动项。
                            // 仅当目标非空且文件/目录确实不存在时才判为失效。
                            bool dead = !string.IsNullOrWhiteSpace(target)
                                        && !File.Exists(target)
                                        && !Directory.Exists(target);
                            if (dead)
                            {
                                NativeMethods.SendToRecycleBin(lnk);
                                _logger?.Info($"已清理启动文件夹失效项(回收站): {lnk} -> {target}");
                            }
                        }
                        catch (Exception ex) { _logger?.Error($"清理启动文件夹项失败: {lnk}", ex); }
                    }
                }
                catch (Exception ex) { _logger?.Error($"枚举启动文件夹失败: {folder}", ex); }
            }
        }

        /// <summary>
        /// 解析 .lnk 快捷方式的目标路径（通过 Shell 原生 IShellLinkW COM，非 WScript.Shell）。失败返回 null。
        /// 说明：原实现调用 WScript.Shell（Windows Script Host）解析快捷方式，会被杀软启发式判定为
        /// "MSIL.Wsh" 木马（HEUR:Trojan/MSIL.Wsh）。改为 Shell 自身的 IShellLinkW + IPersistFile 接口，
        /// 见 Interop/ShortcutHelper.cs。行为等价（返回原始存储路径，不解析/不展开环境变量）。
        /// </summary>
        private static string GetShortcutTarget(string lnkPath)
            => ShortcutHelper.ResolveShortcutTarget(lnkPath);

        /// <summary>
        /// 清理任务计划程序中动作命令指向已卸载程序（exe 已不存在）的失效计划任务。
        /// 删除前将任务 XML 备份到 _taskBackupDir，便于手动恢复。仅删除动作路径确实失效的任务。
        /// </summary>
        private void CleanInvalidScheduledTask()
        {
            var dead = GetDeadScheduledTasks();
            if (dead.Count == 0) return;
            try { Directory.CreateDirectory(_taskBackupDir); } catch { }

            foreach (var (folderPath, name, xml) in dead)
            {
                try
                {
                    // 备份 XML
                    if (!string.IsNullOrWhiteSpace(xml))
                    {
                        var safe = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
                        var bak = Path.Combine(_taskBackupDir, $"{safe}.xml");
                        File.WriteAllText(bak, xml);
                    }
                    var svcType = Type.GetTypeFromProgID("Schedule.Service");
                    if (svcType == null) continue;
                    using dynamic svc = Activator.CreateInstance(svcType);
                    svc.Connect();
                    dynamic folder = svc.GetFolder(folderPath);
                    folder.DeleteTask(name, 0);
                    _logger?.Info($"已清理失效计划任务: {folderPath}\\{name} (备份: {_taskBackupDir})");
                }
                catch (Exception ex) { _logger?.Error($"清理计划任务失败: {folderPath}\\{name}", ex); }
            }
        }

        /// <summary>
        /// 枚举任务计划程序内所有任务，返回动作命令指向已卸载程序（exe 已不存在）的失效任务
        /// （含所在文件夹路径、任务名、XML 定义）。
        /// </summary>
        private static List<(string FolderPath, string Name, string Xml)> GetDeadScheduledTasks()
        {
            var result = new List<(string, string, string)>();
            try
            {
                var svcType = Type.GetTypeFromProgID("Schedule.Service");
                if (svcType == null) return result;
                using dynamic svc = Activator.CreateInstance(svcType);
                svc.Connect();
                dynamic root = svc.GetFolder("\\");
                CollectDeadTasks(root, result);
            }
            catch (Exception)
            {
                // 任务计划程序不可用（如服务被禁用）时静默跳过，不影响其余清理。
            }
            return result;
        }

        private static void CollectDeadTasks(dynamic folder, List<(string, string, string)> result)
        {
            try
            {
                var tasks = folder.GetTasks(1); // 1 = 含隐藏任务
                foreach (dynamic task in tasks)
                {
                    try
                    {
                        string folderPath = task.Folder?.Path as string ?? "\\";
                        string name = task.Name as string;
                        string xml = task.Xml as string;
                        string exe = null;
                        try
                        {
                            var actions = task.Definition.Actions;
                            foreach (dynamic action in actions)
                            {
                                // ActionType: 0 = Exec
                                if ((int)action.Type == 0)
                                {
                                    exe = action.Path as string;
                                    break;
                                }
                            }
                        }
                        catch { }
                        if (!string.IsNullOrWhiteSpace(name) && IsDeadFileReference(exe))
                            result.Add((folderPath, name, xml));
                    }
                    catch { }
                }
            }
            catch { }
            try
            {
                var folders = folder.GetFolders(0);
                foreach (dynamic sub in folders)
                    CollectDeadTasks(sub, result);
            }
            catch { }
        }

        /// <summary>
        /// 清理“失效 COM/ActiveX 组件”：CLSID 中服务器文件（DLL/EXE）已被删除的失效组件。
        /// 仅当服务器文件经校验确实不存在时才删除该 CLSID 子键，不触碰仍在使用的组件。
        /// </summary>
        private void CleanInvalidCom()
        {
            CleanInvalidComIn(Registry.CurrentUser);
            CleanInvalidComIn(Registry.LocalMachine);
        }

        private void CleanInvalidComIn(RegistryKey baseKey)
        {
            foreach (var root in _clsidRoots)
            {
                try
                {
                    using var clsidKey = baseKey.OpenSubKey(root, true);
                    if (clsidKey == null) continue;
                    foreach (var guid in clsidKey.GetSubKeyNames().ToList())
                    {
                        try
                        {
                            using var sub = clsidKey.OpenSubKey(guid, false);
                            if (sub == null) continue;

                            // 只有当【所有存在的服务器值】都指向已删除文件时才删除该 CLSID；
                            // 只要还有一个服务器值（如 InprocServer32 或 LocalServer32）仍存在，就应保留，
                            // 避免误删仍有可用架构（32/64 位）的组件。
                            bool anyServer = false;
                            bool allDead = true;
                            foreach (var v in new[] { "InprocServer32", "InprocServer64", "LocalServer32", "LocalServer64" })
                            {
                                var path = sub.GetValue(v) as string;
                                if (string.IsNullOrWhiteSpace(path)) continue;
                                anyServer = true;
                                if (!IsDeadFileReference(path)) { allDead = false; break; }
                            }

                            if (anyServer && allDead)
                            {
                                clsidKey.DeleteSubKeyTree(guid, false);
                                _logger?.Info($"已清理失效 COM/ActiveX 组件: {root}\\{guid}");
                            }
                        }
                        catch (Exception ex) { _logger?.Error($"清理 COM 项失败: {guid}", ex); }
                    }
                }
                catch (Exception ex) { _logger?.Error($"打开 CLSID 根失败: {root}", ex); }
            }
        }

        /// <summary>
        /// 清理“孤立卸载残留”：卸载信息中卸载程序已丢失的残留项（软件已删但注册表残留）。
        /// 仅当 UninstallString 指向的文件确实不存在时才删除该卸载子键，不影响可正常卸载的软件。
        /// </summary>
        private void CleanInvalidUninstall()
        {
            CleanInvalidUninstallIn(Registry.CurrentUser);
            CleanInvalidUninstallIn(Registry.LocalMachine);
        }

        private void CleanInvalidUninstallIn(RegistryKey baseKey)
        {
            foreach (var root in _uninstallRoots)
            {
                try
                {
                    using var rootKey = baseKey.OpenSubKey(root, true);
                    if (rootKey == null) continue;
                    foreach (var name in rootKey.GetSubKeyNames().ToList())
                    {
                        try
                        {
                            using var sub = rootKey.OpenSubKey(name, false);
                            if (sub == null) continue;

                            // 系统组件（SystemComponent=1，如被取代的 KB/可再发行组件）即使
                            // UninstallString 已失效也不应删除，避免误删系统级注册信息。
                            if ((sub.GetValue("SystemComponent") as int? ?? 0) == 1) continue;

                            var u = sub.GetValue("UninstallString") as string;
                            if (u != null && IsDeadFileReference(u))
                            {
                                rootKey.DeleteSubKeyTree(name, false);
                                _logger?.Info($"已清理孤立卸载残留: {root}\\{name}");
                            }
                        }
                        catch (Exception ex) { _logger?.Error($"清理卸载残留失败: {name}", ex); }
                    }
                }
                catch (Exception ex) { _logger?.Error($"打开卸载根失败: {root}", ex); }
            }
        }

        /// <summary>
        /// 清理“无效右键菜单”：文件/文件夹/桌面右键中指向已卸载程序的失效项。
        /// 覆盖两类：① shell 动词（其 command 指向的 exe 已不存在）；② Shell 扩展（ContextMenuHandlers
        /// 引用的 CLSID 其服务器 DLL/EXE 已不存在）。仅删除服务器文件经校验确实不存在的项，
        /// 不影响任何仍有效的右键菜单。同时扫描 32/64 位 Classes 与 HKCU/HKLM。
        /// </summary>
        private void CleanInvalidContextMenu()
        {
            CleanInvalidContextMenuIn(Registry.CurrentUser);
            CleanInvalidContextMenuIn(Registry.LocalMachine);
        }

        private void CleanInvalidContextMenuIn(RegistryKey baseKey)
        {
            foreach (var classRoot in ContextMenuMapping.ClassRootsForInvalidScan)
            {
                try
                {
                    using var classesKey = baseKey.OpenSubKey(classRoot, false);
                    if (classesKey == null) continue;
                    // 遍历所有类键：固定通用类键 + 扩展名键 + ProgID 子键（嵌套向下）
                    foreach (var cls in EnumerateContextMenuClassKeys(classesKey))
                    {
                        // ① shell 动词：command 指向的 exe 已不存在
                        try
                        {
                            using var shellKey = classesKey.OpenSubKey($@"{cls}\shell", true);
                            if (shellKey != null)
                            {
                                foreach (var verb in shellKey.GetSubKeyNames().ToList())
                                {
                                    try
                                    {
                                        if (verb.StartsWith("__DC_Disabled_", StringComparison.OrdinalIgnoreCase)) continue;
                                        using var verbKey = shellKey.OpenSubKey(verb, false);
                                        if (verbKey == null) continue;
                                        // 安全护栏：内置动词与系统/内置右键项永不清理，避免误删 Win11 第一层正常项
                                        if (ContextMenuMapping.ProtectedVerbs.Contains(verb)) continue;
                                        if (ContextMenuMapping.IsSystemContextMenuItem(verb, null)) continue;
                                        using var cmdKey = verbKey.OpenSubKey("command", false);
                                        if (cmdKey == null) continue;
                                        var cmdVal = cmdKey.GetValue(null) as string;
                                        if (IsDeadFileReference(cmdVal) || IsEmptyExecutableCommand(cmdVal))
                                        {
                                            shellKey.DeleteSubKeyTree(verb, false);
                                            _logger?.Info($"已清理无效右键菜单(动词): {classRoot}\\{cls}\\shell\\{verb}");
                                        }
                                    }
                                    catch (Exception ex) { _logger?.Error($"清理右键动词失败: {verb}", ex); }
                                }
                            }
                        }
                        catch { }

                        // ② Shell 扩展：ContextMenuHandlers 引用的 CLSID 服务器已不存在
                        try
                        {
                            using var shellexKey = classesKey.OpenSubKey($@"{cls}\shellex\ContextMenuHandlers", true);
                            if (shellexKey != null)
                            {
                                foreach (var handler in shellexKey.GetSubKeyNames().ToList())
                                {
                                    try
                                    {
                                        if (handler.StartsWith("__DC_Disabled_", StringComparison.OrdinalIgnoreCase)) continue;
                                        string guid = null;
                                        using (var hk = shellexKey.OpenSubKey(handler, false))
                                            guid = hk?.GetValue(null) as string;
                                        if (string.IsNullOrWhiteSpace(guid)) continue;

                                        // 安全护栏①：系统 Shell 扩展（分享、Defender 扫描等 Win11 第一层项）永不清理
                                        if (ContextMenuMapping.IsSystemContextMenuItem(handler, guid)) continue;

                                        // 安全护栏②：Win11 一级(顶层/显著)右键菜单是用户可见的常用功能
                                        // （如 Bandizip 压缩、火绒 安全扫描）。批量清理中绝不自动删除——
                                        // 宁可漏清，不可误删。用户可在"右键管理"中手动处理这些项。
                                        if (ContextMenuMapping.IsWin11TopLevelExtension(guid, handler)) continue;

                                        // 只有该 CLSID 所有存在的服务器值都失效才删除，
                                        // 避免 32 位服务器失效但 64 位仍可用时误删右键扩展。
                                        bool anyServer = false;
                                        bool allDead = true;
                                        foreach (var root in _clsidRoots)
                                        {
                                            using var clsidKey = baseKey.OpenSubKey(root, false);
                                            if (clsidKey == null) continue;
                                            using var guidKey = clsidKey.OpenSubKey(guid, false);
                                            if (guidKey == null) continue;
                                            foreach (var v in new[] { "InprocServer32", "InprocServer64", "LocalServer32", "LocalServer64" })
                                            {
                                                var path = guidKey.GetValue(v) as string;
                                                if (string.IsNullOrWhiteSpace(path)) continue;
                                                anyServer = true;
                                                if (!IsDeadFileReference(path)) { allDead = false; break; }
                                            }
                                            if (!allDead) break;
                                        }
                                        bool dead = anyServer && allDead;
                                        if (dead)
                                        {
                                            shellexKey.DeleteSubKeyTree(handler, false);
                                            _logger?.Info($"已清理无效右键菜单(扩展): {classRoot}\\{cls}\\shellex\\ContextMenuHandlers\\{handler}");
                                        }
                                    }
                                    catch (Exception ex) { _logger?.Error($"清理右键扩展失败: {handler}", ex); }
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex) { _logger?.Error($"打开 Classes 根失败: {classRoot}", ex); }
            }
        }

        private static long InvalidContextMenuSize()
        {
            long total = 0;
            total += InvalidContextMenuSizeIn(Registry.CurrentUser);
            total += InvalidContextMenuSizeIn(Registry.LocalMachine);
            return total;
        }

        private static long InvalidContextMenuSizeIn(RegistryKey baseKey)
        {
            long count = 0;
            foreach (var classRoot in ContextMenuMapping.ClassRootsForInvalidScan)
            {
                using var classesKey = baseKey.OpenSubKey(classRoot, false);
                if (classesKey == null) continue;
                // 遍历所有类键：通用类键 + 扩展名键 + ProgID 子键（嵌套向下，与清理逻辑保持一致）
                foreach (var cls in EnumerateContextMenuClassKeys(classesKey))
                {
                    try
                    {
                        using var shellKey = classesKey.OpenSubKey($@"{cls}\shell", false);
                        if (shellKey != null)
                            foreach (var verb in shellKey.GetSubKeyNames())
                            {
                                if (verb.StartsWith("__DC_Disabled_", StringComparison.OrdinalIgnoreCase)) continue;
                                using var verbKey = shellKey.OpenSubKey(verb, false);
                                if (verbKey == null) continue;
                                using var cmdKey = verbKey.OpenSubKey("command", false);
                                if (cmdKey == null) continue;
                                var cmdVal = cmdKey.GetValue(null) as string;
                                if (IsDeadFileReference(cmdVal) || IsEmptyExecutableCommand(cmdVal)) count++;
                            }
                    }
                    catch { }
                    try
                    {
                        using var shellexKey = classesKey.OpenSubKey($@"{cls}\shellex\ContextMenuHandlers", false);
                        if (shellexKey != null)
                            foreach (var handler in shellexKey.GetSubKeyNames())
                            {
                                if (handler.StartsWith("__DC_Disabled_", StringComparison.OrdinalIgnoreCase)) continue;
                                string guid = null;
                                using (var hk = shellexKey.OpenSubKey(handler, false))
                                    guid = hk?.GetValue(null) as string;
                                if (string.IsNullOrWhiteSpace(guid)) continue;
                                bool anyServer = false;
                                bool allDead = true;
                                foreach (var root in _clsidRoots)
                                {
                                    using var clsidKey = baseKey.OpenSubKey(root, false);
                                    if (clsidKey == null) continue;
                                    using var guidKey = clsidKey.OpenSubKey(guid, false);
                                    if (guidKey == null) continue;
                                    foreach (var v in new[] { "InprocServer32", "InprocServer64", "LocalServer32", "LocalServer64" })
                                    {
                                        var path = guidKey.GetValue(v) as string;
                                        if (string.IsNullOrWhiteSpace(path)) continue;
                                        anyServer = true;
                                        if (!IsDeadFileReference(path)) { allDead = false; break; }
                                    }
                                    if (!allDead) break;
                                }
                                if (anyServer && allDead) count++;
                            }
                    }
                    catch { }
                }
            }
            return count * 256; // 每个失效右键项的名义开销
        }

        /// <summary>
        /// 估算“无效启动项”的可清理数据量（字节，仅供参考；注册表清理主要价值在消除无效引用而非释放空间）。
        /// </summary>
        private static long InvalidStartupSize()
        {
            long total = 0;
            total += InvalidStartupSizeIn(Registry.CurrentUser);
            total += InvalidStartupSizeIn(Registry.LocalMachine);
            total += InvalidStartupFolderSize();
            total += InvalidScheduledTaskSize();
            return total;
        }

        private static long InvalidStartupSizeIn(RegistryKey baseKey)
        {
            long total = 0;
            foreach (var rel in _startupKeys)
            {
                try
                {
                    using var key = baseKey.OpenSubKey(rel, false);
                    if (key == null) continue;
                    foreach (var name in key.GetValueNames())
                    {
                        try
                        {
                            var val = key.GetValue(name) as string;
                            if (IsDeadFileReference(val) && val != null)
                                total += (val.Length + 1) * 2L;
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return total;
        }

        /// <summary>估算启动文件夹中失效 .lnk 的数量（每个约 1KB 名义开销）。</summary>
        private static long InvalidStartupFolderSize()
        {
            long count = 0;
            foreach (var folder in _startupFolders)
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
                try
                {
                    foreach (var lnk in Directory.GetFiles(folder, "*.lnk"))
                    {
                        try
                        {
                            var target = GetShortcutTarget(lnk);
                            // 与 CleanInvalidStartupFolder 的清理判定保持一致：仅当目标非空且
                            // 文件/目录确实不存在时才计为失效。目标为空（UWP/商店应用）属于
                            // “无法确定”，既不计入可清理估算，也不会被实际删除。
                            bool dead = !string.IsNullOrWhiteSpace(target)
                                        && !File.Exists(target)
                                        && !Directory.Exists(target);
                            if (dead) count++;
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return count * 1024;
        }

        /// <summary>估算任务计划程序中失效任务的数量（每个约 2KB 名义开销，含备份价值）。</summary>
        private static long InvalidScheduledTaskSize()
            => GetDeadScheduledTasks().Count * 2048;

        private static long InvalidComSize()
        {
            long total = 0;
            total += InvalidComCountIn(Registry.CurrentUser);
            total += InvalidComCountIn(Registry.LocalMachine);
            return total * 256; // 每个失效组件约 256 字节名义开销
        }

        private static long InvalidComCountIn(RegistryKey baseKey)
        {
            long count = 0;
            foreach (var root in _clsidRoots)
            {
                try
                {
                    using var clsidKey = baseKey.OpenSubKey(root, false);
                    if (clsidKey == null) continue;
                    foreach (var guid in clsidKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = clsidKey.OpenSubKey(guid, false);
                            if (sub == null) continue;

                            bool anyServer = false;
                            bool allDead = true;
                            foreach (var v in new[] { "InprocServer32", "InprocServer64", "LocalServer32", "LocalServer64" })
                            {
                                var path = sub.GetValue(v) as string;
                                if (string.IsNullOrWhiteSpace(path)) continue;
                                anyServer = true;
                                if (!IsDeadFileReference(path)) { allDead = false; break; }
                            }
                            if (anyServer && allDead) count++;
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return count;
        }

        private static long InvalidUninstallSize()
        {
            long total = 0;
            total += InvalidUninstallCountIn(Registry.CurrentUser);
            total += InvalidUninstallCountIn(Registry.LocalMachine);
            return total * 256;
        }

        private static long InvalidUninstallCountIn(RegistryKey baseKey)
        {
            long count = 0;
            foreach (var root in _uninstallRoots)
            {
                try
                {
                    using var rootKey = baseKey.OpenSubKey(root, false);
                    if (rootKey == null) continue;
                    foreach (var name in rootKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = rootKey.OpenSubKey(name, false);
                            if (sub == null) continue;
                            if ((sub.GetValue("SystemComponent") as int? ?? 0) == 1) continue;
                            var u = sub.GetValue("UninstallString") as string;
                            if (u != null && IsDeadFileReference(u)) count++;
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return count;
        }

        // （失效注册表的判定与清理实现见上方 IsDeadFileReference 与各 Clean/Size 方法）

        /// <summary>
        /// 清理“设备驱动程序包”：仅删除未被任何设备引用的孤立 OEM 驱动包。
        /// 通过 pnputil /enum-devices 取得在用驱动集合，凡不在其中的旧包才删除，
        /// 因此绝不会触碰正在使用的显卡/声卡/网卡等驱动。
        /// </summary>
        private void CleanOldDriverPackages()
        {
            foreach (var (name, _) in GetOrphanDriverPackages())
            {
                try
                {
                    var delPsi = new ProcessStartInfo("pnputil.exe", $"/delete-driver {name} /uninstall")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var delProc = Process.Start(delPsi);
                    delProc?.WaitForExit(8000);
                }
                catch (Exception ex)
                {
                    _logger?.Error($"删除孤立驱动包 {name} 失败", ex);
                }
            }
        }

        /// <summary>
        /// 返回当前被设备引用的 OEM 驱动包集合（小写）。
        /// 任何异常或输出无效（如系统不支持 /enum-devices）一律返回 null，
        /// 调用方据此 fail-safe：宁可什么都不删，也不误删在用驱动。
        /// </summary>
        private static HashSet<string> GetInUseDriverPackages()
        {
            try
            {
                var psi = new ProcessStartInfo("pnputil.exe", "/enum-devices")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return null;
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                // 校验输出确为 enum-devices 结果，避免不支持该参数时误判（fail-open 风险）。
                if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output) ||
                    !output.Contains("Instance ID", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in Regex.Matches(output, @"(?im)^\s*Driver Name:\s*(oem\d+\.inf)"))
                {
                    set.Add(m.Groups[1].Value);
                }
                // 若未解析到任何在用驱动（可能标签被本地化或格式变更），视为不可靠，
                // 宁可放弃删除，也绝不把在用驱动误判为孤立包而全部删掉。
                if (set.Count == 0) return null;
                return set;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 返回未被任何设备引用的孤立 OEM 驱动包（名称 + 驱动存储路径）。
        /// 若无法确定在用集合（GetInUseDriverPackages 返回 null），则返回空列表——不删除任何包。
        /// </summary>
        private static List<(string Name, string Path)> GetOrphanDriverPackages()
        {
            var inUse = GetInUseDriverPackages();
            if (inUse == null) return new List<(string, string)>();

            var result = new List<(string, string)>();
            try
            {
                var psi = new ProcessStartInfo("pnputil.exe", "/enum-drivers")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return result;
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                if (string.IsNullOrWhiteSpace(output) ||
                    !output.Contains("Published Name", StringComparison.OrdinalIgnoreCase))
                {
                    return result;
                }

                string currentName = null;
                foreach (var line in output.Split('\n'))
                {
                    // 空行分隔不同驱动包块，重置 currentName，防止上一块残留名称配对到本块路径
                    if (string.IsNullOrWhiteSpace(line)) { currentName = null; continue; }
                    var mName = Regex.Match(line, @"(?im)^\s*Published Name:\s*(oem\d+\.inf)");
                    if (mName.Success)
                    {
                        currentName = mName.Groups[1].Value.Trim();
                        continue;
                    }
                    var mPath = Regex.Match(line, @"(?im)^\s*Driver Store Path:\s*(.+)$");
                    if (mPath.Success && currentName != null)
                    {
                        if (!inUse.Contains(currentName))
                        {
                            result.Add((currentName, mPath.Groups[1].Value.Trim()));
                        }
                        currentName = null;
                    }
                }
            }
            catch
            {
                // 解析失败也保持空列表，不误删。
            }
            return result;
        }

        // ---------- 极限清理工具 ----------
        private void AddOptimizeTools()
        {
            var tools = new ObservableCollection<OptimizeTool>
            {
                MakeTool("🌙", "关闭休眠", "hibernate",
                    "关闭 Windows 休眠功能会删除 hiberfil.sys（体积约等于物理内存），可释放数 GB 空间；关闭后同时禁用「快速启动」（Fast Startup），开机可能稍慢。关闭后无法再使用「休眠」（睡眠 Sleep 不受影响）。如日后需要，点右侧「恢复」即可重新启用休眠。",
                    hasRestore: true,
                    restoreDesc: "重新启用休眠功能（powercfg -h on），会重建 hiberfil.sys 并恢复快速启动",
                    requiresAdmin: true),
                MakeTool("🛠️", "系统修复", "sfc",
                    "执行 sfc /scannow：扫描所有受 Windows 资源保护的关键系统文件，并用缓存中的正常副本修复损坏文件。耗时数分钟，过程中请勿关闭程序；多数系统文件损坏（如 DLL 缺失/被篡改）可借此修复，无需重装系统。",
                    requiresAdmin: true),
                MakeTool("🧹", "DISM 组件清理", "dism",
                    "执行 DISM /online /cleanup-image /startcomponentcleanup（安全模式），清理组件存储（WinSxS）中已被新版本取代的旧组件，常可释放数 GB。耗时较长（数分钟）。安全模式不会禁用更新卸载功能；如需释放更多空间可手动在管理员终端执行带 /resetbase 的版本（不可卸载累积更新）。程序内实时显示百分比进度与阶段文字。",
                    requiresAdmin: true),
                MakeTool("🗑️", "删除 Windows.old", "windowsold",
                    "删除升级 Windows 后留下的 Windows.old 旧系统目录，通常可释放十几到几十 GB。仅当该目录存在时才可操作；删除后无法再回退到旧系统。",
                    requiresAdmin: true),
                MakeTool("📦", "清理 Delivery Optimization", "do",
                    "清理 SoftwareDistribution\\DeliveryOptimization 与 Download 目录的缓存。不影响已安装的更新，下次 Windows 更新时会按需重新下载所需内容。执行时会自动检测 Windows Update 活动并临时暂停 BITS 服务，清理完成后自动恢复，防止更新损坏。",
                    requiresAdmin: true),
                MakeTool("🌐", "刷新 DNS 缓存", "dns",
                    "执行 ipconfig /flushdns，清空本地 DNS 缓存。可解决因旧 DNS 缓存导致的域名解析异常、网站打不开或访问到错误 IP 的问题。",
                    requiresAdmin: true)
            };
            OptimizeTools = tools;
        }

        private OptimizeTool MakeTool(string icon, string name, string action,
            string detail = null, bool hasRestore = false, string restoreDesc = null, bool requiresAdmin = false)
        {
            var tool = new OptimizeTool
            {
                Icon = icon,
                Name = name,
                Detail = detail,
                Action = action,
                RequiresAdmin = requiresAdmin,
                Command = new RelayCommand(_ => _ = RunOptimizeAsync(action))
            };
            if (hasRestore)
            {
                tool.HasRestore = true;
                tool.RestoreDescription = restoreDesc;
                tool.RestoreCommand = new RelayCommand(_ => _ = RunOptimizeAsync("hibernate_on", tool));
            }
            return tool;
        }

        // ---------- 递归枚举所有叶子清理项 ----------
        private static IEnumerable<CleanItem> AllCleanItems(IEnumerable<CleanGroup> groups)
        {
            foreach (var g in groups)
                foreach (var child in g.Children)
                {
                    if (child is CleanItem item)
                        yield return item;
                    else if (child is CleanGroup sub)
                        foreach (var subItem in AllCleanItems(new[] { sub }))
                            yield return subItem;
                }
        }

        /// <summary>
        /// 应用「仅显示有内容的项」过滤：扫描/清理后调用，使 Size 为 0 的项隐藏（AlwaysShow 的动作型项除外）。
        /// 这样列表只展示真正扫描到垃圾的项，0B 的不再显示。
        /// </summary>
        private void ApplyVisibilityFilter()
        {
            foreach (var item in AllCleanItems(CleanGroups))
            {
                item.IsVisible = item.AlwaysShow || item.Size > 0;
            }
        }

        // ---------- 扫描 ----------
        private async Task ScanAsync()
        {
            if (IsBusy) return;
            SetIsBusy(true);
            _cts = NewCts();
            try
            {
                StatusText = "正在扫描...";
                CleanProgressText = "";
                CleanFileCount = 0;
                IsScanning = true;
                ScanProgressValue = 0;
                ScanCurrentItem = "准备扫描...";

                var items = AllCleanItems(CleanGroups).ToList();
                int total = items.Count;

                // 第一阶段：在 UI 线程上设置初始状态（CanClean 检查很快，串行即可）
                var itemsToScan = new List<CleanItem>();
                foreach (var item in items)
                {
                    if (item.CanClean != null && item.CanClean())
                    {
                        // 扫描后仅默认勾选“安全级”项；谨慎级（驱动包、升级回滚、大型依赖缓存等）
                        // 保持不勾选，避免用户直接一键清理造成不可逆操作。回收站项扫描后再单独取消勾选。
                        item.IsChecked = item.Risk == RiskLevel.Safe;
                        itemsToScan.Add(item);
                    }
                    else
                    {
                        item.IsChecked = false;
                        item.Size = 0;
                        item.Status = ScanStatus.NotScanned;
                    }
                }

                // 第二阶段：并行扫描，限制并发度避免磁盘 I/O 争抢（HDD 上并发过高反而更慢）
                int done = total - itemsToScan.Count; // 不需要扫描的项已"完成"
                long totalSize = 0;
                int concurrency = Math.Min(4, Environment.ProcessorCount);
                var semaphore = new SemaphoreSlim(concurrency);
                var sizes = new long[itemsToScan.Count];

                var scanTasks = itemsToScan.Select(async (item, idx) =>
                {
                    if (_cts.Token.IsCancellationRequested) return;
                    await semaphore.WaitAsync();
                    try
                    {
                        // 标记「正在扫描」的项，UI 进度区实时显示当前项名
                        _uiDispatcher?.BeginInvoke(new Action(() => ScanCurrentItem = item.Name));
                        sizes[idx] = await Task.Run(() => EstimateItemSize(item));
                    }
                    catch { sizes[idx] = 0; }
                    finally
                    {
                        semaphore.Release();
                        var localDone = Interlocked.Increment(ref done);
                        // 通过 Dispatcher 异步更新 UI 进度（不阻塞后台扫描线程）
                        _ = _uiDispatcher.BeginInvoke(new Action(() =>
                        {
                            CleanProgressText = $"扫描 {localDone}/{total}";
                            ScanProgressValue = (int)(localDone * 100.0 / total);
                        }));
                    }
                }).ToArray();

                await Task.WhenAll(scanTasks);

                // 第三阶段：在 UI 线程批量更新扫描结果（避免逐项 Dispatcher 调用）
                for (int i = 0; i < itemsToScan.Count; i++)
                {
                    itemsToScan[i].Size = sizes[i];
                    itemsToScan[i].Status = ScanStatus.Scanned;
                    // 回收站项默认不勾选，不应计入顶部“可清理”总量
                    if (itemsToScan[i].Id != "empty_recycle_bin")
                        totalSize += sizes[i];
                }

                RecommendedTotalText = $"{FormatSize(totalSize)} 可清理";
                StatusText = "扫描完成";
                ApplyVisibilityFilter(); // 扫描后隐藏 0B 的清理项，只显示扫描到内容的项
                HasScanned = true; // 首次扫描完成后才显示结果列表
                // 回收站项扫描期间保持勾选，扫描结束后取消勾选，避免默认清空回收站
                var recycleScanItem = AllCleanItems(CleanGroups).FirstOrDefault(c => c.Id == "empty_recycle_bin");
                if (recycleScanItem != null) recycleScanItem.IsChecked = false;
                UpdateDriveInfo();
            }
            catch (Exception ex)
            {
                StatusText = "扫描失败";
                _logger.Error("扫描失败", ex);
            }
            finally
            {
                // 无论成功/失败/取消都结束扫描态，复位进度条，避免卡在中间进度
                IsScanning = false;
                ScanCurrentItem = "";
                _cts?.Dispose();
                SetIsBusy(false);
            }
        }

        // ---------- 清理 ----------
        private async Task CleanAsync()
        {
            var selected = AllCleanItems(CleanGroups)
                .Where(c => c.IsChecked == true && c.Risk != RiskLevel.Forbidden).ToList();
            if (!selected.Any())
            {
                MessageBox.Show("未选中任何可清理项。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (IsBusy) return;
            SetIsBusy(true);
            IsCleaning = true;
            CleanFileCount = 0;
            _cts = NewCts();
            var uiDispatcher = _uiDispatcher;
            // 本次清理默认“移入回收站”；极速模式下 Safe 级直接删除、Caution 级仍进回收站。
            // 回收站项勾选时才在最后清空。
            var prevUseRecycleBin = CleanService.UseRecycleBin;
            bool fastClean = _config?.FastClean == true;
            CleanService.UseRecycleBin = true;
            // 将“清空回收站”项从主列表分离，确保它在所有垃圾都移入回收站后再执行。
            var recycleItem = selected.FirstOrDefault(c => c.Id == "empty_recycle_bin");
            var mainItems = selected.Where(c => c != recycleItem).ToList();
            try
            {
                StatusText = "正在清理...";
                // 实际删除工作放到后台线程，避免阻塞 UI；通过 Dispatcher 切回 UI 线程实时更新进度与状态。
                await Task.Run(() =>
                {
                    foreach (var item in mainItems)
                    {
                        if (_cts.Token.IsCancellationRequested) break;
                        try
                        {
                            // 每个清理项执行前按「极速模式 + 风险等级」切换删除方式。
                            // 清理在单线程顺序执行，切换全局开关不会产生竞态。
                            CleanService.UseRecycleBin = !(fastClean && item.Risk == RiskLevel.Safe);

                            uiDispatcher?.Invoke(() =>
                            {
                                item.Status = ScanStatus.Cleaning;
                                CleanProgressText = $"正在清理：{item.Name}";
                            });

                            // 优先使用支持部分成功报告的新接口，否则回退到旧 Action + try/catch
                            CleanService.CleanResult result = default;
                            bool hasResultAware = item.CleanActionWithResult != null;

                            if (hasResultAware)
                            {
                                result = item.CleanActionWithResult();
                            }
                            else
                            {
                                item.CleanAction?.Invoke();
                                result = new CleanService.CleanResult { FilesSucceeded = 1 };
                            }

                            uiDispatcher?.Invoke(() =>
                            {
                                if (result.FullySucceeded || (result.NoOperation && !hasResultAware))
                                {
                                    // 全部成功（或旧方法无异常完成）
                                    item.Status = ScanStatus.Cleaned;
                                    item.ErrorMessage = null;
                                }
                                else if (result.PartiallySucceeded)
                                {
                                    // 部分成功：能删的都删了，少数被占用/保护
                                    item.Status = ScanStatus.PartiallyCleaned;
                                    item.ErrorMessage = result.ToString();
                                    // 部分成功时保留勾选——用户可能想重试剩余项
                                }
                                else if (result.FullyFailed)
                                {
                                    // 全部失败（零文件被删除）
                                    item.Status = ScanStatus.Error;
                                    item.ErrorMessage = result.ToString();
                                    item.IsChecked = false;
                                }
                                else if (result.NoOperation)
                                {
                                    // 无操作（目录已为空/不存在）→ 视为已清理
                                    item.Status = ScanStatus.Cleaned;
                                    item.ErrorMessage = null;
                                }
                                else
                                {
                                    // 旧路径无异常 → 成功
                                    item.Status = ScanStatus.Cleaned;
                                    item.ErrorMessage = null;
                                }

                                CleanFileCount++;
                                CleanProgressText = item.Status == ScanStatus.Cleaned ? $"已清理：{item.Name}"
                                    : item.Status == ScanStatus.PartiallyCleaned ? $"部分清理：{item.Name}"
                                    : $"失败：{item.Name}";
                            });
                        }
                        catch (Exception ex)
                        {
                            // 仅旧 void 方法或灾难性错误会走到这里
                            uiDispatcher?.Invoke(() =>
                            {
                                item.ErrorMessage = ex.Message;
                                item.Status = ScanStatus.Error;
                                item.IsChecked = false;
                                CleanFileCount++;
                                CleanProgressText = $"失败：{item.Name}";
                            });
                            _logger.Error($"清理失败: {item.Name}", ex);
                        }
                    }

                    // 所有垃圾已移入回收站，若勾选了“清空回收站”，最后再彻底清空。
                    if (recycleItem != null)
                    {
                        uiDispatcher?.Invoke(() =>
                        {
                            recycleItem.Status = ScanStatus.Cleaning;
                            CleanProgressText = "正在清空回收站...";
                        });
                        try
                        {
                            CleanService.EmptyRecycleBin(_logger);
                            uiDispatcher?.Invoke(() =>
                            {
                                recycleItem.Status = ScanStatus.Cleaned;
                                CleanProgressText = "已清空回收站";
                            });
                        }
                        catch (Exception ex)
                        {
                            uiDispatcher?.Invoke(() =>
                            {
                                recycleItem.Status = ScanStatus.Error;
                                CleanProgressText = "清空回收站失败";
                            });
                            _logger.Error("清空回收站失败", ex);
                        }
                    }
                }, _cts.Token);

                StatusText = "清理完成";
                CleanProgressText = string.Empty; // 清理结束后清掉最后一项的「已清理：XXX」提示
                // 清理后重新统计「剩余可清理」：
                //   Cleaned      → 大小归零、取消勾选（已全部清完）
                //   PartiallyCleaned → 保留原大小估算、保留勾选（用户可重试剩余项），ErrorMessage 已含实际释放量
                //   Error        → 保留大小、取消勾选
                long remaining = 0;
                foreach (var item in AllCleanItems(CleanGroups))
                {
                    if (item.Status == ScanStatus.Cleaned)
                    {
                        item.Size = 0;
                        item.IsChecked = false;
                    }
                    else if (item.Status == ScanStatus.Error)
                    {
                        item.IsChecked = false;
                    }
                    // PartiallyCleaned: 保持 Size 和 IsChecked 不变，用户可重试
                    else if (item.Id == "empty_recycle_bin")
                    {
                        item.Size = CleanService.GetRecycleBinSize();
                    }
                    if (item.IsChecked == true && item.Risk != RiskLevel.Forbidden && item.Status != ScanStatus.Cleaned)
                        remaining += item.Size;
                }
                RecommendedTotalText = remaining > 0 ? $"{FormatSize(remaining)} 可清理" : "—";
                ApplyVisibilityFilter(); // 清理后隐藏已清零（0B）的项，只保留仍有内容/动作型项
                UpdateDriveInfo();
                _ = SaveConfigAsync();
            }
            catch (Exception ex)
            {
                StatusText = "清理失败";
                CleanProgressText = string.Empty;
                _logger.Error("清理失败", ex);
            }
            finally
            {
                // 恢复全局回收站开关为清理前的设置，避免影响其它清理逻辑。
                CleanService.UseRecycleBin = prevUseRecycleBin;
                _cts?.Dispose();
                IsCleaning = false;
                SetIsBusy(false);
            }
        }


        // ---------- 单一清理项的预估大小 ----------
        /// <summary>
        /// 估算某个清理项的可清理数据大小（字节）。扫描时调用。
        /// 路径必须与 CleanXxx 方法中实际清理的路径完全一致，否则扫描显示的大小会与实际清理的大小不符。
        /// </summary>
        private long EstimateItemSize(CleanItem item)
        {
            try
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                switch (item.Id)
                {
                    case "temp_files":
                        return DirSize(Path.Combine(winDir, "Temp")) + DirSize(Path.GetTempPath());
                    case "minidumps":
                        long size = 0;
                        var miniDumpDir = Path.Combine(winDir, "Minidump");
                        if (Directory.Exists(miniDumpDir))
                            size += DiskUtils.FilesSize(miniDumpDir, "*.dmp");
                        // 复用 DiskUtils.FilesSize：单次枚举 + 读取缓存的 Length，避免
                        // Directory.GetFiles 全量数组 + 逐文件 new FileInfo().Length 重复 stat。
                        size += DiskUtils.FilesSize(winDir, "*.dmp");
                        // MEMORY.DMP 由 memory_dump 项单独统计，此处排除，避免重复计数
                        var memoryDumpFile = Path.Combine(winDir, "MEMORY.DMP");
                        if (File.Exists(memoryDumpFile))
                        {
                            try { size -= new FileInfo(memoryDumpFile).Length; } catch { }
                        }
                        return size;
                    case "windows_logs": return DirSize(Path.Combine(winDir, "Logs"));
                    case "thumbnail_cache":
                        // 仅估算 thumbcache_*.db 和 iconcache_*.db（与清理路径一致），不计算 Explorer 目录中的其他文件
                        return ThumbIconCacheDbSize(Path.Combine(local, @"Microsoft\Windows\Explorer"));
                    case "inet_cache": return DirSize(Path.Combine(local, @"Microsoft\Windows\INetCache"));
                    case "wer": return DirSize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER"));
                    case "delivery_optimization":
                        return DirSize(Path.Combine(winDir, @"SoftwareDistribution\DeliveryOptimization"))
                             + DirSize(Path.Combine(winDir, @"SoftwareDistribution\Download"));
                    case "edge_cache":
                        return BrowserProfileCachesSize(Path.Combine(local, @"Microsoft\Edge\User Data"), new[] { "Cache", "Code Cache" });
                    case "quark_cache":
                        {
                            long q = 0;
                            q += BrowserProfileCachesSize(Path.Combine(local, @"Quark\Quark\User Data"), new[] { "Cache", "Code Cache" });
                            q += BrowserProfileCachesSize(Path.Combine(local, @"夸克浏览器\User Data"), new[] { "Cache", "Code Cache" });
                            var appCache = Path.Combine(local, @"Quark\Application\Cache");
                            if (Directory.Exists(appCache)) q += DirSize(appCache);
                            return q;
                        }
                    case "chrome_cache":
                        return BrowserProfileCachesSize(Path.Combine(local, @"Google\Chrome\User Data"), new[] { "Cache", "Code Cache", "Media Cache" });
                    case "firefox_cache":
                        {
                            long fs = 0;
                            var profilesRoot = Path.Combine(local, @"Mozilla\Firefox\Profiles");
                            if (Directory.Exists(profilesRoot))
                                foreach (var profile in Directory.GetDirectories(profilesRoot))
                                    fs += DirSize(Path.Combine(profile, "cache2"))
                                        + DirSize(Path.Combine(profile, "thumbnails"))
                                        + DirSize(Path.Combine(profile, "cache"));
                            return fs;
                        }
                    case "brave_cache":
                        return BrowserProfileCachesSize(Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"), new[] { "Cache", "Code Cache" });
                    case "opera_cache":
                        {
                            // Opera Software 下可能有多个浏览器分支（Opera Stable / Opera GX 等），各自含 Cache/Code Cache
                            long oc = 0;
                            var operaRoot = Path.Combine(local, @"Opera Software");
                            if (Directory.Exists(operaRoot))
                                foreach (var browserDir in Directory.GetDirectories(operaRoot))
                                    oc += BrowserProfileCachesSize(browserDir, new[] { "Cache", "Code Cache" }, flatten: true);
                            return oc;
                        }
                    case "dingtalk_cache":
                        {
                            long ds = 0;
                            foreach (var c in new[]
                            {
                                Path.Combine(local, @"DingTalk\Cache"),
                                Path.Combine(local, @"DingTalk\update")
                            })
                                if (Directory.Exists(c)) ds += DirSize(c);
                            return ds;
                        }
                    case "netease_cache":
                        // 仅估算 Cache（与清理路径一致）；webdata 是登录凭据，不清理也不计入
                        return DirSize(Path.Combine(local, @"NetEase\CloudMusic\Cache"));
                    case "steam_cache":
                        {
                            var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                            long ss = 0;
                            foreach (var c in new[]
                            {
                                Path.Combine(x86, @"Steam\appcache"),
                                Path.Combine(x86, @"Steam\httpcache"),
                                Path.Combine(x86, @"Steam\config\htmlcache")
                            })
                                if (Directory.Exists(c)) ss += DirSize(c);
                            return ss;
                        }
                    case "windows_widgets":
                        return DirSize(Path.Combine(local, @"Packages\MicrosoftWindows.Client.WebExperience_cw5n1h2txyewy\LocalCache"));
                    case "directx_shader_cache":
                        return DirSize(Path.Combine(local, "D3DSCache"));
                    case "cert_cache":
                        return DirSize(Path.Combine(local, @"Microsoft\CryptnetUrlCache"));
                    case "etw_logs":
                        return DirSize(Path.Combine(winDir, @"System32\LogFiles\WMI\RtBackup"))
                             + DirSize(Path.Combine(winDir, @"Logs\MeasuredBoot"));
                    case "recent_items":
                        // 只估算真正的最近访问 .lnk 快捷方式大小；Quick Access / 跳转列表数据不再清理，也不计入。
                        return LnkFilesSize(Environment.GetFolderPath(Environment.SpecialFolder.Recent));
                    case "clipboard_history":
                        return DirSize(Path.Combine(local, @"Microsoft\Windows\Clipboard"));
                    case "invalid_startup":
                        return InvalidStartupSize();
                    case "invalid_com":
                        return InvalidComSize();
                    case "invalid_uninstall":
                        return InvalidUninstallSize();
                    case "invalid_context_menu":
                        return InvalidContextMenuSize();
                    case "invalid_file_association":
                        return InvalidFileAssociationSize();
                    case "invalid_shared_dlls":
                        return InvalidSharedDllsSize();
                    case "invalid_mui_cache":
                        return InvalidMuiCacheSize();
                    case "invalid_appcompat_layers":
                        return InvalidAppCompatLayersSize();
                    case "driver_packages":
                        {
                            long total = 0;
                            foreach (var (_, path) in GetOrphanDriverPackages())
                            {
                                if (Directory.Exists(path)) total += DirSize(path);
                            }
                            return total;
                        }
                    case "prefetch":
                        return DirSize(Path.Combine(winDir, "Prefetch"));
                    case "memory_dump":
                        {
                            var dump = Path.Combine(winDir, "MEMORY.DMP");
                            return File.Exists(dump) ? new FileInfo(dump).Length : 0;
                        }
                    case "store_cache":
                        {
                            long sc = 0;
                            var packages = Path.Combine(local, "Packages");
                            if (Directory.Exists(packages))
                                foreach (var pkg in Directory.GetDirectories(packages))
                                    if (Path.GetFileName(pkg).StartsWith("Microsoft.WindowsStore", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var cache = Path.Combine(pkg, "LocalCache");
                                        if (Directory.Exists(cache)) sc += DirSize(cache);
                                    }
                            return sc;
                        }
                    case "baidu_pan_cache":
                        {
                            long bp = 0;
                            foreach (var c in new[]
                            {
                                Path.Combine(local, @"Baidu\BaiduNetdisk\cache"),
                                Path.Combine(local, @"Baidu\BaiduNetdisk\tmp")
                            })
                                if (Directory.Exists(c)) bp += DirSize(c);
                            return bp;
                        }
                    case "thunder_cache":
                        {
                            // 仅估算 Cache + XLLiveUD（与清理路径一致）；Torrents 是用户收藏种子，不清理也不计入
                            long tc = 0;
                            foreach (var c in new[]
                            {
                                Path.Combine(local, @"Thunder Network\Thunder\Profiles\Cache"),
                                Path.Combine(local, @"Thunder Network\XLLiveUD\Cache")
                            })
                                if (Directory.Exists(c)) tc += DirSize(c);
                            return tc;
                        }
                    case "bilibili_cache":
                        {
                            long bc = 0;
                            foreach (var c in new[]
                            {
                                Path.Combine(local, @"bilibili\Cache"),
                                Path.Combine(local, @"Bilibili\Cache")
                            })
                                if (Directory.Exists(c)) bc += DirSize(c);
                            return bc;
                        }
                    case "iqiyi_cache":
                        {
                            long ic = 0;
                            foreach (var c in new[]
                            {
                                Path.Combine(local, @"Qiyi\QiyiClient\cache"),
                                Path.Combine(local, @"Qiyi\QiyiClient\webcache")
                            })
                                if (Directory.Exists(c)) ic += DirSize(c);
                            return ic;
                        }
                    case "discord_cache":
                        {
                            long dc = 0;
                            var stable = Path.Combine(local, @"Discord");
                            foreach (var c in new[] { "Cache", "Code Cache", "GPUCache" })
                            {
                                var p = Path.Combine(stable, c);
                                if (Directory.Exists(p)) dc += DirSize(p);
                            }
                            return dc;
                        }
                    case "browser360_cache":
                        return BrowserProfileCachesSize(Path.Combine(local, @"360Chrome\Chrome\User Data"), new[] { "Cache", "Code Cache" });
                    case "sogou_cache":
                        {
                            long sg = 0;
                            foreach (var c in new[]
                            {
                                Path.Combine(local, @"SogouExplorer\Cache"),
                                Path.Combine(local, @"SogouExplorer\WebkitCache")
                            })
                                if (Directory.Exists(c)) sg += DirSize(c);
                            return sg;
                        }
                    case "feishu_cache":
                        {
                            long fc = 0;
                            foreach (var c in new[]
                            {
                                Path.Combine(local, @"Feishu\Cache"),
                                Path.Combine(local, @"Feishu\Application\Cache"),
                                Path.Combine(local, @"Feishu\user_data\cache")
                            })
                                if (Directory.Exists(c)) fc += DirSize(c);
                            return fc;
                        }
                    case "wemeet_cache":
                        {
                            // 仅估算 Cache + AppCache（与清理路径一致）；Recording 是用户录制视频，不清理也不计入
                            long mc = 0;
                            foreach (var c in new[]
                            {
                                Path.Combine(local, @"Tencent\WeMeet\Cache"),
                                Path.Combine(local, @"Tencent\WeMeet\AppCache")
                            })
                                if (Directory.Exists(c)) mc += DirSize(c);
                            return mc;
                        }
                    case "jianying_cache":
                        {
                            long jc = 0;
                            foreach (var c in new[]
                            {
                                Path.Combine(local, @"JianyingPro\Cache"),
                                Path.Combine(local, @"JianyingPro\User Data\Cache"),
                                Path.Combine(local, @"JianyingPro\User Data\GPUCache")
                            })
                                if (Directory.Exists(c)) jc += DirSize(c);
                            return jc;
                        }
                    case "telegram_cache":
                        {
                            long tg = 0;
                            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                            var safeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ".cache", ".blob", ".bin", ".tmp", ".dat", ".jpg", ".jpeg", ".png", ".webp", ".gif",
                                ".mp4", ".mp3", ".ogg", ".webm", ".mov", ".mkv", ".avi"
                            };
                            foreach (var c in new[]
                            {
                                Path.Combine(appData, @"Telegram Desktop\tdata\cache"),
                                Path.Combine(appData, @"Telegram Desktop\tdata\media_cache")
                            })
                                tg += DirSizeByExtensionWhitelist(c, safeExts);
                            return tg;
                        }
                    case "spotify_cache":
                        {
                            // 仅估算 cache + Storage（与清理路径一致）；Data 是离线音乐，不清理也不计入
                            long sp = 0;
                            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                            foreach (var c in new[]
                            {
                                Path.Combine(local, @"Spotify\cache"),
                                Path.Combine(appData, @"Spotify\Storage")
                            })
                                if (Directory.Exists(c)) sp += DirSize(c);
                            return sp;
                        }
                    case "adobe_cache":
                        {
                            long ac = 0;
                            var adobeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ".pek", ".cfa", ".ims", ".xmp"
                            };
                            foreach (var c in new[]
                            {
                                Path.Combine(local, @"Adobe\Common\Media Cache Files"),
                                Path.Combine(local, @"Adobe\Common\Media Cache")
                            })
                                ac += DirSizeByExtensionWhitelist(c, adobeExts);
                            var premiereRoot = Path.Combine(local, @"Adobe\Premiere Pro");
                            if (Directory.Exists(premiereRoot))
                                foreach (var ver in Directory.GetDirectories(premiereRoot))
                                {
                                    var mc = Path.Combine(ver, "Media Cache Files");
                                    ac += DirSizeByExtensionWhitelist(mc, adobeExts);
                                }
                            return ac;
                        }
                    case "kuaishou_cache":
                        {
                            long ks = 0;
                            foreach (var c in new[]
                            {
                                Path.Combine(local, @"Kuaishou\Cache"),
                                Path.Combine(local, @"Kuaishou\Application\Cache")
                            })
                                if (Directory.Exists(c)) ks += DirSize(c);
                            return ks;
                        }
                    case "empty_recycle_bin":
                        return CleanService.GetRecycleBinSize();

                    // ===== 新增：系统级清理项估算 =====
                    case "windows_upgrade_temp":
                        {
                            var sysDrive = Path.GetPathRoot(winDir);
                            if (string.IsNullOrEmpty(sysDrive)) return 0;
                            long wu = 0;
                            foreach (var c in new[]
                            {
                                Path.Combine(sysDrive, "$WINDOWS.~BT"),
                                Path.Combine(sysDrive, "$WINDOWS.~WS"),
                                Path.Combine(sysDrive, "$WINDOWS.~Q")
                            })
                                if (Directory.Exists(c)) wu += DirSize(c);
                            return wu;
                        }
                    case "defender_history":
                        {
                            long dh = 0;
                            var scansRoot = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                @"Microsoft\Windows Defender\Scans\History");
                            foreach (var c in new[]
                            {
                                Path.Combine(scansRoot, @"Service\DetectionHistory"),
                                Path.Combine(scansRoot, @"Service\DtlQuarantine"),
                                Path.Combine(scansRoot, @"Store"),
                                Path.Combine(scansRoot, @"ParserStore")
                            })
                                if (Directory.Exists(c)) dh += DirSize(c);
                            return dh;
                        }
                    case "font_cache":
                        return FilesByPatternSize(Path.Combine(winDir, @"ServiceProfiles\LocalService\AppData\Local\FontCache"), "*.dat");
                    case "live_kernel_reports":
                        return DirSize(Path.Combine(winDir, "LiveKernelReports"));
                    case "icon_cache":
                        {
                            var f = Path.Combine(local, "IconCache.db");
                            return File.Exists(f) ? new FileInfo(f).Length : 0;
                        }

                    // ===== 新增：即时通讯估算 =====
                    case "wechat_cache":
                        return WeChatCacheSize();
                    case "qq_cache":
                        return QQCacheSize();
                    case "wxwork_cache":
                        return WXWorkCacheSize();
                    case "mailmaster_cache":
                        return DirSize(Path.Combine(local, @"MailMaster\Cache"))
                             + DirSize(Path.Combine(local, @"MailMaster\Application\Cache"));

                    // ===== 新增：浏览器估算（多 Profile） =====
                    case "qq_browser_cache":
                        return BrowserProfileCachesSize(Path.Combine(local, @"Tencent\QQBrowser\User Data"), new[] { "Cache", "Code Cache" });
                    case "vivaldi_cache":
                        return BrowserProfileCachesSize(Path.Combine(local, @"Vivaldi\User Data"), new[] { "Cache", "Code Cache" });

                    // ===== 新增：音视频估算 =====
                    case "qq_music_cache":
                        return DirSize(Path.Combine(local, @"Tencent\QQMusic\Cache")) + DirSize(Path.Combine(local, @"QQMusic\Cache"));
                    case "kugou_cache":
                        return DirSize(Path.Combine(local, @"KuGou\Cache")) + DirSize(Path.Combine(local, @"KuGou8\Cache"));
                    case "kuwo_cache":
                        return DirSize(Path.Combine(local, @"KuwoMusic\Cache")) + DirSize(Path.Combine(local, @"Kuwo\Cache"));
                    case "qqlive_cache":
                        return DirSize(Path.Combine(local, @"Tencent\QQLive\Cache")) + DirSize(Path.Combine(local, @"QQLive\Cache"));
                    case "youku_cache":
                        return DirSize(Path.Combine(local, @"Youku\YoukuClient\cache")) + DirSize(Path.Combine(local, @"Youku\cache"));

                    // ===== 新增：办公与游戏估算 =====
                    case "wps_cache":
                        return DirSize(Path.Combine(local, @"Kingsoft\office6\cache")) + DirSize(Path.Combine(local, @"WPS\cache"));
                    case "epic_cache":
                        {
                            long ec = 0;
                            foreach (var c in new[]
                            {
                                Path.Combine(local, @"Epic\EpicGamesLauncher\Data\EMS\cache"),
                                Path.Combine(local, @"Epic\EpicGamesLauncher\Data\EMS\EPIC"),
                                Path.Combine(local, @"Epic\EpicGamesLauncher\WebCache")
                            })
                                if (Directory.Exists(c)) ec += DirSize(c);
                            return ec;
                        }

                    // ===== 新增：开发工具缓存估算 =====
                    case "vscode_cache":
                        {
                            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                            long vc = 0;
                            foreach (var c in new[]
                            {
                                Path.Combine(appData, @"Code\Cache"),
                                Path.Combine(appData, @"Code\CachedData"),
                                Path.Combine(appData, @"Code\Code Cache"),
                                Path.Combine(appData, @"Code\GPUCache")
                            })
                                if (Directory.Exists(c)) vc += DirSize(c);
                            return vc;
                        }
                    case "jetbrains_cache":
                        {
                            long jc = 0;
                            var jbRoot = Path.Combine(local, @"JetBrains");
                            if (Directory.Exists(jbRoot))
                                foreach (var productDir in Directory.GetDirectories(jbRoot))
                                    foreach (var sub in new[] { "caches", "log", "tmp", "index" })
                                    {
                                        var p = Path.Combine(productDir, sub);
                                        if (Directory.Exists(p)) jc += DirSize(p);
                                    }
                            var asRoot = Path.Combine(local, @"Google\AndroidStudio");
                            if (Directory.Exists(asRoot))
                                foreach (var verDir in Directory.GetDirectories(asRoot))
                                    foreach (var sub in new[] { "caches", "log", "tmp", "index" })
                                    {
                                        var p = Path.Combine(verDir, sub);
                                        if (Directory.Exists(p)) jc += DirSize(p);
                                    }
                            return jc;
                        }
                    case "vs_component_cache":
                        {
                            long vcc = 0;
                            var vsRoot = Path.Combine(local, @"Microsoft\VisualStudio");
                            if (Directory.Exists(vsRoot))
                            {
                                foreach (var verDir in Directory.GetDirectories(vsRoot))
                                {
                                    var cm = Path.Combine(verDir, "ComponentModelCache");
                                    if (Directory.Exists(cm)) vcc += DirSize(cm);
                                }
                                try
                                {
                                    foreach (var mef in Directory.GetDirectories(vsRoot, "MEFCache", SearchOption.AllDirectories))
                                        vcc += DirSize(mef);
                                }
                                catch { }
                            }
                            return vcc;
                        }
                    case "npm_cache":
                        return DirSize(Path.Combine(local, "npm-cache"));
                    case "pip_cache":
                        return DirSize(Path.Combine(local, @"pip\cache"));
                    case "maven_cache":
                        return DirSize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".m2\repository"));
                    case "gradle_cache":
                        return DirSize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".gradle\caches"));
                    case "nuget_cache":
                        return DirSize(Path.Combine(local, @"NuGet\v3-cache")) + DirSize(Path.Combine(local, @"NuGet\Cache"));

                    default: return 0;
                }
            }
            catch { return 0; }
        }

        /// <summary>
        /// 计算某目录下所有 .lnk 快捷方式的总字节数（用于「Windows 最近访问」大小估算）。
        /// </summary>
        private static long LnkFilesSize(string dir)
        {
            // 复用 DiskUtils.FilesSize：单次枚举 + 读取缓存的 Length，避免
            // Directory.GetFiles 全量数组 + 逐文件 new FileInfo().Length 重复 stat。
            return DiskUtils.FilesSize(dir, "*.lnk");
        }

        /// <summary>
        /// 估算浏览器多 Profile 缓存目录总大小（与 CleanBrowserProfileCaches 路径一致）。
        /// 严格按 Profile 命名规范匹配（Default / Profile N / Guest Profile / System Profile），
        /// 不计算 User Data 根目录下的 Cookies / Login Data / Bookmarks 等用户数据。
        /// </summary>
        private static long BrowserProfileCachesSize(string userDataRoot, string[] cacheSubdirs, bool flatten = false)
        {
            if (!Directory.Exists(userDataRoot)) return 0;
            if (cacheSubdirs == null || cacheSubdirs.Length == 0) return 0;

            var profileDirs = new List<string>();
            if (flatten)
            {
                profileDirs.Add(userDataRoot);
            }
            else
            {
                foreach (var d in Directory.GetDirectories(userDataRoot))
                {
                    var name = Path.GetFileName(d);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (name.Equals("Default", StringComparison.Ordinal)
                        || name.StartsWith("Profile ", StringComparison.Ordinal)
                        || name.Equals("Guest Profile", StringComparison.Ordinal)
                        || name.Equals("System Profile", StringComparison.Ordinal))
                        profileDirs.Add(d);
                }
            }

            long total = 0;
            foreach (var profileDir in profileDirs)
                foreach (var cacheName in cacheSubdirs)
                {
                    var p = Path.Combine(profileDir, cacheName);
                    if (Directory.Exists(p)) total += DirSize(p);
                }
            return total;
        }

        /// <summary>估算目录中匹配通配符的文件总大小（顶层，不递归）。</summary>
        private static long FilesByPatternSize(string dir, string pattern)
        {
            return DiskUtils.FilesSize(dir, pattern);
        }

        /// <summary>估算 Explorer 目录中 thumbcache_*.db 和 iconcache_*.db 的总大小（与清理路径一致）。</summary>
        private static long ThumbIconCacheDbSize(string explorerDir)
        {
            if (!Directory.Exists(explorerDir)) return 0;
            return DiskUtils.FilesSize(explorerDir, "thumbcache_*.db")
                 + DiskUtils.FilesSize(explorerDir, "iconcache_*.db");
        }

        /// <summary>估算微信所有账号的 FileStorage\Cache 总大小（与清理路径一致，不计算 Image/Video/MsgAttach/File）。</summary>
        private static long WeChatCacheSize()
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var wechatRoot = Path.Combine(documents, "WeChat Files");
            if (!Directory.Exists(wechatRoot))
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                wechatRoot = Path.Combine(local, @"Tencent\WeChat");
            }
            if (!Directory.Exists(wechatRoot)) return 0;
            long total = 0;
            foreach (var wxidDir in Directory.GetDirectories(wechatRoot))
            {
                if (Path.GetFileName(wxidDir).Equals("All Users", StringComparison.OrdinalIgnoreCase)) continue;
                var cache = Path.Combine(wxidDir, @"FileStorage\Cache");
                if (Directory.Exists(cache)) total += DirSize(cache);
            }
            return total;
        }

        /// <summary>估算 QQ 所有账号的 RichMedia 总大小（与清理路径一致，不计算 Image/Video/FileRecv）。</summary>
        private static long QQCacheSize()
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var tencentRoot = Path.Combine(documents, "Tencent Files");
            if (!Directory.Exists(tencentRoot)) return 0;
            long total = 0;
            foreach (var qqDir in Directory.GetDirectories(tencentRoot))
            {
                if (Path.GetFileName(qqDir).Equals("All Users", StringComparison.OrdinalIgnoreCase)) continue;
                var richMedia = Path.Combine(qqDir, "RichMedia");
                if (Directory.Exists(richMedia)) total += DirSize(richMedia);
            }
            return total;
        }

        /// <summary>估算企业微信所有账号的 Cache 总大小（与清理路径一致，不计算 Image/Video/Msg）。</summary>
        private static long WXWorkCacheSize()
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var wxworkRoot = Path.Combine(documents, "WXWork");
            if (!Directory.Exists(wxworkRoot)) return 0;
            long total = 0;
            foreach (var uidDir in Directory.GetDirectories(wxworkRoot))
            {
                var cache = Path.Combine(uidDir, "Cache");
                if (Directory.Exists(cache)) total += DirSize(cache);
            }
            return total;
        }

        // ---------- 刷新（重新评估可清理） ----------
        private void RefreshItems()
        {
            foreach (var item in AllCleanItems(CleanGroups))
            {
                // 仅“安全”级随刷新自动勾选；“谨慎”级保持用户当前选择，避免误勾高风险项。
                if (item.Risk != RiskLevel.Safe) continue;
                // 回收站项始终不自动勾选，由用户手动决定。
                if (item.Id == "empty_recycle_bin") continue;
                if (item.CanClean != null)
                {
                    item.IsChecked = item.CanClean();
                }
            }
            StatusText = "已刷新";
            long total = AllCleanItems(CleanGroups)
                .Where(c => c.IsChecked == true && c.Risk != RiskLevel.Forbidden && c.Status != ScanStatus.Cleaned)
                .Sum(c => c.Size);
            RecommendedTotalText = total > 0 ? $"{FormatSize(total)} 可清理" : "—";
            ApplyVisibilityFilter();
            UpdateDriveInfo();
        }

        // ---------- 极限清理执行 ----------
        /// <summary>当前进程是否以管理员身份运行（供极限清理 UI 判定“需管理员”禁用态）。</summary>
        public bool IsElevated => SystemOptimizeService.IsRunningAsAdministrator();

        private async Task RunOptimizeAsync(string action, OptimizeTool specificTool = null)
        {
            // 直接按 Action 标识定位工具（specificTool 用于“恢复”等不对应独立工具项的动作）。
            var tool = specificTool ?? OptimizeTools.FirstOrDefault(t => t.Action == action);
            if (tool == null) return;

            if (IsBusy) return;

            // 未提权时，需管理员权限的工具直接给出清晰中文提示，而非启动 DISM/SFC 后拿到乱码的“无权限”报错。
            if (tool.RequiresAdmin && !SystemOptimizeService.IsRunningAsAdministrator())
            {
                const string msg = "本操作需要管理员权限，请右键以“管理员身份运行”本程序后重试。";
                tool.StatusText = msg;
                OptimizeLogList.Add($"{DateTime.Now:HH:mm:ss} [{tool.Name}] 需要管理员权限");
                StatusText = $"{tool.Name}：需要管理员权限";
                return;
            }

            SetIsBusy(true);

            tool.StatusText = "执行中...";
            tool.IsRunning = true;
            tool.ProgressValue = 0;
            tool.IsIndeterminate = true;
            var progress = new Progress<string>(msg => tool.StatusText = msg);
            var percentProgress = new Progress<int>(p =>
            {
                tool.ProgressValue = p;
                tool.IsIndeterminate = false;
            });
            try
            {
                var result = await _optimizeService.ExecuteAsync(action, progress, percentProgress);
                tool.StatusText = result;
                OptimizeLogList.Add($"{DateTime.Now:HH:mm:ss} [{tool.Name}] {result}");
                StatusText = $"{tool.Name}：{result}";
            }
            catch (Exception ex)
            {
                tool.StatusText = "错误: " + ex.Message;
                _logger.Error($"优化失败: {action}", ex);
            }
            finally
            {
                tool.IsRunning = false;
                SetIsBusy(false);
            }
        }

        // ---------- 空文件夹扫描 / 删除 ----------
        private async Task ScanEmptyFoldersAsync()
        {
            if (IsBusy) return;
            SetIsBusy(true);
            try
            {
                _cts = NewCts();
                StatusText = "正在扫描空文件夹...";
                EmptyFolderList.Clear();
                EmptyFolderReportText = "";
                EmptyFolderStatusText = "扫描中...";

                // 复用 SpaceAnalyzer.GetEmptyFoldersAsync：迭代遍历、跳过重解析点（如 Documents and Settings）、
                // 按目录捕获异常、支持取消，避免对整个 C: 递归时因首个不可访问目录而整体中止。
                var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                var folders = await _spaceAnalyzer.GetEmptyFoldersAsync(root, _cts.Token);

                EmptyFolderList.Clear();
                foreach (var f in folders) EmptyFolderList.Add(f);
                EmptyFolderReportText = folders.Count == 0
                    ? "未发现空文件夹。"
                    : string.Join(Environment.NewLine, folders);
                EmptyFolderStatusText = $"找到 {folders.Count} 个空文件夹";
                StatusText = "空文件夹扫描完成";
                UpdateDriveInfo();
            }
            catch (OperationCanceledException)
            {
                EmptyFolderStatusText = "扫描已取消。";
                StatusText = "已取消";
            }
            catch (Exception ex)
            {
                _logger.Error("空文件夹扫描失败", ex);
                EmptyFolderStatusText = "扫描失败：" + ex.Message;
                StatusText = "空文件夹扫描失败";
            }
            finally
            {
                _cts?.Dispose();
                SetIsBusy(false);
            }
        }

        private async Task DeleteEmptyFoldersAsync()
        {
            if (!EmptyFolderList.Any())
            {
                EmptyFolderStatusText = "没有可删除的空文件夹。";
                return;
            }
            if (MessageBox.Show($"确认删除 {EmptyFolderList.Count} 个空文件夹？", "确认",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            await Task.Run(() =>
            {
                int deleted = 0;
                // 按目录深度（路径分隔符数量）降序、再按路径长度降序排序：
                // 先删子目录、后删父目录，避免父目录因仍有子项残留而单轮删不掉。
                foreach (var dir in EmptyFolderList.ToList()
                             .OrderByDescending(d => d.Count(c => c == '\\'))
                             .ThenByDescending(d => d.Length))
                {
                    try
                    {
                        // 防御：重解析点（junction/symlink）绝不删除，避免误删链接目标。
                        if (DiskUtils.IsReparsePoint(dir))
                            continue;
                        Directory.Delete(dir);
                        deleted++;
                    }
                    catch { }
                }
                _uiDispatcher.Invoke(() =>
                {
                    EmptyFolderList.Clear();
                    EmptyFolderReportText = $"已删除 {deleted} 个空文件夹。";
                    EmptyFolderStatusText = $"删除了 {deleted} 个空文件夹";
                    StatusText = "空文件夹清理完成";
                });
            });
        }
    }
}
