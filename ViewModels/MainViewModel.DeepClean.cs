using DiskCleaner.Interop;
using DiskCleaner.Models;
using DiskCleaner.Services;
using DiskCleaner.Views;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace DiskCleaner.ViewModels
{
    
    
    
    
    public partial class MainViewModel : ViewModelBase
    {
        
        private ObservableCollection<CleanGroup> _cleanGroups = new();
        private ObservableCollection<OptimizeTool> _optimizeTools = new();
        private ObservableCollection<string> _optimizeLogList = new();
        private ObservableCollection<string> _emptyFolderList = new();

        
        private bool _isGarbageCleanupExpanded;
        private bool _isSystemToolsExpanded;
        private bool _isEmptyFolderExpanded;

        
        private bool _isCleaning;
        private string _cleanProgressText = "";
        private int _cleanFileCount;
        private string _recommendedTotalText = "—";

        
        private string _emptyFolderReportText = "";
        private string _emptyFolderStatusText = "";

        
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

        private readonly ConcurrentDictionary<OptimizeTool, CancellationTokenSource> _toolCtsMap = new();
        private bool AnyToolRunning => _toolCtsMap.Count > 0;

        public ObservableCollection<string> EmptyFolderList
        {
            get => _emptyFolderList;
            private set => SetProperty(ref _emptyFolderList, value);
        }

        public bool IsGarbageCleanupExpanded { get => _isGarbageCleanupExpanded; set => SetProperty(ref _isGarbageCleanupExpanded, value); }
        public bool IsSystemToolsExpanded { get => _isSystemToolsExpanded; set => SetProperty(ref _isSystemToolsExpanded, value); }
        public bool IsEmptyFolderExpanded { get => _isEmptyFolderExpanded; set => SetProperty(ref _isEmptyFolderExpanded, value); }

        public bool IsCleaning { get => _isCleaning; private set => SetProperty(ref _isCleaning, value); }

        
        private bool _hasScanned;
        public bool HasScanned { get => _hasScanned; private set => SetProperty(ref _hasScanned, value); }

        
        public bool HasCleanableSelection =>
            AllCleanItems(CleanGroups).Any(c => c.IsChecked == true && c.Risk != RiskLevel.Forbidden);

        public string CleanProgressText { get => _cleanProgressText; set => SetProperty(ref _cleanProgressText, value); }
        public int CleanFileCount { get => _cleanFileCount; set => SetProperty(ref _cleanFileCount, value); }
        public string RecommendedTotalText { get => _recommendedTotalText; set => SetProperty(ref _recommendedTotalText, value); }

        
        private bool _isScanning;
        private int _scanProgressValue;
        private string _scanCurrentItem = "";
        
        public bool IsScanning { get => _isScanning; private set => SetProperty(ref _isScanning, value); }
        
        public int ScanProgressValue { get => _scanProgressValue; private set => SetProperty(ref _scanProgressValue, value); }
        
        public string ScanCurrentItem { get => _scanCurrentItem; private set => SetProperty(ref _scanCurrentItem, value); }

        public string EmptyFolderReportText { get => _emptyFolderReportText; set => SetProperty(ref _emptyFolderReportText, value); }
        public string EmptyFolderStatusText { get => _emptyFolderStatusText; set => SetProperty(ref _emptyFolderStatusText, value); }

        
        public ICommand ScanCommand { get; private set; }
        public ICommand CleanCommand { get; private set; }
        public ICommand RefreshCommand { get; private set; }
        public ICommand EmptyFolderScanCommand { get; private set; }
        public ICommand EmptyFolderDeleteCommand { get; private set; }

        

        
        private bool _cleanItemsInitialized;
        internal void EnsureCleanItemsInitialized()
        {
            if (_cleanItemsInitialized) return;
            _cleanItemsInitialized = true;
            InitCleanItems();
            AddOptimizeTools();
            
            
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            
            foreach (var item in AllCleanItems(CleanGroups))
            {
                item.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(CleanItem.IsChecked))
                        OnPropertyChanged(nameof(HasCleanableSelection));
                };
            }
        }

        
        private void InitCleanItems()
        {
            var systemGroup = new CleanGroup("系统清理");
            systemGroup.AddRange(new List<ICleanNode>
            {
                MakeCleanItem("temp_files", "临时文件", "清理 Windows 与用户临时文件目录", RiskLevel.Safe, CleanTempFiles),
                MakeCleanItem("minidumps", "系统错误小型转储文件", "删除 C:\\Windows\\Minidump 中的小型转储文件", RiskLevel.Safe, CleanMinidumps),
                MakeCleanItem("windows_logs", "Windows 日志文件", "清理 C:\\Windows\\Logs 目录（清理前自动停止 Windows Update / WaaSMedic 等更新服务以释放被锁定的日志，清理后自动恢复）", RiskLevel.Safe,
                    () => CleanService.CleanWindowsLogs(_logger),
                    () => Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs"))),
                MakeCleanItem("driver_packages", "设备驱动程序包", "仅清理未被任何设备使用的旧驱动包（如历史显卡/声卡残留），绝不删除正在使用的驱动；清理前先检测系统是否正在升级/维护，升级中自动跳过", RiskLevel.Caution, CleanOldDriverPackages, () => !IsSystemUpgrading()),
                MakeCleanItem("thumbnail_cache", "缩略图缓存", "清理 Windows 缩略图缓存文件", RiskLevel.Safe, CleanThumbnailCache),
                MakeCleanItem("inet_cache", "Internet 临时文件", "清理 Internet Explorer/系统 INetCache 缓存", RiskLevel.Safe, CleanInternetCache),
                MakeCleanItem("wer", "Windows 错误报告和反馈", "清理 Windows 错误报告与反馈文件", RiskLevel.Safe,
                    () => CleanService.DeleteDirContents(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER"), _logger),
                    () => Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER"))),
                MakeCleanItem("delivery_optimization", "传递优化文件", "清理 Windows 传递优化与更新下载缓存", RiskLevel.Safe, CleanDeliveryOptimization),
                MakeCleanItem("windows_widgets", "Windows 小组件", "清理 Windows 小组件（WebExperience）缓存", RiskLevel.Safe, CleanWindowsWidgets),
                MakeCleanItem("directx_shader_cache", "DirectX 着色器缓存", "清理 DirectX 着色器缓存（D3DSCache）", RiskLevel.Safe, CleanDirectXShaderCache),
                MakeCleanItem("cert_cache", "Windows 网络证书缓存", "清理用于证书验证的网络缓存（CryptnetUrlCache，如 CRL/OCSP 响应）", RiskLevel.Safe, CleanCertCache),
                MakeCleanItem("prefetch", "预读取文件", "清理 C:\\Windows\\Prefetch 中的预读取缓存（不影响已安装程序运行）", RiskLevel.Safe, CleanPrefetch),
                MakeCleanItem("memory_dump", "内存转储文件", "删除 C:\\Windows\\MEMORY.DMP（系统蓝屏后生成的大型转储，可释放数 GB）", RiskLevel.Safe, CleanMemoryDump),
                MakeCleanItem("store_cache", "应用商店缓存", "清理 Microsoft Store 的本地缓存（LocalCache），不改卸载已装应用", RiskLevel.Safe, CleanStoreCache),
                MakeCleanItem("windows_upgrade_temp", "Windows 升级临时文件", "清理 Windows 大版本升级遗留的临时文件（C:\\$WINDOWS.~BT、$WINDOWS.~WS、$WINDOWS.~Q）。注意：升级后 10 天内删除将无法回滚到旧版本系统！清理前先检测系统是否正在升级/维护，升级中自动跳过", RiskLevel.Caution, CleanWindowsUpgradeTemp, () => !IsSystemUpgrading()),
                MakeCleanItem("defender_history", "Windows Defender 历史扫描", "清理 Windows Defender 扫描历史与隔离区元数据（不删除已隔离的恶意文件本体）", RiskLevel.Safe, CleanDefenderHistory),
                MakeCleanItem("font_cache", "Windows 字体缓存", "清理系统字体缓存服务（FontCache）的临时数据，删除后系统会自动重建", RiskLevel.Safe, CleanFontCache),
                MakeCleanItem("live_kernel_reports", "实时内核报告", "清理 C:\\Windows\\LiveKernelReports 中的实时内核崩溃报告", RiskLevel.Safe, CleanLiveKernelReports),
                MakeCleanItem("icon_cache", "Windows 图标缓存", "清理 %LocalAppData%\\IconCache.db 图标缓存数据库，删除后系统会自动重建图标缓存", RiskLevel.Safe, CleanIconCache)
            });

            var appGroup = new CleanGroup("应用清理");
            appGroup.AddRange(new List<ICleanNode>
            {
                MakeCleanItem("edge_cache", "Edge 缓存", "清理 Microsoft Edge 浏览器缓存", RiskLevel.Safe, CleanEdgeCache),
                MakeCleanItem("edge_update_cache", "Edge 更新安装包缓存", "清理 Microsoft EdgeUpdate 下载的更新安装包残留（Program Files (x86)\\Microsoft\\EdgeUpdate\\Download），属已应用更新的旧安装包，删除后下次更新会重新下载；目录受系统保护，需以管理员身份运行本程序才能实际删除。", RiskLevel.Safe, CleanEdgeUpdateCache),
                MakeCleanItem("edge_bak", "Edge 备份文件", "清理 Edge 浏览器用户数据目录下的自动备份文件（*.bak，如 Bookmarks.bak）。这些是 Edge 每次修改数据时生成的上一版快照备份，删除后会在下次修改时自动重建，不影响当前书签/数据。", RiskLevel.Safe, CleanEdgeBak),
                MakeCleanItem("chrome_cache", "Chrome 缓存", "清理 Google Chrome 浏览器缓存与代码缓存", RiskLevel.Safe, CleanChromeCache),
                MakeCleanItem("firefox_cache", "Firefox 缓存", "清理 Mozilla Firefox 浏览器缓存", RiskLevel.Safe, CleanFirefoxCache),
                MakeCleanItem("brave_cache", "Brave 缓存", "清理 Brave 浏览器缓存与代码缓存", RiskLevel.Safe, CleanBraveCache),
                MakeCleanItem("opera_cache", "Opera 缓存", "清理 Opera 浏览器缓存与代码缓存", RiskLevel.Safe, CleanOperaCache),
                MakeCleanItem("quark_cache", "Quark浏览器缓存", "清理 Quark 浏览器各类可重建缓存：HTTP 磁盘缓存(Cache/Cache_Data)、代码缓存(Code Cache)、GPU 缓存、图片/Blob/远程资源缓存、WebGPU 缓存、Service Worker 缓存等（覆盖默认与多 Profile）。均为纯缓存，删除后浏览器按需重建；清理前建议先关闭 Quark，否则运行中的文件可能被占用无法删除。", RiskLevel.Safe, CleanQuarkCache),
                MakeCleanItem("androws_cef_cache", "Androws 模拟器缓存", "清理腾讯 Androws 安卓模拟器内置 CEF（Chromium 内核）缓存与可重建数据：HTTP 磁盘缓存(Cache)、代码缓存(Code Cache)、GPU 缓存、站点特征数据库(Site Characteristics Database)、图片/Blob/远程资源缓存、WebGPU 缓存、Service Worker 缓存等（覆盖默认与多 Profile）。均为纯缓存，删除后模拟器按需重建；清理前建议先关闭 Androws，否则运行中的文件可能被占用无法删除。", RiskLevel.Safe, CleanAndrowsCefCache),
                MakeCleanItem("github_cache", "GitHub Desktop 缓存", "清理 GitHub Desktop（基于 Electron/Chromium）的缓存目录 `Cache\\Cache_Data`（Chromium 磁盘缓存，含 data_* 与 index）与 `No_Vary_Search` 等。均为纯缓存，删除后程序按需重建；清理前建议先关闭 GitHub Desktop，否则运行中的文件可能被占用无法删除。", RiskLevel.Safe, CleanGitHubDesktopCache),
                MakeCleanItem("ie_cache", "IE 浏览器缓存", "清理 IE 的网页缓存（含临时文件、Cookie 与历史记录），存储于系统 WebCache 数据库。该文件通常被系统进程锁定，直接删除可能因“文件正在使用”而跳过；建议先关闭 IE、重启资源管理器，或通过系统“删除浏览历史记录”清理。删除后系统会按需重建，不影响其它程序。", RiskLevel.Safe, CleanWebCache),
                MakeCleanItem("legacy_edge_cache", "传统 Edge 缓存", "清理传统（EdgeHTML 版）Edge 的网页缓存（含临时文件、Cookie 与历史记录），与 IE 共用系统 WebCache 数据库。该文件通常被系统进程锁定，直接删除可能因“文件正在使用”而跳过；建议先关闭相关程序、重启资源管理器，或通过系统“删除浏览历史记录”清理。删除后系统会按需重建，不影响其它程序。", RiskLevel.Safe, CleanWebCache),
                MakeCleanItem("dingtalk_cache", "钉钉缓存", "清理钉钉缓存目录", RiskLevel.Safe, CleanDingTalkCache),
                MakeCleanItem("netease_cache", "网易云音乐缓存", "清理网易云音乐缓存与 web 数据", RiskLevel.Safe, CleanNetEaseMusicCache),
                MakeCleanItem("steam_cache", "Steam 缓存", "清理 Steam 下载缓存与 HTTP 缓存", RiskLevel.Safe, CleanSteamCache),
                MakeCleanItem("baidu_pan_cache", "百度网盘缓存", "清理百度网盘客户端缓存目录", RiskLevel.Safe, CleanBaiduPanCache),
                MakeCleanItem("thunder_cache", "迅雷缓存", "清理迅雷客户端缓存与种子缓存", RiskLevel.Safe, CleanThunderCache),
                MakeCleanItem("bilibili_cache", "哔哩哔哩缓存", "清理哔哩哔哩客户端缓存", RiskLevel.Safe, CleanBilibiliCache),
                MakeCleanItem("iqiyi_cache", "爱奇艺缓存", "清理爱奇艺客户端缓存", RiskLevel.Safe, CleanIqiyiCache),
                MakeCleanItem("discord_cache", "Discord 缓存", "清理 Discord 客户端缓存与代码缓存", RiskLevel.Safe, CleanDiscordCache),
                MakeCleanItem("browser360_cache", "360 浏览器缓存", "清理 360 极速浏览器缓存与代码缓存", RiskLevel.Safe, CleanBrowser360Cache),
                MakeCleanItem("sogou_cache", "搜狗浏览器缓存", "清理搜狗浏览器缓存", RiskLevel.Safe, CleanSogouCache),
                MakeCleanItem("feishu_cache", "飞书缓存", "清理飞书客户端缓存与用户数据缓存", RiskLevel.Safe, CleanFeishuCache),
                MakeCleanItem("wemeet_cache", "腾讯会议缓存", "清理腾讯会议客户端缓存与录制临时文件", RiskLevel.Safe, CleanWeMeetCache),
                MakeCleanItem("jianying_cache", "剪映缓存", "清理剪映专业版缓存与草稿渲染临时文件", RiskLevel.Safe, CleanJianyingCache),
                MakeCleanItem("telegram_cache", "Telegram 缓存", "清理 Telegram 桌面端缓存数据（保留登录信息）", RiskLevel.Safe, CleanTelegramCache),
                MakeCleanItem("spotify_cache", "Spotify 缓存", "清理 Spotify 客户端离线缓存与存储", RiskLevel.Safe, CleanSpotifyCache),
                MakeCleanItem("adobe_cache", "Adobe 媒体缓存", "清理 Adobe 系列（PR/AE/PS）的媒体缓存文件", RiskLevel.Safe, CleanAdobeCache),
                MakeCleanItem("kuaishou_cache", "快手缓存", "清理快手 PC 客户端缓存", RiskLevel.Safe, CleanKuaishouCache),

                
                MakeCleanItem("wechat_cache", "微信缓存", "仅清理微信 FileStorage\\Cache 临时缓存，绝不触碰聊天记录、聊天图片视频与接收的文件", RiskLevel.Safe, CleanWeChatCache),
                MakeCleanItem("qq_cache", "QQ 缓存", "清理 QQ RichMedia 富媒体缩略图缓存，以及 QQ NT（新版）Chromium 内核缓存（Cache/Code Cache/GPUCache，含 Cache_Data）。均为纯缓存，与聊天数据（Roaming\\Tencent\\QQ\\Storage）隔离，绝不触碰聊天图片视频与接收的文件", RiskLevel.Safe, CleanQQCache),
                MakeCleanItem("wxwork_cache", "企业微信缓存", "仅清理企业微信 Cache 临时缓存，保留聊天记录、图片视频与接收的文件", RiskLevel.Safe, CleanWXWorkCache),
                MakeCleanItem("mailmaster_cache", "网易邮箱大师缓存", "清理网易邮箱大师客户端缓存", RiskLevel.Safe, CleanMailMasterCache),

                
                MakeCleanItem("qq_browser_cache", "QQ浏览器缓存", "清理 QQ 浏览器缓存与代码缓存", RiskLevel.Safe, CleanQQBrowserCache),
                MakeCleanItem("vivaldi_cache", "Vivaldi 缓存", "清理 Vivaldi 浏览器缓存与代码缓存", RiskLevel.Safe, CleanVivaldiCache),

                
                MakeCleanItem("qq_music_cache", "QQ音乐缓存", "清理 QQ 音乐客户端缓存", RiskLevel.Safe, CleanQQMusicCache),
                MakeCleanItem("kugou_cache", "酷狗音乐缓存", "清理酷狗音乐客户端缓存", RiskLevel.Safe, CleanKugouCache),
                MakeCleanItem("kuwo_cache", "酷我音乐缓存", "清理酷我音乐客户端缓存", RiskLevel.Safe, CleanKuwoCache),
                MakeCleanItem("qqlive_cache", "腾讯视频缓存", "清理腾讯视频客户端缓存", RiskLevel.Safe, CleanQQLiveCache),
                MakeCleanItem("youku_cache", "优酷缓存", "清理优酷客户端缓存", RiskLevel.Safe, CleanYoukuCache),

                
                MakeCleanItem("wps_cache", "WPS 缓存", "清理 WPS Office 备份与缓存文件", RiskLevel.Safe, CleanWPSCache),
                MakeCleanItem("epic_cache", "Epic Games Launcher 缓存", "清理 Epic Games 启动器缓存（保留已下载游戏库）", RiskLevel.Safe, CleanEpicCache),

                
                MakeCleanItem("vscode_cache", "VSCode 缓存", "清理 Visual Studio Code 缓存、CachedData 与 Code Cache", RiskLevel.Safe, CleanVSCodeCache),
                MakeCleanItem("jetbrains_cache", "JetBrains 系列缓存", "清理 IDEA/PyCharm/WebStorm/Android Studio 等的 caches、log、tmp 目录（保留配置与项目）", RiskLevel.Safe, CleanJetBrainsCache),
                MakeCleanItem("vs_component_cache", "Visual Studio 组件缓存", "清理 Visual Studio ComponentModelCache 与 IDE 缓存（不影响已建项目）", RiskLevel.Safe, CleanVSComponentCache),
                MakeCleanItem("npm_cache", "npm 缓存", "清理 npm 包管理器缓存（C:\\Users\\{user}\\AppData\\Local\\npm-cache），下次 install 会重新下载", RiskLevel.Safe, CleanNpmCache),
                MakeCleanItem("pip_cache", "pip 缓存", "清理 Python pip 包管理器缓存（C:\\Users\\{user}\\AppData\\Local\\pip\\cache）", RiskLevel.Safe, CleanPipCache),
                MakeCleanItem("maven_cache", "Maven 本地仓库", "清理 Maven 本地依赖仓库（~/.m2/repository），下次构建会重新下载依赖", RiskLevel.Caution, CleanMavenCache),
                MakeCleanItem("gradle_cache", "Gradle 缓存", "清理 Gradle 构建缓存（~/.gradle/caches），下次构建会重新下载依赖", RiskLevel.Caution, CleanGradleCache),
                MakeCleanItem("nuget_cache", "NuGet 缓存", "清理 NuGet 包管理器 HTTP 缓存（不删除已安装包，仅删下载临时缓存）", RiskLevel.Safe, CleanNuGetCache)
            });

            
            
            var appLogGroup = new CleanGroup("应用日志清理");
            appLogGroup.AddRange(new List<ICleanNode>
            {
                MakeCleanItem("edge_logs", "Edge 日志", "清理 Microsoft Edge 运行日志与崩溃报告（Crashpad）", RiskLevel.Safe, () => CleanAppLog("edge_logs")),
                MakeCleanItem("chrome_logs", "Chrome 日志", "清理 Google Chrome 运行日志与崩溃报告（Crashpad）", RiskLevel.Safe, () => CleanAppLog("chrome_logs")),
                MakeCleanItem("firefox_logs", "Firefox 日志", "清理 Mozilla Firefox 崩溃报告", RiskLevel.Safe, () => CleanAppLog("firefox_logs")),
                MakeCleanItem("brave_logs", "Brave 日志", "清理 Brave 运行日志与崩溃报告（Crashpad）", RiskLevel.Safe, () => CleanAppLog("brave_logs")),
                MakeCleanItem("opera_logs", "Opera 日志", "清理 Opera / Opera GX 崩溃报告（Crashpad）", RiskLevel.Safe, () => CleanAppLog("opera_logs")),
                MakeCleanItem("browser360_logs", "360 浏览器日志", "清理 360 极速浏览器崩溃报告（Crashpad）", RiskLevel.Safe, () => CleanAppLog("browser360_logs")),
                MakeCleanItem("sogou_logs", "搜狗浏览器日志", "清理搜狗浏览器日志与崩溃报告", RiskLevel.Safe, () => CleanAppLog("sogou_logs")),
                MakeCleanItem("qqbrowser_logs", "QQ浏览器日志", "清理 QQ 浏览器崩溃报告（Crashpad）", RiskLevel.Safe, () => CleanAppLog("qqbrowser_logs")),
                MakeCleanItem("vivaldi_logs", "Vivaldi 日志", "清理 Vivaldi 崩溃报告（Crashpad）", RiskLevel.Safe, () => CleanAppLog("vivaldi_logs")),
                MakeCleanItem("quark_logs", "Quark浏览器日志", "清理 Quark 浏览器日志与崩溃报告", RiskLevel.Safe, () => CleanAppLog("quark_logs")),
                MakeCleanItem("wechat_logs", "微信日志", "清理微信诊断日志（xlog），不触碰聊天记录/图片/文件", RiskLevel.Safe, () => CleanAppLog("wechat_logs")),
                MakeCleanItem("androws_logs", "Androws 模拟器日志", "清理腾讯 Androws 安卓模拟器运行日志（ams_cache/log 下的 AmsWinSdk_*.log 等），仅删日志、不碰模拟器配置与缓存", RiskLevel.Safe, () => CleanAppLog("androws_logs")),
                MakeCleanItem("qq_logs", "QQ 日志", "清理 QQ 运行日志（Logs / NTQQ\\log），不触碰聊天数据", RiskLevel.Safe, () => CleanAppLog("qq_logs")),
                MakeCleanItem("wxwork_logs", "企业微信日志", "清理企业微信日志（Logs），不触碰聊天记录", RiskLevel.Safe, () => CleanAppLog("wxwork_logs")),
                MakeCleanItem("dingtalk_logs", "钉钉日志", "清理钉钉运行日志", RiskLevel.Safe, () => CleanAppLog("dingtalk_logs")),
                MakeCleanItem("feishu_logs", "飞书日志", "清理飞书运行日志", RiskLevel.Safe, () => CleanAppLog("feishu_logs")),
                MakeCleanItem("discord_logs", "Discord 日志", "清理 Discord 日志", RiskLevel.Safe, () => CleanAppLog("discord_logs")),
                MakeCleanItem("telegram_logs", "Telegram 日志", "清理 Telegram 桌面端日志", RiskLevel.Safe, () => CleanAppLog("telegram_logs")),
                MakeCleanItem("mailmaster_logs", "网易邮箱大师日志", "清理网易邮箱大师日志", RiskLevel.Safe, () => CleanAppLog("mailmaster_logs")),
                MakeCleanItem("vscode_logs", "VSCode 日志", "清理 Visual Studio Code 日志（%AppData%\\Code\\logs）", RiskLevel.Safe, () => CleanAppLog("vscode_logs")),
                MakeCleanItem("npm_logs", "npm 日志", "清理 npm 安装/报错日志（~\\.npm\\_logs）", RiskLevel.Safe, () => CleanAppLog("npm_logs")),
                MakeCleanItem("netease_logs", "网易云音乐日志", "清理网易云音乐运行日志", RiskLevel.Safe, () => CleanAppLog("netease_logs")),
                MakeCleanItem("qqmusic_logs", "QQ音乐日志", "清理 QQ 音乐运行日志", RiskLevel.Safe, () => CleanAppLog("qqmusic_logs")),
                MakeCleanItem("kugou_logs", "酷狗音乐日志", "清理酷狗音乐运行日志", RiskLevel.Safe, () => CleanAppLog("kugou_logs")),
                MakeCleanItem("kuwo_logs", "酷我音乐日志", "清理酷我音乐运行日志", RiskLevel.Safe, () => CleanAppLog("kuwo_logs")),
                MakeCleanItem("qqlive_logs", "腾讯视频日志", "清理腾讯视频运行日志", RiskLevel.Safe, () => CleanAppLog("qqlive_logs")),
                MakeCleanItem("youku_logs", "优酷日志", "清理优酷运行日志", RiskLevel.Safe, () => CleanAppLog("youku_logs")),
                MakeCleanItem("iqiyi_logs", "爱奇艺日志", "清理爱奇艺运行日志", RiskLevel.Safe, () => CleanAppLog("iqiyi_logs")),
                MakeCleanItem("bilibili_logs", "哔哩哔哩日志", "清理哔哩哔哩运行日志", RiskLevel.Safe, () => CleanAppLog("bilibili_logs")),
                MakeCleanItem("kuaishou_logs", "快手日志", "清理快手运行日志", RiskLevel.Safe, () => CleanAppLog("kuaishou_logs")),
                MakeCleanItem("thunder_logs", "迅雷日志", "清理迅雷运行日志", RiskLevel.Safe, () => CleanAppLog("thunder_logs")),
                MakeCleanItem("baidupan_logs", "百度网盘日志", "清理百度网盘客户端日志", RiskLevel.Safe, () => CleanAppLog("baidupan_logs")),
                MakeCleanItem("steam_logs", "Steam 日志", "清理 Steam 运行日志（logs / log）", RiskLevel.Safe, () => CleanAppLog("steam_logs")),
                MakeCleanItem("spotify_logs", "Spotify 日志", "清理 Spotify 运行日志", RiskLevel.Safe, () => CleanAppLog("spotify_logs")),
                MakeCleanItem("wemeet_logs", "腾讯会议日志", "清理腾讯会议运行日志", RiskLevel.Safe, () => CleanAppLog("wemeet_logs")),
                MakeCleanItem("jianying_logs", "剪映日志", "清理剪映专业版运行日志", RiskLevel.Safe, () => CleanAppLog("jianying_logs")),
                MakeCleanItem("epic_logs", "Epic 日志", "清理 Epic Games Launcher 运行日志（Saved\\Logs）", RiskLevel.Safe, () => CleanAppLog("epic_logs"))
            });

            var usageTracesGroup = new CleanGroup("电脑使用痕迹");
            usageTracesGroup.AddRange(new List<ICleanNode>
            {
                MakeCleanItem("etw_logs", "Windows 事件跟踪日志", "清理 Windows 事件跟踪（ETL）实时日志缓冲", RiskLevel.Safe, CleanEtwLogs),
                MakeCleanItem("recent_items", "Windows 最近访问", "清理“最近使用的项目”快捷方式与跳转列表", RiskLevel.Safe, CleanRecentItems),
                MakeCleanItem("clipboard_history", "Windows 剪贴板", "清理 Windows 剪贴板历史记录", RiskLevel.Safe, CleanClipboardHistory),
                MakeCleanItem("explorer_traces", "资源管理器使用痕迹", "清理 HKCU\\Explorer\\ 下的纯使用历史：最近文档(RecentDocs)、运行框历史(RunMRU)、地址栏键入路径(TypedPaths)、程序使用统计(UserAssist)、打开/保存对话框历史(ComDlg32\\OpenSaveMRU 等)。属纯痕迹，清空后系统自动重建，不影响任何功能。", RiskLevel.Safe, CleanExplorerTraces)
            });

            var registryCleanGroup = new CleanGroup("注册表清理");
            var invalidStartupItem = MakeCleanItem("invalid_startup", "无效启动项",
                "清理“开机启动”里指向已卸载程序的残留项，覆盖三处：① 注册表 Run/RunOnce（exe 已不存在）；② 启动文件夹（shell:startup / shell:common startup 中指向已删除程序的 .lnk 快捷方式，直接删除）；③ 任务计划程序（动作命令指向已删除 exe 的计划任务，删除前自动备份 XML）。仅删除失效的启动项，不影响任何仍有效的启动项。",
                RiskLevel.Safe, CleanInvalidStartup);
            var invalidComItem = MakeCleanItem("invalid_com", "失效 COM/ActiveX 组件",
                "清理类标识符（CLSID）中服务器文件（DLL/EXE）已被删除的失效组件。仅当服务器文件经校验确实不存在时才删除该 CLSID，不触碰仍在使用的组件。",
                RiskLevel.Safe, CleanInvalidCom);
            var invalidUninstallItem = MakeCleanItem("invalid_uninstall", "孤立卸载残留",
                "清理“程序卸载”信息中卸载程序已丢失的残留项（软件已删但注册表残留）。仅当卸载程序文件确实不存在时才删除，不影响可正常卸载的软件。",
                RiskLevel.Safe, CleanInvalidUninstall);
            var invalidContextMenuItem = MakeCleanItem("invalid_context_menu", "无效右键菜单",
                "清理文件/文件夹/桌面右键菜单中指向已卸载程序（exe/dll 已不存在）的失效项，包括 shell 动词与 Shell 扩展（ContextMenuHandlers）。仅删除服务器文件经校验确实不存在的项，不影响任何仍有效的右键菜单。",
                RiskLevel.Safe, CleanInvalidContextMenu);
            registryCleanGroup.AddRange(new List<ICleanNode> { invalidStartupItem, invalidComItem, invalidUninstallItem, invalidContextMenuItem });

            
            var invalidFileAssociationItem = MakeCleanItem("invalid_file_association", "无效文件关联",
                "清理文件打开方式与程序关联中的 5 类残留：① HKCR\\Applications（HKLM/HKCU\\Software\\Classes\\Applications）下命令指向已删除 exe 的已注册应用；② 各文件扩展名 HKCR\\.ext（.txt/.jpg/.mp3 等）及其 OpenWithProgids/OpenWithList 中失效的 ProgID/打开程序；③ HKCU/HKLM\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts\\.ext\\OpenWithProgids 与 OpenWithList 中的失效项；④ HKCU/HKLM\\SOFTWARE\\RegisteredApplications 中指向已删除程序的功能声明；⑤ HKCU/HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths 中指向已删除 exe 的路径项。仅验证确实不存在后才删除，不影响任何仍有效的关联。",
                RiskLevel.Safe, CleanInvalidFileAssociation);
            registryCleanGroup.AddRange(new[] { invalidFileAssociationItem });

            
            var invalidSharedDllsItem = MakeCleanItem("invalid_shared_dlls", "失效共享 DLL 引用",
                "清理 SharedDLLs 中指向已删除 DLL 的引用计数。这些计数只用于卸载时判断 DLL 是否可安全删除，文件已不存在时是纯残留，删除不影响任何仍在使用的程序。",
                RiskLevel.Safe, CleanInvalidSharedDlls);

            
            var invalidMuiCacheItem = MakeCleanItem("invalid_mui_cache", "失效 MUI 程序名缓存",
                "清理 Windows Shell MuiCache 中指向已删除 exe 的程序名/厂商名缓存（覆盖 64 位与 32 位 WOW64 程序两套缓存）。属纯历史缓存，删除后系统会按需重建，不影响任何程序运行。",
                RiskLevel.Safe, CleanInvalidMuiCache);

            
            var invalidAppCompatLayersItem = MakeCleanItem("invalid_appcompat_layers", "失效兼容性设置",
                "清理 AppCompatFlags\\Layers 中为已删除 exe 保留的兼容性设置（如以管理员运行、禁用 DPI 缩放等）。文件已不存在时该设置为纯残留，删除后不影响现有程序。",
                RiskLevel.Safe, CleanInvalidAppCompatLayers);

            
            var invalidAppCompatStoreItem = MakeCleanItem("invalid_appcompat_store", "失效兼容性助手记录",
                "清理 AppCompatFlags\\Compatibility Assistant\\Store 中指向已删除 exe 的兼容性助手(PCA)运行记录。这是 Windows 记录“曾运行过哪些程序”的内部台账，程序卸载/删除后不会自动清理；文件已不存在的记录为纯残留，删除后不影响任何程序，重装并运行会由系统自动重建。",
                RiskLevel.Safe, CleanInvalidCompatStore);

            
            var invalidFirewallRuleItem = MakeCleanItem("invalid_firewall_rule", "失效防火墙规则",
                "清理 Windows 防火墙中指向已卸载程序(App=路径不存在)的孤儿规则。这类规则是软件卸载后残留、对网络无任何实际作用的失效项，删除不影响仍在使用的程序或系统自带规则（写入 HKLM 需管理员权限，非管理员时自动跳过）。",
                RiskLevel.Safe, CleanInvalidFirewallRules);

            registryCleanGroup.AddRange(new[] { invalidSharedDllsItem, invalidMuiCacheItem, invalidAppCompatLayersItem, invalidAppCompatStoreItem, invalidFirewallRuleItem });

            var recycleGroup = new CleanGroup("回收站");
            var emptyRecycleItem = MakeCleanItem("empty_recycle_bin", "清空回收站", "⚠ 将清空【所有磁盘】的回收站，包括您手动删除的文件，不可恢复。其余清理项现在直接删除（不再移入回收站）。仅当您确认不再需要回收站内任何内容时，才勾选此项。", RiskLevel.Caution, () => CleanService.EmptyRecycleBin(_logger));
            emptyRecycleItem.IsChecked = false; 
            recycleGroup.AddRange(new[] { emptyRecycleItem }); 

            CleanGroups = new ObservableCollection<CleanGroup> { systemGroup, appGroup, appLogGroup, usageTracesGroup, registryCleanGroup, recycleGroup };

            WireBrowserKillButtons();
        }

        // 浏览器/客户端类缓存项：关联其进程名，运行时在卡片上显示“关闭并清理”按钮。
        // 点击按钮 → 杀掉关联进程 → 再执行本项清理（解决“程序开着导致缓存文件被占用删不掉”的问题）。
        // 范围：浏览器 + 用户近期涉及的 Quark/Androws/GitHub Desktop 等；其余客户端（微信/QQ/飞书…）保持 best-effort 删除，避免误杀带用户数据的进程。
        // 注意：ie_cache / legacy_edge_cache 共用 WebCacheV01.dat，由系统进程(dllhost/explorer)锁定；
        // 杀 IE/Edge 浏览器进程解不开该锁，而杀系统进程违反项目安全约定（项目说明 L385/L1394），故两者均不加关闭按钮。
        private static readonly Dictionary<string, string[]> BrowserProcessNames = new()
        {
            ["edge_cache"] = new[] { "msedge" },
            ["chrome_cache"] = new[] { "chrome" },
            ["firefox_cache"] = new[] { "firefox" },
            ["brave_cache"] = new[] { "brave" },
            ["opera_cache"] = new[] { "opera" },
            ["quark_cache"] = new[] { "Quark" },
            ["androws_cef_cache"] = new[] { "Androws" },
            ["github_cache"] = new[] { "GitHubDesktop" },
            ["browser360_cache"] = new[] { "360Chrome" },
            ["sogou_cache"] = new[] { "SogouExplorer", "SogouBrowser" },
            ["qq_browser_cache"] = new[] { "QQBrowser" },
            ["vivaldi_cache"] = new[] { "vivaldi" },
        };

        // 为浏览器类缓存项注入进程名与关闭命令，并刷新一次运行状态。
        private void WireBrowserKillButtons()
        {
            foreach (var grp in CleanGroups)
                foreach (var node in grp.Children)
                    if (node is CleanItem ci && BrowserProcessNames.TryGetValue(ci.Id, out var procs))
                    {
                        ci.RelatedProcessNames = procs;
                        ci.KillAndCleanCommand = KillBrowserAndCleanCommand;
                    }
            RefreshBrowserProcessStates();
        }

        // 刷新所有已配置进程名的清理项的“是否运行中”状态。
        private void RefreshBrowserProcessStates()
        {
            foreach (var grp in CleanGroups)
                foreach (var node in grp.Children)
                    if (node is CleanItem ci && ci.RelatedProcessNames?.Length > 0)
                        ci.RefreshProcessState();
        }

        private RelayCommand _killBrowserAndCleanCommand;
        public RelayCommand KillBrowserAndCleanCommand => _killBrowserAndCleanCommand ??= new RelayCommand(obj =>
        {
            var item = obj as CleanItem;
            KillBrowserAndClean(item);
        });

        // 点击「查看失败详情」：弹出独立窗口展示说明 / 将清理位置 / 失败明细（替代 ToolTip 内无法滚动的长列表）。
        private static RelayCommand _showFailureDetailsCommand;
        public static RelayCommand ShowFailureDetailsCommand => _showFailureDetailsCommand ??= new RelayCommand(obj =>
        {
            if (obj is CleanItem item)
            {
                var win = new FailureDetailsWindow(item)
                {
                    Owner = Application.Current?.MainWindow
                };
                win.Show();
            }
        });

        // 点击“关闭并清理”按钮：先确认（避免误关丢失未保存内容），再杀进程后清理本项。
        private void KillBrowserAndClean(CleanItem item)
        {
            if (item == null || !item.CanKillAndClean) return;
            if (item.Status == ScanStatus.Cleaning) return;

            var confirm = MessageBox.Show(
                $"即将关闭“{item.Name}”对应的进程以清理其缓存，未保存的内容可能丢失。确定继续？",
                "关闭程序并清理", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            _ = KillBrowserAndCleanAsync(item);
        }

        private async Task KillBrowserAndCleanAsync(CleanItem item)
        {
            if (IsBusy) return;
            SetIsBusy(true);
            try
            {
                // 1) 杀掉关联进程
                await Task.Run(() =>
                {
                    foreach (var name in item.RelatedProcessNames ?? Array.Empty<string>())
                    {
                        try
                        {
                            foreach (var p in Process.GetProcessesByName(name))
                            {
                                try { if (!p.HasExited) p.Kill(); } catch { }
                            }
                        }
                        catch { }
                    }
                });

                // 2) 等待文件句柄释放
                await Task.Delay(600);

                // 3) 刷新运行状态（按钮应隐藏）
                item.RefreshProcessState();

                // 4) 执行本项清理
                await Task.Run(() => CleanSingleItem(item));

                // 5) 再次刷新（进程可能自启，按钮按实情显示）
                item.RefreshProcessState();
            }
            catch (Exception ex)
            {
                _logger?.Error($"关闭并清理失败: {item.Name}", ex);
            }
            finally
            {
                SetIsBusy(false);
            }
        }

        // 单条清理项的执行逻辑（主流程并行循环与“关闭并清理”命令共用）。
        private void CleanSingleItem(CleanItem item)
        {
            try
            {
                _uiDispatcher?.Invoke(() =>
                {
                    item.Status = ScanStatus.Cleaning;
                    CleanProgressText = $"正在清理：{item.Name}";
                });

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

                _uiDispatcher?.Invoke(() =>
                {
                    if (result.FullySucceeded || (result.NoOperation && !hasResultAware))
                    {
                        item.Status = ScanStatus.Cleaned;
                        item.ErrorMessage = null;
                        item.Details = null;
                    }
                    else if (result.PartiallySucceeded)
                    {
                        item.Status = ScanStatus.PartiallyCleaned;
                        item.ErrorMessage = result.ToString();
                        item.Details = result.GetFailureDetails();
                    }
                    else if (result.FullyFailed)
                    {
                        item.Status = ScanStatus.Error;
                        item.ErrorMessage = result.ToString();
                        item.Details = result.GetFailureDetails();
                        item.IsChecked = false;
                    }
                    else if (result.NoOperation)
                    {
                        item.Status = ScanStatus.Cleaned;
                        item.ErrorMessage = null;
                        item.Details = null;
                    }
                    else
                    {
                        item.Status = ScanStatus.Cleaned;
                        item.ErrorMessage = null;
                        item.Details = null;
                    }

                    CleanFileCount++;
                    CleanProgressText = item.Status == ScanStatus.Cleaned ? $"已清理：{item.Name}"
                        : item.Status == ScanStatus.PartiallyCleaned ? $"部分清理：{item.Name}"
                        : $"失败：{item.Name}";
                });
            }
            catch (Exception ex)
            {
                _uiDispatcher?.Invoke(() =>
                {
                    item.ErrorMessage = ex.Message;
                    item.Details = null;
                    item.Status = ScanStatus.Error;
                    item.IsChecked = false;
                    CleanFileCount++;
                    CleanProgressText = $"失败：{item.Name}";
                });
                _logger.Error($"清理失败: {item.Name}", ex);
            }
        }

        // canClean 缺省统一为「始终可清理」(() => true)，仅少数项（系统升级中、目录存在性等）传入自定义谓词
        private static CleanItem MakeCleanItem(string id, string name, string desc, RiskLevel risk, Func<CleanService.CleanResult> clean, Func<bool> canClean = null)
        {
            canClean ??= () => true;
            var item = new CleanItem
            {
                Id = id,
                Name = name,
                Description = desc,
                Risk = risk,
                CanClean = canClean,
                IsEnabled = risk != RiskLevel.Forbidden,
                IsChecked = false 
            };
            
            item.CleanActionWithResult = clean;
            item.ShowFailureDetailsCommand = ShowFailureDetailsCommand;
            return item;
        }

        
        
        
        
        
        private static CleanItem MakeCleanItem(string id, string name, string desc, RiskLevel risk, Action clean, Func<bool> canClean = null)
        {
            canClean ??= () => true;
            var item = new CleanItem
            {
                Id = id,
                Name = name,
                Description = desc,
                Risk = risk,
                CanClean = canClean,
                IsEnabled = risk != RiskLevel.Forbidden,
                IsChecked = false 
            };
            item.CleanAction = clean;
            item.CleanActionWithResult = null;
            item.ShowFailureDetailsCommand = ShowFailureDetailsCommand;
            return item;
        }

        #region 各清理项「将清理的顶层目录 / 注册表项」映射（供 ⓘ ToolTip 展示）

        private static Dictionary<string, string[]> _targetRoots;
        private static Dictionary<string, string[]> TargetRoots
        {
            get
            {
                if (_targetRoots == null) _targetRoots = BuildTargetRoots();
                return _targetRoots;
            }
        }

        // 返回该清理项「将清理的顶层目录 / 注册表项」清单（显示用，非逐文件枚举）。
        private static List<string> GetItemTargetRoots(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return TargetRoots.TryGetValue(id, out var arr) && arr != null && arr.Length > 0
                ? new List<string>(arr) : null;
        }

        private static Dictionary<string, string[]> BuildTargetRoots()
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var temp = Path.GetTempPath();
            var sysDrive = Path.GetPathRoot(win) ?? "C:\\";
            var dict = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            // 应用日志类（直接复用 AppLogDirectories 映射）
            foreach (var kvp in AppLogDirectories())
                dict[kvp.Key] = kvp.Value;

            // —— 系统清理 ——
            dict["temp_files"] = new[] { Path.Combine(win, "Temp"), temp };
            dict["minidumps"] = new[] { Path.Combine(win, "Minidump"), Path.Combine(win, "*.dmp") };
            dict["windows_logs"] = new[] { Path.Combine(win, "Logs") };
            dict["driver_packages"] = new[] { "孤立的旧设备驱动包（oem*.inf，通过 pnputil /delete-driver 卸载，不影响在用驱动）" };
            dict["thumbnail_cache"] = new[] { Path.Combine(local, @"Microsoft\Windows\Explorer") + "（thumbcache_*.db / iconcache_*.db）" };
            dict["inet_cache"] = new[] { Path.Combine(local, @"Microsoft\Windows\INetCache") };
            dict["wer"] = new[] { Path.Combine(common, @"Microsoft\Windows\WER") };
            dict["delivery_optimization"] = new[] { Path.Combine(win, @"SoftwareDistribution\DeliveryOptimization"), Path.Combine(win, @"SoftwareDistribution\Download") };
            dict["windows_widgets"] = new[] { Path.Combine(local, @"Packages\MicrosoftWindows.Client.WebExperience_cw5n1h2txyewy\LocalCache") };
            dict["directx_shader_cache"] = new[] { Path.Combine(local, "D3DSCache") };
            dict["cert_cache"] = new[] { Path.Combine(local, @"Microsoft\CryptnetUrlCache") };
            dict["prefetch"] = new[] { Path.Combine(win, "Prefetch") };
            dict["memory_dump"] = new[] { Path.Combine(win, "MEMORY.DMP") };
            dict["store_cache"] = new[] { Path.Combine(local, "Packages") + "（仅 Microsoft.WindowsStore* 的 LocalCache）" };
            dict["windows_upgrade_temp"] = new[] { Path.Combine(sysDrive, "$WINDOWS.~BT"), Path.Combine(sysDrive, "$WINDOWS.~WS"), Path.Combine(sysDrive, "$WINDOWS.~Q") };
            dict["defender_history"] = new[] { Path.Combine(common, @"Microsoft\Windows Defender\Scans\History") };
            dict["font_cache"] = new[] { Path.Combine(win, @"ServiceProfiles\LocalService\AppData\Local\FontCache") };
            dict["live_kernel_reports"] = new[] { Path.Combine(win, "LiveKernelReports") };
            dict["icon_cache"] = new[] { Path.Combine(local, "IconCache.db") };

            // —— 浏览器 / 应用缓存 ——
            dict["edge_cache"] = new[] { Path.Combine(local, @"Microsoft\Edge\User Data") + "（Cache / Code Cache）" };
            dict["edge_update_cache"] = new[] { Path.Combine(x86, @"Microsoft\EdgeUpdate\Download") };
            dict["edge_bak"] = new[] { Path.Combine(local, @"Microsoft\Edge\User Data") + "（*.bak 备份文件）" };
            dict["chrome_cache"] = new[] { Path.Combine(local, @"Google\Chrome\User Data") + "（Cache / Code Cache / Media Cache）" };
            dict["firefox_cache"] = new[] { Path.Combine(local, @"Mozilla\Firefox\Profiles") + "（各 Profile 的 cache2 / thumbnails / cache）" };
            dict["brave_cache"] = new[] { Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data") + "（Cache / Code Cache）" };
            dict["opera_cache"] = new[] { Path.Combine(local, @"Opera Software") + "（各浏览器目录的 Cache / Code Cache）" };
            dict["quark_cache"] = new[] {
                Path.Combine(local, @"Quark\User Data"), Path.Combine(local, @"Quark\Quark\User Data"),
                Path.Combine(local, @"夸克浏览器\User Data"), Path.Combine(local, @"Quark\Application\Cache") };
            dict["androws_cef_cache"] = new[] { Path.Combine(roaming, @"Tencent\Androws\cef\CEF_Androws") };
            dict["legacy_edge_cache"] = new[] { Path.Combine(local, @"Microsoft\Windows\WebCache") };
            dict["ie_cache"] = new[] { Path.Combine(local, @"Microsoft\Windows\WebCache") };
            dict["github_cache"] = new[] { Path.Combine(roaming, @"GitHub Desktop\Cache") + "（Cache_Data / No_Vary_Search）" };
            dict["dingtalk_cache"] = new[] { Path.Combine(local, @"DingTalk\Cache"), Path.Combine(local, @"DingTalk\update") };
            dict["netease_cache"] = new[] { Path.Combine(local, @"NetEase\CloudMusic\Cache") };
            dict["steam_cache"] = new[] { Path.Combine(x86, @"Steam\appcache"), Path.Combine(x86, @"Steam\httpcache"), Path.Combine(x86, @"Steam\config\htmlcache") };
            dict["baidu_pan_cache"] = new[] { Path.Combine(local, @"Baidu\BaiduNetdisk\cache"), Path.Combine(local, @"Baidu\BaiduNetdisk\tmp") };
            dict["thunder_cache"] = new[] { Path.Combine(local, @"Thunder Network\Thunder\Profiles\Cache"), Path.Combine(local, @"Thunder Network\XLLiveUD\Cache") };
            dict["bilibili_cache"] = new[] { Path.Combine(local, @"bilibili\Cache"), Path.Combine(local, @"Bilibili\Cache") };
            dict["iqiyi_cache"] = new[] { Path.Combine(local, @"Qiyi\QiyiClient\cache"), Path.Combine(local, @"Qiyi\QiyiClient\webcache") };
            dict["discord_cache"] = new[] { Path.Combine(local, @"Discord\Cache"), Path.Combine(local, @"Discord\Code Cache"), Path.Combine(local, @"Discord\GPUCache") };
            dict["browser360_cache"] = new[] { Path.Combine(local, @"360Chrome\Chrome\User Data") + "（Cache / Code Cache）" };
            dict["sogou_cache"] = new[] { Path.Combine(local, @"SogouExplorer\Cache"), Path.Combine(local, @"SogouExplorer\WebkitCache") };
            dict["feishu_cache"] = new[] { Path.Combine(local, @"Feishu\Cache"), Path.Combine(local, @"Feishu\Application\Cache"), Path.Combine(local, @"Feishu\user_data\cache") };
            dict["wemeet_cache"] = new[] { Path.Combine(local, @"Tencent\WeMeet\Cache"), Path.Combine(local, @"Tencent\WeMeet\AppCache") };
            dict["jianying_cache"] = new[] { Path.Combine(local, @"JianyingPro\Cache"), Path.Combine(local, @"JianyingPro\User Data\Cache"), Path.Combine(local, @"JianyingPro\User Data\GPUCache") };
            dict["telegram_cache"] = new[] { Path.Combine(roaming, @"Telegram Desktop\data\cache"), Path.Combine(roaming, @"Telegram Desktop\data\media_cache") };
            dict["spotify_cache"] = new[] { Path.Combine(local, @"Spotify\cache"), Path.Combine(roaming, @"Spotify\Storage") };
            dict["adobe_cache"] = new[] { Path.Combine(local, @"Adobe\Common\Media Cache Files"), Path.Combine(local, @"Adobe\Common\Media Cache"), Path.Combine(local, @"Adobe\Premiere Pro") + "（各版本 Media Cache Files）" };
            dict["kuaishou_cache"] = new[] { Path.Combine(local, @"Kuaishou\Cache"), Path.Combine(local, @"Kuaishou\Application\Cache") };
            dict["wechat_cache"] = new[] { Path.Combine(user, @"Documents\WeChat Files") + @"（各账号 FileStorage\Cache）", Path.Combine(local, @"Tencent\WeChat") + @"（各账号 FileStorage\Cache）" };
            dict["qq_cache"] = new[] { Path.Combine(user, @"Documents\Tencent Files") + "（各账号 RichMedia）", Path.Combine(local, @"Tencent\QQ\Cache"), Path.Combine(local, @"Tencent\QQ\Code Cache"), Path.Combine(local, @"Tencent\QQ\GPUCache") };
            dict["wxwork_cache"] = new[] { Path.Combine(user, @"Documents\WXWork") + "（各账号 Cache）" };
            dict["mailmaster_cache"] = new[] { Path.Combine(local, @"MailMaster\Cache"), Path.Combine(local, @"MailMaster\Application\Cache") };
            dict["qq_browser_cache"] = new[] { Path.Combine(local, @"Tencent\QQBrowser\User Data") + "（Cache / Code Cache）" };
            dict["vivaldi_cache"] = new[] { Path.Combine(local, @"Vivaldi\User Data") + "（Cache / Code Cache）" };
            dict["qq_music_cache"] = new[] { Path.Combine(local, @"Tencent\QQMusic\Cache"), Path.Combine(local, @"QQMusic\Cache") };
            dict["kugou_cache"] = new[] { Path.Combine(local, @"KuGou\Cache"), Path.Combine(local, @"KuGou8\Cache") };
            dict["kuwo_cache"] = new[] { Path.Combine(local, @"KuwoMusic\Cache"), Path.Combine(local, @"Kuwo\Cache") };
            dict["qqlive_cache"] = new[] { Path.Combine(local, @"Tencent\QQLive\Cache"), Path.Combine(local, @"QQLive\Cache") };
            dict["youku_cache"] = new[] { Path.Combine(local, @"Youku\YoukuClient\cache"), Path.Combine(local, @"Youku\cache") };
            dict["wps_cache"] = new[] { Path.Combine(local, @"Kingsoft\office6\cache"), Path.Combine(local, @"WPS\cache") };
            dict["epic_cache"] = new[] { Path.Combine(local, @"Epic\EpicGamesLauncher\Data\EMS\cache"), Path.Combine(local, @"Epic\EpicGamesLauncher\Data\EMS\EPIC"), Path.Combine(local, @"Epic\EpicGamesLauncher\WebCache") };
            dict["vscode_cache"] = new[] { Path.Combine(roaming, @"Code\Cache"), Path.Combine(roaming, @"Code\CachedData"), Path.Combine(roaming, @"Code\Code Cache"), Path.Combine(roaming, @"Code\GPUCache") };
            dict["jetbrains_cache"] = new[] { Path.Combine(local, @"JetBrains") + "（各产品的 caches / log / tmp / index）", Path.Combine(local, @"Google\AndroidStudio") + "（各版本 caches / log / tmp / index）" };
            dict["vs_component_cache"] = new[] { Path.Combine(local, @"Microsoft\VisualStudio") + "（各版本 ComponentModelCache / MEFCache）" };
            dict["npm_cache"] = new[] { Path.Combine(local, "npm-cache") };
            dict["pip_cache"] = new[] { Path.Combine(local, @"pip\cache") };
            dict["maven_cache"] = new[] { Path.Combine(user, @".m2\repository") };
            dict["gradle_cache"] = new[] { Path.Combine(user, @".gradle\caches") };
            dict["nuget_cache"] = new[] { Path.Combine(local, @"NuGet\v3-cache"), Path.Combine(local, @"NuGet\Cache") };

            // —— Windows 使用痕迹 ——
            dict["etw_logs"] = new[] { Path.Combine(win, @"System32\LogFiles\WMI\RtBackup"), Path.Combine(win, @"Logs\MeasuredBoot") };
            dict["recent_items"] = new[] { Path.Combine(user, "Recent"), Path.Combine(roaming, @"Microsoft\Windows\Recent\AutomaticDestinations"), Path.Combine(roaming, @"Microsoft\Windows\Recent\CustomDestinations") };
            dict["clipboard_history"] = new[] { Path.Combine(local, @"Microsoft\Windows\Clipboard") };
            dict["explorer_traces"] = new[] {
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSaveMRU",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\LastVisitedMRU",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\CIDSizeMRU" };
            dict["empty_recycle_bin"] = new[] { "回收站（所有磁盘）" };

            // —— 无效注册表项类 ——
            dict["invalid_startup"] = new[] {
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
                @"启动文件夹（所有用户 / 当前用户）" };
            dict["invalid_com"] = new[] {
                @"HKCR\CLSID", @"HKCR\Wow6432Node\CLSID",
                @"HKLM\SOFTWARE\Classes\CLSID", @"HKLM\SOFTWARE\WOW6432Node\Classes\CLSID",
                @"HKCU\Software\Classes\CLSID" };
            dict["invalid_uninstall"] = new[] {
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
            dict["invalid_context_menu"] = new[] {
                @"HKCU\Software\Classes\*\shell", @"HKCU\Software\Classes\*\shellex\ContextMenuHandlers",
                @"HKCU\Software\Classes\Directory\shell", @"HKCU\Software\Classes\Directory\Background\shell",
                @"HKCU\Software\Classes\Folder\shell", @"HKCU\Software\Classes\Drive\shell", @"HKCU\Software\Classes\DesktopBackground\shell",
                @"HKLM\Software\Classes\*\shell", @"HKLM\Software\Classes\*\shellex\ContextMenuHandlers",
                @"HKLM\Software\Classes\Directory\shell", @"HKLM\Software\Classes\Directory\Background\shell",
                @"HKLM\Software\Classes\Folder\shell", @"HKLM\Software\Classes\Drive\shell", @"HKLM\Software\Classes\DesktopBackground\shell" };
            dict["invalid_file_association"] = new[] {
                @"HKCR\Applications（HKLM/HKCU\Software\Classes\Applications 合并视图）",
                @"HKLM\SOFTWARE\Classes\Applications",
                @"HKLM\SOFTWARE\WOW6432Node\Classes\Applications",
                @"HKCU\Software\Classes\Applications",
                @"HKCU\Software\WOW6432Node\Classes\Applications",
                @"HKCR\.<扩展名>（.txt/.jpg/.mp3 等，实际扫描 HKLM/HKCU\Software\Classes 下所有 .xxx）",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.<扩展名>\OpenWithProgids / OpenWithList",
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.<扩展名>\OpenWithProgids / OpenWithList",
                @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
                @"HKCU\SOFTWARE\RegisteredApplications",
                @"HKLM\SOFTWARE\RegisteredApplications" };
            dict["invalid_shared_dlls"] = new[] {
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs",
                @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs" };
            dict["invalid_mui_cache"] = new[] {
                @"HKCU\Software\Classes\Local Settings\MuiCache", @"HKLM\SOFTWARE\Classes\MuiCache" };
            dict["invalid_appcompat_layers"] = new[] {
                @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
                @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers" };
            dict["invalid_appcompat_store"] = new[] {
                @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
                @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store" };
            dict["invalid_firewall_rule"] = new[] {
                @"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules" };

            return dict;
        }

        #endregion

        #region 应用日志清理（新建「应用日志清理」分组，复用 *_cache 软件清单）

        
        
        
        
        
        private static Dictionary<string, string[]> AppLogDirectories()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                
                ["edge_logs"] = new[] { Path.Combine(local, @"Microsoft\Edge\User Data\Crashpad\reports"), Path.Combine(local, @"Microsoft\Edge\Logs") },
                ["chrome_logs"] = new[] { Path.Combine(local, @"Google\Chrome\User Data\Crashpad\reports"), Path.Combine(local, @"Google\Chrome\Logs") },
                ["firefox_logs"] = new[] { Path.Combine(local, @"Mozilla\Firefox\Crash Reports"), Path.Combine(local, @"Mozilla\Crash Reports") },
                ["brave_logs"] = new[] { Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data\Crashpad\reports") },
                ["opera_logs"] = new[] { Path.Combine(local, @"Opera Software\Opera Stable\Crashpad\reports"), Path.Combine(local, @"Opera Software\Opera GX\Crashpad\reports") },
                ["browser360_logs"] = new[] { Path.Combine(local, @"360Chrome\Chrome\User Data\Crashpad\reports") },
                ["sogou_logs"] = new[] { Path.Combine(local, @"SogouExplorer\Crashpad\reports"), Path.Combine(local, @"SogouExplorer\Log") },
                ["qqbrowser_logs"] = new[] { Path.Combine(local, @"Tencent\QQBrowser\User Data\Crashpad\reports") },
                ["vivaldi_logs"] = new[] { Path.Combine(local, @"Vivaldi\User Data\Crashpad\reports") },
                ["quark_logs"] = new[] { Path.Combine(local, @"Quark\Quark\User Data\Crashpad\reports"), Path.Combine(local, @"Quark\Application\logs") },
                
                ["wechat_logs"] = new[] { Path.Combine(local, @"Tencent\WeChat\xlog") },
                ["qq_logs"] = new[] { Path.Combine(local, @"Tencent\QQ\Logs"), Path.Combine(local, @"Tencent\QQ\NTQQ\log") },
                ["wxwork_logs"] = new[] { Path.Combine(local, @"Tencent\WXWork\Logs") },
                ["dingtalk_logs"] = new[] { Path.Combine(local, @"DingTalk\logs"), Path.Combine(local, @"DingTalk\Log") },
                ["feishu_logs"] = new[] { Path.Combine(local, @"Feishu\logs"), Path.Combine(local, @"Feishu\Log") },
                ["discord_logs"] = new[] { Path.Combine(roaming, @"discord\logs") },
                ["telegram_logs"] = new[] { Path.Combine(local, @"Telegram Desktop\logs") },
                ["mailmaster_logs"] = new[] { Path.Combine(local, @"MailMaster\logs") },
                
                ["vscode_logs"] = new[] { Path.Combine(roaming, @"Code\logs") },
                ["npm_logs"] = new[] { Path.Combine(user, @".npm\_logs") },
                
                ["netease_logs"] = new[] { Path.Combine(local, @"NetEase\CloudMusic\logs") },
                ["qqmusic_logs"] = new[] { Path.Combine(local, @"Tencent\QQMusic\logs") },
                ["kugou_logs"] = new[] { Path.Combine(local, @"KuGou\log") },
                ["kuwo_logs"] = new[] { Path.Combine(local, @"KuwoMusic\log") },
                ["qqlive_logs"] = new[] { Path.Combine(local, @"Tencent\QQLive\logs") },
                ["youku_logs"] = new[] { Path.Combine(local, @"Youku\logs") },
                ["iqiyi_logs"] = new[] { Path.Combine(local, @"IQIYI\log"), Path.Combine(local, @"iqiyi\logs") },
                ["bilibili_logs"] = new[] { Path.Combine(local, @"bilibili\logs"), Path.Combine(local, @"Bilibili\logs") },
                ["kuaishou_logs"] = new[] { Path.Combine(local, @"Kuaishou\logs") },
                
                ["thunder_logs"] = new[] { Path.Combine(local, @"Thunder Network\Thunder\Logs") },
                ["baidupan_logs"] = new[] { Path.Combine(local, @"BaiduNetdisk\logs") },
                ["steam_logs"] = new[] { Path.Combine(local, @"Steam\logs"), Path.Combine(local, @"Steam\log") },
                ["spotify_logs"] = new[] { Path.Combine(local, @"Spotify\logs") },
                ["wemeet_logs"] = new[] { Path.Combine(local, @"Tencent\WeMeet\logs") },
                ["jianying_logs"] = new[] { Path.Combine(local, @"JianyingPro\logs") },
                ["androws_logs"] = new[] { Path.Combine(roaming, @"Tencent\Androws\ams_cache\log") },
                ["epic_logs"] = new[] { Path.Combine(local, @"Epic\EpicGamesLauncher\Saved\Logs") }
            };
        }

        private static long AppLogDirsSize(string[] dirs)
        {
            long total = 0;
            foreach (var d in dirs)
                if (Directory.Exists(d)) total += DirSize(d);
            return total;
        }

        private CleanService.CleanResult CleanAppLog(string id)
        {
            if (!AppLogDirectories().TryGetValue(id, out var dirs)) return default;
            var results = new List<CleanService.CleanResult>();
            foreach (var d in dirs)
                if (Directory.Exists(d)) results.Add(CleanService.DeleteDirContents(d, _logger));
            return Aggregate(results.ToArray());
        }

        #endregion

        

        
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
                if (res.FailedPaths != null && res.FailedPaths.Count > 0)
                {
                    var list = r.FailedPaths ?? System.Collections.Immutable.ImmutableList<string>.Empty;
                    foreach (var p in res.FailedPaths) list = list.Add(p);
                    r = r with { FailedPaths = list };
                }
            }
            return r;
        }

        private CleanService.CleanResult CleanTempFiles()
        {
            var winTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
            
            
            
            var r1 = CleanService.DeleteDirContents(winTemp, _logger, null);
            var r2 = CleanService.DeleteDirContents(Path.GetTempPath(), _logger, null);
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
                
                TryDeleteEmptySubdirs(path);
            }
            catch (Exception ex)
            {
                logger?.Error($"白名单清理目录失败: {path}", ex);
            }
        }

        
        
        
        private static void CollectFilesRecursivelySafe(string path, List<string> files, ILogger logger)
        {
            try
            {
                
                
                
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

        
        
        
        
        
        
        
        
        
        private static void TryDeleteEmptySubdirs(string root)
        {
            try
            {
                
                
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
                        
                        var attr = dirInfo.Attributes;
                        
                        if ((attr & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                            continue;
                        
                        if ((attr & FileAttributes.System) == FileAttributes.System)
                            continue;
                        
                        if (Directory.GetFileSystemEntries(dir, "*", SearchOption.TopDirectoryOnly).Length == 0)
                        {
                            
                            if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                                File.SetAttributes(dir, attr & ~FileAttributes.ReadOnly);
                            
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

        // EdgeUpdate 下载目录：已应用更新的旧安装包残留，属可安全删除的 stale 安装包（下次更新会重新下载）。
        private CleanService.CleanResult CleanEdgeUpdateCache()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Microsoft\EdgeUpdate\Download");
            if (!Directory.Exists(dir)) return default;
            return CleanService.DeleteDirContents(dir, _logger);
        }

        // Edge 用户数据目录下的 *.bak 是浏览器每次修改数据时自动生成的上一版快照备份（如 Bookmarks.bak、Favicons.bak 等）。
        // 均为冗余备份，删除后会在下次修改时自动重建，不影响当前书签/数据；非缓存、也非用户文档。
        private CleanService.CleanResult CleanEdgeBak()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userData = Path.Combine(local, @"Microsoft\Edge\User Data");
            if (!Directory.Exists(userData)) return default;

            var profileDirs = new List<string> { userData };
            try { profileDirs.AddRange(Directory.GetDirectories(userData, "Profile *")); } catch { }

            var result = new CleanService.CleanResult();
            foreach (var pd in profileDirs)
            {
                foreach (var f in Directory.GetFiles(pd, "*.bak"))
                {
                    try
                    {
                        var len = new FileInfo(f).Length;
                        File.Delete(f);
                        result = result with
                        {
                            FilesSucceeded = result.FilesSucceeded + 1,
                            BytesFreed = result.BytesFreed + len
                        };
                    }
                    catch
                    {
                        result = result with { FilesFailed = result.FilesFailed + 1 };
                    }
                }
            }
            return result;
        }

        // GitHub Desktop 基于 Electron(Chromium)，其 Cache 目录含 Chromium 磁盘缓存(Cache_Data，data_* + index)与 No_Vary_Search 等，均为纯缓存，删除后按需重建。
        private CleanService.CleanResult CleanGitHubDesktopCache()
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var cacheRoot = Path.Combine(roaming, @"GitHub Desktop\Cache");
            if (!Directory.Exists(cacheRoot)) return default;
            var result = new CleanService.CleanResult();
            foreach (var sub in new[] { "Cache_Data", "No_Vary_Search" })
            {
                var dir = Path.Combine(cacheRoot, sub);
                if (Directory.Exists(dir))
                    result = Aggregate(result, CleanService.DeleteDirContents(dir, _logger));
            }
            return result;
        }

        // IE / 传统 Edge 缓存数据库(WebCacheV01.dat，ESE 格式，含 Temporary Internet Files/Cookie/历史索引)。
        // 该 .dat 平时被 dllhost.exe / explorer 进程独占锁定，登录状态下直接删文件通常因“文件正在使用”而跳过；故 best-effort 删除，清理前建议关闭 IE/浏览器并重启 explorer，或经系统“删除浏览历史记录”清理。
        private CleanService.CleanResult CleanWebCache()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\Windows\WebCache");
            if (!Directory.Exists(dir)) return default;
            return CleanService.DeleteDirContents(dir, _logger);
        }

        // Quark 不同安装/版本下 User Data 位置不同：标准 `Quark\User Data`、部分版本 `Quark\Quark\User Data`、中文名 `夸克浏览器\User Data`。
        // 用户实测真实路径为 `Quark\User Data\Default\Cache\Cache_Data`，故首项为标准路径。
        private static readonly string[] QuarkUserDataRoots = new[]
        {
            @"Quark\User Data",
            @"Quark\Quark\User Data",
            @"夸克浏览器\User Data",
        };

        // 仅可安全清理、删除后可重建的缓存类目录（不含 Local Storage / IndexedDB / 扩展 / 会话 / 皮肤 / 证书等用户数据）。
        private static readonly string[] QuarkCacheSubdirs = new[]
        {
            "Cache", "Code Cache", "GPUCache", "image_cache", "blob_storage",
            "remote_resource_cache", "DawnGraphiteCache", "DawnWebGPUCache",
            "Shared Dictionary", "Service Worker",
        };

        private CleanService.CleanResult CleanQuarkCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var results = new List<CleanService.CleanResult>();
            foreach (var root in QuarkUserDataRoots)
            {
                var userData = Path.Combine(local, root);
                if (Directory.Exists(userData))
                    results.Add(CleanBrowserProfileCaches(userData, QuarkCacheSubdirs));
            }
            var appCache = Path.Combine(local, @"Quark\Application\Cache");
            if (Directory.Exists(appCache))
                results.Add(CleanService.DeleteDirContents(appCache, _logger));
            return results.Count > 0 ? Aggregate(results.ToArray()) : default;
        }

        // Androws（腾讯应用宝/安卓模拟器）采用 CEF（Chromium 内核）作为内嵌浏览器，其 User Data 位于 Roaming\Tencent\Androws\cef\CEF_Androws。
        // 下列均为纯缓存/可重建数据：站点特征数据库(Site Characteristics Database)是 Chromium 记录站点交互特征的 leveldb，删除后自动重建；
        // 其余 Cache/Code Cache/GPUCache 等与各浏览器缓存性质相同，不含用户数据（Local Storage/IndexedDB/扩展/会话等不在此列）。
        private static readonly string[] AndrowsCefCacheSubdirs = new[]
        {
            "Cache", "Code Cache", "GPUCache", "Site Characteristics Database",
            "image_cache", "blob_storage", "remote_resource_cache",
            "DawnGraphiteCache", "DawnWebGPUCache", "Shared Dictionary", "Service Worker",
        };

        private CleanService.CleanResult CleanAndrowsCefCache()
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var cefRoot = Path.Combine(roaming, @"Tencent\Androws\cef\CEF_Androws");
            if (!Directory.Exists(cefRoot)) return default;
            return CleanBrowserProfileCaches(cefRoot, AndrowsCefCacheSubdirs);
        }

        // 统一助手：清理多个候选目录（存在才删），汇总各目录的清理结果。
        // 替代各 Clean*Cache 里反复出现的 “new List + foreach + DeleteDirContents + Aggregate” 样板代码。
        private CleanService.CleanResult CleanCandidateDirs(params string[] dirs)
        {
            var results = new List<CleanService.CleanResult>();
            foreach (var d in dirs)
                if (Directory.Exists(d)) results.Add(CleanService.DeleteDirContents(d, _logger));
            return results.Count > 0 ? Aggregate(results.ToArray()) : default;
        }

        // 统一助手：清理单个目录（目录不存在则视为无需清理）。
        private CleanService.CleanResult CleanSingleDir(string dir)
            => Directory.Exists(dir) ? CleanService.DeleteDirContents(dir, _logger) : default;

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
            return CleanCandidateDirs(
                Path.Combine(local, @"DingTalk\Cache"),
                Path.Combine(local, @"DingTalk\update"));
        }

        private CleanService.CleanResult CleanNetEaseMusicCache()
            => CleanSingleDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"NetEase\CloudMusic\Cache"));

        private CleanService.CleanResult CleanSteamCache()
        {
            var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            return CleanCandidateDirs(
                Path.Combine(x86, @"Steam\appcache"),
                Path.Combine(x86, @"Steam\httpcache"),
                Path.Combine(x86, @"Steam\config\htmlcache"));
        }

        private CleanService.CleanResult CleanPrefetch()
            => CleanSingleDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch"));

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
            return CleanCandidateDirs(
                Path.Combine(local, @"Baidu\BaiduNetdisk\cache"),
                Path.Combine(local, @"Baidu\BaiduNetdisk\tmp"));
        }

        private CleanService.CleanResult CleanThunderCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return CleanCandidateDirs(
                Path.Combine(local, @"Thunder Network\Thunder\Profiles\Cache"),
                Path.Combine(local, @"Thunder Network\XLLiveUD\Cache"));
        }

        private CleanService.CleanResult CleanBilibiliCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return CleanCandidateDirs(
                Path.Combine(local, @"bilibili\Cache"),
                Path.Combine(local, @"Bilibili\Cache"));
        }

        private CleanService.CleanResult CleanIqiyiCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return CleanCandidateDirs(
                Path.Combine(local, @"Qiyi\QiyiClient\cache"),
                Path.Combine(local, @"Qiyi\QiyiClient\webcache"));
        }

        private CleanService.CleanResult CleanDiscordCache()
        {
            var stable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Discord");
            return CleanCandidateDirs(
                Path.Combine(stable, "Cache"),
                Path.Combine(stable, "Code Cache"),
                Path.Combine(stable, "GPUCache"));
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
            return CleanCandidateDirs(
                Path.Combine(local, @"SogouExplorer\Cache"),
                Path.Combine(local, @"SogouExplorer\WebkitCache"));
        }

        private CleanService.CleanResult CleanFeishuCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return CleanCandidateDirs(
                Path.Combine(local, @"Feishu\Cache"),
                Path.Combine(local, @"Feishu\Application\Cache"),
                Path.Combine(local, @"Feishu\user_data\cache"));
        }

        private CleanService.CleanResult CleanWeMeetCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return CleanCandidateDirs(
                Path.Combine(local, @"Tencent\WeMeet\Cache"),
                Path.Combine(local, @"Tencent\WeMeet\AppCache"));
        }

        private CleanService.CleanResult CleanJianyingCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return CleanCandidateDirs(
                Path.Combine(local, @"JianyingPro\Cache"),
                Path.Combine(local, @"JianyingPro\User Data\Cache"),
                Path.Combine(local, @"JianyingPro\User Data\GPUCache"));
        }

        private void CleanTelegramCache()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var candidates = new[]
            {
                Path.Combine(appData, @"Telegram Desktop\tdata\cache"),
                Path.Combine(appData, @"Telegram Desktop\tdata\media_cache")
            };
            
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

        
        
        

        
        
        
        
        
        private static bool IsSystemUpgrading()
        {
            
            
            
            
            
            
            var markers = new[] { "SetupHost", "WindowsUpdateBox", "MoUsoCoreWorker", "DismHost", "TiWorker" };
            foreach (var name in markers)
            {
                try { if (Process.GetProcessesByName(name).Length > 0) return true; }
                catch { }
            }
            return false;
        }

        
        
        
        
        
        private void CleanWindowsUpgradeTemp()
        {
            if (IsSystemUpgrading())
            {
                _logger?.Warning("检测到系统正在执行升级/维护，已跳过 Windows 升级临时文件清理，避免破坏进行中的升级。");
                return;
            }
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

        
        
        
        
        
        private void CleanDefenderHistory()
        {
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var scansRoot = Path.Combine(common, @"Microsoft\Windows Defender\Scans\History");
            if (!Directory.Exists(scansRoot)) return;
            var candidates = new[]
            {
                Path.Combine(scansRoot, @"Service\DetectionHistory"), 
                Path.Combine(scansRoot, @"Service\DtlQuarantine"),    
                Path.Combine(scansRoot, @"Store"),                    
                Path.Combine(scansRoot, @"ParserStore")               
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) CleanService.DeleteDirContents(c, _logger);
        }

        
        
        
        
        
        private void CleanFontCache()
        {
            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var fontCacheDir = Path.Combine(winDir, @"ServiceProfiles\LocalService\AppData\Local\FontCache");
            if (!Directory.Exists(fontCacheDir)) return;
            
            foreach (var f in Directory.GetFiles(fontCacheDir, "*.dat"))
            {
                try { CleanService.DeleteFile(f, _logger); }
                catch (Exception ex) { _logger?.Error($"清理字体缓存失败: {f}", ex); }
            }
        }

        
        
        
        
        private void CleanLiveKernelReports()
        {
            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var dir = Path.Combine(winDir, "LiveKernelReports");
            if (!Directory.Exists(dir)) return;
            CleanService.DeleteDirContents(dir, _logger);
        }

        
        
        
        
        
        
        private void CleanIconCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var iconCache = Path.Combine(local, "IconCache.db");
            if (File.Exists(iconCache))
                CleanService.DeleteFile(iconCache, _logger);
        }

        
        
        

        
        
        
        
        
        
        
        
        
        
        
        private void CleanWeChatCache()
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var wechatRoot = Path.Combine(documents, "WeChat Files");
            if (!Directory.Exists(wechatRoot))
            {
                
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                wechatRoot = Path.Combine(local, @"Tencent\WeChat");
            }
            if (!Directory.Exists(wechatRoot)) return;

            foreach (var wxidDir in Directory.GetDirectories(wechatRoot))
            {
                
                var dirName = Path.GetFileName(wxidDir);
                if (dirName.Equals("All Users", StringComparison.OrdinalIgnoreCase)) continue;

                var fileStorage = Path.Combine(wxidDir, "FileStorage");
                if (!Directory.Exists(fileStorage)) continue;

                
                
                var cacheOnly = Path.Combine(fileStorage, "Cache");
                if (Directory.Exists(cacheOnly))
                    CleanService.DeleteDirContents(cacheOnly, _logger);
            }
        }

        
        
        
        
        
        
        
        
        
        
        
        
        private void CleanQQCache()
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var tencentRoot = Path.Combine(documents, "Tencent Files");
            if (!Directory.Exists(tencentRoot)) return;
            foreach (var qqDir in Directory.GetDirectories(tencentRoot))
            {
                var dirName = Path.GetFileName(qqDir);
                
                if (dirName.Equals("All Users", StringComparison.OrdinalIgnoreCase)) continue;

                
                
                var richMedia = Path.Combine(qqDir, "RichMedia");
                if (Directory.Exists(richMedia))
                    CleanService.DeleteDirContents(richMedia, _logger);
            }

            
            
            
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var c in new[]
            {
                Path.Combine(local, @"Tencent\QQ\Cache"),
                Path.Combine(local, @"Tencent\QQ\Code Cache"),
                Path.Combine(local, @"Tencent\QQ\GPUCache")
            })
                if (Directory.Exists(c)) CleanService.DeleteDirContents(c, _logger);
        }

        
        
        
        
        
        private void CleanWXWorkCache()
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var wxworkRoot = Path.Combine(documents, "WXWork");
            if (!Directory.Exists(wxworkRoot)) return;
            foreach (var uidDir in Directory.GetDirectories(wxworkRoot))
            {
                
                var cacheOnly = Path.Combine(uidDir, "Cache");
                if (Directory.Exists(cacheOnly))
                    CleanService.DeleteDirContents(cacheOnly, _logger);
            }
        }

        
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

        
        
        

        
        private void CleanQQBrowserCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userData = Path.Combine(local, @"Tencent\QQBrowser\User Data");
            CleanBrowserProfileCaches(userData, new[] { "Cache", "Code Cache" });
        }

        
        private void CleanVivaldiCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userData = Path.Combine(local, @"Vivaldi\User Data");
            CleanBrowserProfileCaches(userData, new[] { "Cache", "Code Cache" });
        }

        
        
        

        
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

        
        
        

        
        
        
        
        
        
        private void CleanWPSCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(local, @"Kingsoft\office6\cache"),
                Path.Combine(local, @"WPS\cache")
                
                
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) CleanService.DeleteDirContents(c, _logger);
        }

        
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

        
        
        

        
        private void CleanVSCodeCache()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var candidates = new[]
            {
                Path.Combine(appData, @"Code\Cache"),
                Path.Combine(appData, @"Code\CachedData"),
                Path.Combine(appData, @"Code\Code Cache"),
                Path.Combine(appData, @"Code\GPUCache")
                
                
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) CleanService.DeleteDirContents(c, _logger);
        }

        
        
        
        
        private void CleanJetBrainsCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var jbRoot = Path.Combine(local, @"JetBrains");
            if (!Directory.Exists(jbRoot)) return;
            foreach (var productDir in Directory.GetDirectories(jbRoot))
            {
                
                var subDirs = new[] { "caches", "log", "tmp", "index" };
                foreach (var sub in subDirs)
                {
                    var p = Path.Combine(productDir, sub);
                    if (Directory.Exists(p)) CleanService.DeleteDirContents(p, _logger);
                }
            }
            
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
            
            try
            {
                var mefCacheDirs = Directory.GetDirectories(vsRoot, "MEFCache", SearchOption.AllDirectories);
                foreach (var d in mefCacheDirs)
                    if (Directory.Exists(d)) CleanService.DeleteDirContents(d, _logger);
            }
            catch (Exception ex) { _logger?.Error($"枚举 MEFCache 目录失败: {vsRoot}", ex); }
        }

        
        private void CleanNpmCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var cache = Path.Combine(local, "npm-cache");
            if (Directory.Exists(cache)) CleanService.DeleteDirContents(cache, _logger);
        }

        
        private void CleanPipCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            
            var cache = Path.Combine(local, @"pip\cache");
            if (Directory.Exists(cache)) CleanService.DeleteDirContents(cache, _logger);
        }

        
        
        
        
        
        private void CleanMavenCache()
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var repo = Path.Combine(userHome, @".m2\repository");
            if (Directory.Exists(repo))
                CleanService.DeleteDirContents(repo, _logger);
        }

        
        
        
        
        
        private void CleanGradleCache()
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var caches = Path.Combine(userHome, @".gradle\caches");
            if (Directory.Exists(caches))
                CleanService.DeleteDirContents(caches, _logger);
        }

        
        private void CleanNuGetCache()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            
            var httpCache = Path.Combine(local, @"NuGet\v3-cache");
            if (Directory.Exists(httpCache)) CleanService.DeleteDirContents(httpCache, _logger);
            
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
                
                
                
                foreach (var f in Directory.GetFiles(recent, "*.lnk"))
                    CleanService.DeleteFile(f, _logger);
            }
        }

        private void CleanClipboardHistory()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            CleanService.DeleteDirContents(Path.Combine(local, @"Microsoft\Windows\Clipboard"), _logger);
        }

        
        
        
        
        
        
        
        

        
        
        
        
        
        private static bool IsDeadFileReference(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            var s = command.Trim();

            
            
            
            if (LooksLikeSystemOrSafeCommand(s)) return false;

            string exe;
            if (s.StartsWith("\""))
            {
                var end = s.IndexOf('"', 1);
                exe = end > 1 ? s.Substring(1, end - 1) : s;
            }
            else
            {
                
                
                
                
                
                exe = ResolveExecutableFromUnquoted(s);
            }

            exe = Environment.ExpandEnvironmentVariables(exe).Trim();
            if (string.IsNullOrWhiteSpace(exe) || !Path.IsPathRooted(exe)) return false;
            
            
            if (NativeMethods.FileExistsResilient(exe)) return false;

            // 重解析点（如 App 执行别名 wt.exe）本身存在即视为存活，不删除
            if (DiskUtils.IsReparsePoint(exe)) return false;

            
            string alt = null;
            if (exe.IndexOf("System32", StringComparison.OrdinalIgnoreCase) >= 0)
                alt = ReplaceIgnoreCase(exe, "System32", "SysWOW64");
            else if (exe.IndexOf("SysWOW64", StringComparison.OrdinalIgnoreCase) >= 0)
                alt = ReplaceIgnoreCase(exe, "SysWOW64", "System32");
            if (alt != null && NativeMethods.FileExistsResilient(alt)) return false;
            return true;
        }

        private static string ReplaceIgnoreCase(string s, string oldValue, string newValue)
        {
            var idx = s.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
            return idx < 0 ? s : s.Remove(idx, oldValue.Length).Insert(idx, newValue);
        }

        
        
        
        
        
        
        private static bool IsEmptyExecutableCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            var s = command.Trim();
            if (s == "\\" || s == "\"" || s == "\"\"") return true;
            
            if (s.StartsWith("\"\"", StringComparison.Ordinal)) return true;
            return false;
        }

        
        
        
        
        
        
        
        
        
        private static bool LooksLikeSystemOrSafeCommand(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            var expanded = Environment.ExpandEnvironmentVariables(s).Trim();
            if (string.IsNullOrWhiteSpace(expanded)) return false;

            
            if (expanded.StartsWith("@", StringComparison.Ordinal)) return true;

            
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(win)
                && (expanded.StartsWith(win, StringComparison.OrdinalIgnoreCase)
                    || expanded.IndexOf(@"Windows\System32", StringComparison.OrdinalIgnoreCase) >= 0
                    || expanded.IndexOf(@"Windows\SysWOW64", StringComparison.OrdinalIgnoreCase) >= 0))
                return true;

            
            if (expanded.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            
            if ((expanded.IndexOf(':', StringComparison.Ordinal) >= 0 || expanded.StartsWith("{", StringComparison.Ordinal))
                && !expanded.StartsWith("\"", StringComparison.Ordinal)
                && !Path.IsPathRooted(expanded))
                return true;

            
            var first = expanded.Split(' ', '\t', '"')[0].Trim();
            if (_knownSystemExeNames.Contains(first)) return true;

            return false;
        }

        
        
        
        
        private static List<(string exeName, string command)> GetInvalidFileAssociations()
        {
            var result = new List<(string exeName, string command)>();

            try
            {
                
                
                
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

                        
                        foreach (var baseKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
                        {
                            foreach (var classesRel in new[] { @"Software\Classes", @"Software\WOW6432Node\Classes" })
                            {
                                using var key = baseKey.OpenSubKey($@"{classesRel}\Applications\{exeName}\shell\open\command", false);
                                if (key != null)
                                {
                                    var c = key.GetValue(null) as string;
                                    if (!string.IsNullOrWhiteSpace(c))
                                        command = c; 
                                }
                            }
                        }

                        if (string.IsNullOrWhiteSpace(command)) continue;

                        
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

        
        
        
        private static bool IsSystemCriticalAssociation(string exeName, string command)
        {
            
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

            
            
            
            

            return false;
        }

        
        
        
        
        
        
        
        
        
        
        
        private static long InvalidFileAssociationSize()
        {
            int count = GetInvalidFileAssociations().Count;
            count += CountOpenWithResiduals();
            count += CountInvalidExtensionAssociations();
            count += CountInvalidRegisteredApplications();
            count += CountInvalidAppPaths();
            
            return count * 500L;
        }

        
        
        
        
        
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

                            
                            using (var progidsKey = extKey.OpenSubKey("OpenWithProgids", false))
                            {
                                if (progidsKey != null)
                                {
                                    foreach (var valueName in progidsKey.GetValueNames())
                                    {
                                        try
                                        {
                                            
                                            
                                            if (IsDeadProgId(valueName))
                                                count++;
                                        }
                                        catch { }
                                    }
                                }
                            }

                            
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

        
        
        
        private void CleanInvalidFileAssociation()
        {
            var invalid = GetInvalidFileAssociations();
            int cleaned = 0;

            
            
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

            
            
            CleanOpenWithResiduals();

            
            
            
            
            
            CleanInvalidExtensionAssociations();
            CleanInvalidRegisteredApplications();
            CleanInvalidAppPaths();

            
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

                            
                            using (var progidsKey = extKey.OpenSubKey("OpenWithProgids", true))
                            {
                                if (progidsKey != null)
                                {
                                    var deadValues = new List<string>();
                                    foreach (var valueName in progidsKey.GetValueNames())
                                    {
                                        try
                                        {
                                            
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

                                    
                                    try
                                    {
                                        var defVal = progidsKey.GetValue(null) as string;
                                        if (!string.IsNullOrWhiteSpace(defVal) && IsDeadProgId(defVal))
                                            progidsKey.SetValue(null, "", RegistryValueKind.String);
                                    }
                                    catch { }
                                }
                            }

                            
                            using (var listKey = extKey.OpenSubKey("OpenWithList", true))
                            {
                                if (listKey != null)
                                {
                                    var deadValues = new List<string>();
                                    foreach (var valueName in listKey.GetValueNames())
                                    {
                                        
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

        
        
        
        
        private static bool IsDeadProgId(string progId)
        {
            if (string.IsNullOrWhiteSpace(progId)) return false;

            
            
            
            
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

            
            
            if (anyAliveCommand) return false;
            if (foundDeadCommand) return true;

            
            
            return !existsAnywhere;
        }

        
        
        
        
        
        
        
        
        
        
        private static bool IsDeadOpenWithExe(string exeName)
        {
            if (string.IsNullOrWhiteSpace(exeName)) return false;

            var bases = new[] { Registry.LocalMachine, Registry.CurrentUser };
            var classRels = new[] { @"Software\Classes", @"Software\WOW6432Node\Classes" };

            foreach (var baseKey in bases)
            {
                foreach (var cr in classRels)
                {
                    
                    try
                    {
                        using var appKey = baseKey.OpenSubKey($@"{cr}\Applications\{exeName}\shell\open\command", false);
                        if (appKey != null)
                        {
                            var command = appKey.GetValue(null) as string;
                            if (!string.IsNullOrWhiteSpace(command))
                            {
                                if (!IsDeadFileReference(command))
                                    return false; 
                            }
                        }
                    }
                    catch { }
                }
            }

            
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
                                    return false; 
                            }
                        }
                    }
                    catch { }
                }
            }

            
            return true;
        }

        
        
        
        
        
        
        
        private static readonly string[] _classesRels = new[]
        {
            @"Software\Classes",
            @"Software\WOW6432Node\Classes"
        };

        
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

        
        // MuiCache 值名 = exe/dll 完整路径，可能带 .FriendlyAppName / .ApplicationCompany 后缀。
        // 32 位(WOW64)程序的 MuiCache 重定向到 Wow6432Node 下的同名键，需两处都扫，否则大量废弃项漏掉。
        private static readonly string[] MuiCacheKeyPaths = new[]
        {
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
            @"Software\Classes\Wow6432Node\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
        };

        private static string ExtractMuiCachePath(string valueName)
        {
            if (string.IsNullOrWhiteSpace(valueName)) return null;
            int idx = valueName.IndexOf(".FriendlyAppName", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return valueName.Substring(0, idx);
            idx = valueName.IndexOf(".ApplicationCompany", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return valueName.Substring(0, idx);
            // 无后缀：值名本身就是 exe/dll 的完整路径（旧版 Windows / 无法解析 FriendlyAppName 时的原始格式）。
            // 仅当形如文件路径（含盘符或路径分隔符）才返回，避免误删极少数非路径内部条目。
            if (valueName.IndexOf('\\') >= 0 || (valueName.Length >= 2 && valueName[1] == ':'))
                return valueName;
            return null;
        }

        
        private static int CountInvalidMuiCache()
        {
            int count = 0;
            foreach (var rel in MuiCacheKeyPaths)
            {
                using var key = Registry.CurrentUser.OpenSubKey(rel, false);
                if (key == null) continue;
                foreach (var name in key.GetValueNames())
                {
                    var path = ExtractMuiCachePath(name);
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    try { if (!NativeMethods.FileExistsResilient(path)) count++; } catch { }
                }
            }
            return count;
        }

        private static long InvalidMuiCacheSize() => CountInvalidMuiCache() * 100L;

        private void CleanInvalidMuiCache()
        {
            int cleaned = 0;
            foreach (var rel in MuiCacheKeyPaths)
            {
                using var key = Registry.CurrentUser.OpenSubKey(rel, true);
                if (key == null) continue;
                foreach (var name in key.GetValueNames().ToList())
                {
                    var path = ExtractMuiCachePath(name);
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    try
                    {
                        if (!NativeMethods.FileExistsResilient(path))
                        {
                            key.DeleteValue(name, false);
                            cleaned++;
                        }
                    }
                    catch { }
                }
            }

            if (cleaned > 0)
                _logger?.Info($"已清理失效 MUI 缓存: {cleaned} 条");
        }

        
        
        
        
        
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

        
        
        
        
        
        
        private static int CountInvalidCompatStore()
        {
            int count = 0;
            foreach (var baseKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var rel in new[]
                {
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store"
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

        private static long InvalidCompatStoreSize() => CountInvalidCompatStore() * 100L;

        private void CleanInvalidCompatStore()
        {
            int cleaned = 0;
            foreach (var baseKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var rel in new[]
                {
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store"
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
                _logger?.Info($"已清理失效兼容性助手记录: {cleaned} 条");
        }

        #region 失效防火墙规则

        private static readonly string[] FirewallRuleKeys =
        {
            @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules"
        };

        
        
        
        
        private static string ExtractFirewallApp(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            foreach (var seg in value.Split('|'))
            {
                if (seg.StartsWith("App=", StringComparison.OrdinalIgnoreCase))
                    return seg.Substring(4).Trim();
            }
            return null; 
        }

        private static int CountInvalidFirewallRules()
        {
            int count = 0;
            foreach (var rel in FirewallRuleKeys)
            {
                using var key = Registry.LocalMachine.OpenSubKey(rel, false);
                if (key == null) continue;
                foreach (var name in key.GetValueNames())
                {
                    try
                    {
                        var app = ExtractFirewallApp(key.GetValue(name) as string);
                        if (app != null && IsDeadFileReference(app)) count++;
                    }
                    catch { }
                }
            }
            return count;
        }

        private static long InvalidFirewallRulesSize() => CountInvalidFirewallRules() * 100L;

        private void CleanInvalidFirewallRules()
        {
            int cleaned = 0;
            
            foreach (var rel in FirewallRuleKeys)
            {
                using var key = Registry.LocalMachine.OpenSubKey(rel, true);
                if (key == null) continue;
                foreach (var name in key.GetValueNames().ToList())
                {
                    try
                    {
                        var app = ExtractFirewallApp(key.GetValue(name) as string);
                        if (app != null && IsDeadFileReference(app))
                        {
                            key.DeleteValue(name, false);
                            cleaned++;
                        }
                    }
                    catch { }
                }
            }

            if (cleaned > 0)
                _logger?.Info($"已清理失效防火墙规则: {cleaned} 条");
        }

        #endregion

        #region 资源管理器使用痕迹

        
        
        
        
        private static readonly string[] ExplorerTraceKeys =
        {
            @"RecentDocs",
            @"RunMRU",
            @"TypedPaths",
            @"UserAssist",
            @"ComDlg32\OpenSaveMRU",
            @"ComDlg32\LastVisitedMRU",
            @"ComDlg32\CIDSizeMRU"
        };

        private static long SumValueDataBytes(RegistryKey key)
        {
            if (key == null) return 0;
            long total = 0;
            try
            {
                foreach (var name in key.GetValueNames())
                {
                    var data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (data is string s) total += s.Length * 2L;
                    else if (data is byte[] b) total += b.Length;
                    else if (data is int) total += 4;
                    else if (data is long) total += 8;
                }
            }
            catch { }
            foreach (var sub in key.GetSubKeyNames())
            {
                using var subKey = key.OpenSubKey(sub, false);
                total += SumValueDataBytes(subKey);
            }
            return total;
        }

        private static int CountRegistryValues(RegistryKey key)
        {
            if (key == null) return 0;
            int n = 0;
            try { n += key.GetValueNames().Length; } catch { }
            foreach (var sub in key.GetSubKeyNames())
            {
                using var subKey = key.OpenSubKey(sub, false);
                n += CountRegistryValues(subKey);
            }
            return n;
        }

        private static int CountExplorerTraces()
        {
            int count = 0;
            foreach (var rel in ExplorerTraceKeys)
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\" + rel, false);
                count += CountRegistryValues(key);
            }
            return count;
        }

        private static long ExplorerTracesSize()
        {
            long total = 0;
            foreach (var rel in ExplorerTraceKeys)
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\" + rel, false);
                total += SumValueDataBytes(key);
            }
            return total;
        }

        private void CleanExplorerTraces()
        {
            int cleaned = 0;
            foreach (var rel in ExplorerTraceKeys)
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\" + rel, true);
                if (key == null) continue;
                cleaned += ClearRegistryValues(key);
            }
            if (cleaned > 0)
                _logger?.Info($"已清理资源管理器使用痕迹: {cleaned} 项");
        }

        
        private static int ClearRegistryValues(RegistryKey key)
        {
            if (key == null) return 0;
            int n = 0;
            foreach (var name in key.GetValueNames().ToList())
            {
                try { key.DeleteValue(name, false); n++; } catch { }
            }
            foreach (var sub in key.GetSubKeyNames())
            {
                using var subKey = key.OpenSubKey(sub, true);
                n += ClearRegistryValues(subKey);
            }
            return n;
        }

        #endregion

        
        
        
        
        
        
        
        private static bool IsDeadCapabilityApp(RegistryKey baseKey, string capRel)
        {
            using var cap = baseKey.OpenSubKey(capRel, false);
            
            
            if (cap == null) return false;

            var icon = cap.GetValue("ApplicationIcon") as string;
            if (!string.IsNullOrWhiteSpace(icon))
            {
                var exe = icon.Split(',')[0].Trim();
                
                if (exe.StartsWith("@", StringComparison.Ordinal)) return false;
                exe = Environment.ExpandEnvironmentVariables(exe);
                
                if (!Path.IsPathRooted(exe)) return false;
                if (!IsDeadFileReference(exe)) return false; 
                return true; 
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
                    if (!IsDeadProgId(progId)) return false; 
                }
            }

            return anyAssociation; 
        }

        
        
        
        
        
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
            
            
            
            
            return s;
        }

        private static readonly string[] _startupKeys = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce"
        };

        
        
        
        private static readonly string[] _startupFolders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
        };

        
        private static readonly string _taskBackupDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskCleaner", "TaskBackups");

        
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

        
        
        
        
        
        
        
        
        private static IEnumerable<string> EnumerateContextMenuClassKeys(RegistryKey classesKey)
        {
            
            foreach (var cls in ContextMenuMapping.InvalidContextMenuGenericClasses)
                yield return cls;

            
            string[] extNames;
            try { extNames = classesKey.GetSubKeyNames(); }
            catch { yield break; }

            foreach (var name in extNames)
            {
                if (!name.StartsWith(".", StringComparison.OrdinalIgnoreCase)) continue;
                yield return name;

                
                
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

            
            var topLevelProgIds = new List<string>();
            try
            {
                foreach (var name in extNames)
                {
                    if (name.StartsWith(".", StringComparison.OrdinalIgnoreCase)) continue;
                    if (ContextMenuMapping.InvalidContextMenuGenericClasses.Contains(name)) continue;
                    if (name.StartsWith("{", StringComparison.Ordinal)) continue; 
                    if (name.IndexOf('\\') >= 0) continue;
                    using var shellKey = classesKey.OpenSubKey(name + @"\shell", false);
                    if (shellKey != null) topLevelProgIds.Add(name);
                }
            }
            catch { }

            foreach (var p in topLevelProgIds)
                yield return p;
        }

        
        
        
        
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
                            
                            
                            
                            bool dead = !string.IsNullOrWhiteSpace(target)
                                        && !NativeMethods.FileExistsResilient(target)
                                        && !Directory.Exists(target);
                            if (dead)
                            {
                                var la = File.GetAttributes(lnk);
                                if ((la & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                                    File.SetAttributes(lnk, la & ~FileAttributes.ReadOnly);
                                File.Delete(lnk);
                                _logger?.Info($"已清理启动文件夹失效项: {lnk} -> {target}");
                            }
                        }
                        catch (Exception ex) { _logger?.Error($"清理启动文件夹项失败: {lnk}", ex); }
                    }
                }
                catch (Exception ex) { _logger?.Error($"枚举启动文件夹失败: {folder}", ex); }
            }
        }

        
        
        
        
        
        
        private static string GetShortcutTarget(string lnkPath)
            => ShortcutHelper.ResolveShortcutTarget(lnkPath);

        
        
        
        
        private void CleanInvalidScheduledTask()
        {
            var dead = GetDeadScheduledTasks();
            if (dead.Count == 0) return;
            try { Directory.CreateDirectory(_taskBackupDir); } catch { }

            foreach (var (folderPath, name, xml) in dead)
            {
                try
                {
                    
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
                
            }
            return result;
        }

        private static void CollectDeadTasks(dynamic folder, List<(string, string, string)> result)
        {
            try
            {
                var tasks = folder.GetTasks(1); 
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

                            if (ContextMenuMapping.IsProtectedShellExtension(guid, null)) continue;

                            
                            
                            
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
                    
                    foreach (var cls in EnumerateContextMenuClassKeys(classesKey))
                    {
                        
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

                                        
                                        if (ContextMenuMapping.IsSystemContextMenuItem(handler, guid)) continue;

                                        
                                        
                                        
                                        if (ContextMenuMapping.IsWin11TopLevelExtension(guid, handler)) continue;
                                        if (ContextMenuMapping.IsProtectedShellExtension(guid, handler)) continue;

                                        
                                        
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
            total += InvalidContextMenuSizeIn(Registry.CurrentUser, null);
            total += InvalidContextMenuSizeIn(Registry.LocalMachine, null);
            return total;
        }

        // 扫描无效右键菜单，同时把具体无效路径写入 item.Details，供信息图标 ⓘ 与状态文本 ToolTip 展示。
        private long ScanInvalidContextMenu(CleanItem item)
        {
            var details = new List<string>();
            long total = 0;
            total += InvalidContextMenuSizeIn(Registry.CurrentUser, details);
            total += InvalidContextMenuSizeIn(Registry.LocalMachine, details);
            item.Details = details.Count > 0
                ? $"检测到 {details.Count} 个无效右键菜单项：\n\n{string.Join("\n", details)}"
                : null;
            return total;
        }

        private static long InvalidContextMenuSizeIn(RegistryKey baseKey, List<string> details)
        {
            long count = 0;
            foreach (var classRoot in ContextMenuMapping.ClassRootsForInvalidScan)
            {
                using var classesKey = baseKey.OpenSubKey(classRoot, false);
                if (classesKey == null) continue;
                
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
                                if (ContextMenuMapping.ProtectedVerbs.Contains(verb)) continue;
                                if (ContextMenuMapping.IsSystemContextMenuItem(verb, null)) continue;
                                using var cmdKey = verbKey.OpenSubKey("command", false);
                                if (cmdKey == null) continue;
                                var cmdVal = cmdKey.GetValue(null) as string;
                                if (IsDeadFileReference(cmdVal) || IsEmptyExecutableCommand(cmdVal))
                                {
                                    count++;
                                    details?.Add($@"{baseKey.Name}\{classRoot}\{cls}\shell\{verb}");
                                }
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
                                if (ContextMenuMapping.IsSystemContextMenuItem(handler, null)) continue;
                                string guid = null;
                                using (var hk = shellexKey.OpenSubKey(handler, false))
                                    guid = hk?.GetValue(null) as string;
                                if (string.IsNullOrWhiteSpace(guid)) continue;
                                if (ContextMenuMapping.IsSystemContextMenuItem(handler, guid)) continue;
                                if (ContextMenuMapping.IsWin11TopLevelExtension(guid, handler)) continue;
                                if (ContextMenuMapping.IsProtectedShellExtension(guid, handler)) continue;
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
                                if (anyServer && allDead)
                                {
                                    count++;
                                    details?.Add($@"{baseKey.Name}\{classRoot}\{cls}\shellex\ContextMenuHandlers\{handler}");
                                }
                            }
                    }
                    catch { }
                }
            }
            return count * 256; 
        }

        
        
        
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
                            
                            
                            
                            bool dead = !string.IsNullOrWhiteSpace(target)
                                        && !NativeMethods.FileExistsResilient(target)
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

        
        private static long InvalidScheduledTaskSize()
            => GetDeadScheduledTasks().Count * 2048;

        private static long InvalidComSize()
        {
            long total = 0;
            total += InvalidComCountIn(Registry.CurrentUser);
            total += InvalidComCountIn(Registry.LocalMachine);
            return total * 256; 
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

                            if (ContextMenuMapping.IsProtectedShellExtension(guid, null)) continue;

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

        

        
        
        
        
        
        private void CleanOldDriverPackages()
        {
            if (IsSystemUpgrading())
            {
                _logger?.Warning("检测到系统正在执行升级/维护，已跳过设备驱动程序包清理，避免干扰进行中的升级。");
                return;
            }
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
                
                
                if (set.Count == 0) return null;
                return set;
            }
            catch
            {
                return null;
            }
        }

        
        
        
        
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
                
            }
            return result;
        }

        
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
            tool.CancelCommand = new RelayCommand(_ => CancelOptimize(tool));
            tool.ClearCommand = new RelayCommand(_ => tool.ClearOutput());
            if (hasRestore)
            {
                tool.HasRestore = true;
                tool.RestoreDescription = restoreDesc;
                tool.RestoreCommand = new RelayCommand(_ => _ = RunOptimizeAsync("hibernate_on", tool));
            }
            return tool;
        }

        
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

        
        
        
        
        private void ApplyVisibilityFilter()
        {
            foreach (var item in AllCleanItems(CleanGroups))
            {
                item.IsVisible = item.AlwaysShow || item.Size > 0;
            }
        }

        
        
        
        
        
        private static void ApplyDefaultCheck(CleanItem item)
        {
            item.IsChecked = item.CanClean != null && item.CanClean() && item.Size > 0;
        }

        
        private async Task ScanAsync()
        {
            if (IsBusy || AnyToolRunning) return;
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
                foreach (var it in items)
                    it.TargetPaths = GetItemTargetRoots(it.Id);
                int total = items.Count;

                
                var itemsToScan = new List<CleanItem>();
                foreach (var item in items)
                {
                    item.ErrorMessage = null;
                    item.Details = null;
                    if (item.CanClean != null && item.CanClean())
                    {
                        
                        item.IsChecked = false;
                        itemsToScan.Add(item);
                    }
                    else
                    {
                        item.IsChecked = false;
                        item.Size = 0;
                        item.Status = ScanStatus.NotScanned;
                    }
                }

                
                int done = total - itemsToScan.Count; 
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
                        
                        _uiDispatcher?.BeginInvoke(new Action(() => ScanCurrentItem = item.Name));
                        sizes[idx] = await Task.Run(() => EstimateItemSize(item));
                    }
                    catch { sizes[idx] = 0; }
                    finally
                    {
                        semaphore.Release();
                        var localDone = Interlocked.Increment(ref done);
                        
                        _ = _uiDispatcher.BeginInvoke(new Action(() =>
                        {
                            CleanProgressText = $"扫描 {localDone}/{total}";
                            ScanProgressValue = (int)(localDone * 100.0 / total);
                        }));
                    }
                }).ToArray();

                await Task.WhenAll(scanTasks);

                
                for (int i = 0; i < itemsToScan.Count; i++)
                {
                    itemsToScan[i].Size = sizes[i];
                    itemsToScan[i].Status = ScanStatus.Scanned;
                    ApplyDefaultCheck(itemsToScan[i]); 
                    
                    if (itemsToScan[i].Id != "empty_recycle_bin")
                        totalSize += sizes[i];
                }

                RecommendedTotalText = $"{FormatSize(totalSize)} 可清理";
                StatusText = "扫描完成";
                ApplyVisibilityFilter(); 
                HasScanned = true; 
                UpdateDriveInfo();
            }
            catch (Exception ex)
            {
                StatusText = "扫描失败";
                _logger.Error("扫描失败", ex);
            }
            finally
            {
                
                IsScanning = false;
                ScanCurrentItem = "";
                _cts?.Dispose();
                SetIsBusy(false);
            }
        }

        
        private async Task CleanAsync()
        {
            var selected = AllCleanItems(CleanGroups)
                .Where(c => c.IsChecked == true && c.Risk != RiskLevel.Forbidden).ToList();
            if (!selected.Any())
            {
                MessageBox.Show("未选中任何可清理项。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (IsBusy || AnyToolRunning) return;
            SetIsBusy(true);
            IsCleaning = true;
            CleanFileCount = 0;
            _cts = NewCts();
            var uiDispatcher = _uiDispatcher;
            
            
            var recycleItem = selected.FirstOrDefault(c => c.Id == "empty_recycle_bin");
            if (recycleItem != null) recycleItem.IsVisible = true; 
            var mainItems = selected.Where(c => c != recycleItem).ToList();
            try
            {
                StatusText = "正在清理...";

                
                void ProcessCleanItem(CleanItem item)
                {
                    if (_cts.Token.IsCancellationRequested) return;
                    CleanSingleItem(item);
                }

                await Task.Run(() =>
                {
                    
                    var sem = new SemaphoreSlim(Math.Max(1, Math.Min(4, Environment.ProcessorCount)));
                    var tasks = new List<Task>(mainItems.Count);
                    foreach (var item in mainItems)
                    {
                        if (_cts.Token.IsCancellationRequested) break;
                        try { sem.Wait(_cts.Token); }
                        catch (OperationCanceledException) { break; }
                        tasks.Add(Task.Run(() =>
                        {
                            try { ProcessCleanItem(item); }
                            finally { sem.Release(); }
                        }, _cts.Token));
                    }
                    try { Task.WhenAll(tasks).Wait(_cts.Token); }
                    catch (OperationCanceledException) { }
                    catch (AggregateException ae) { ae.Handle(e => e is OperationCanceledException); }

                    
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
                CleanProgressText = string.Empty; 
                
                
                
                
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
                    
                    else if (item.Id == "empty_recycle_bin")
                    {
                        item.Size = CleanService.GetRecycleBinSize();
                    }
                    if (item.IsChecked == true && item.Risk != RiskLevel.Forbidden && item.Status != ScanStatus.Cleaned)
                        remaining += item.Size;
                }
                RecommendedTotalText = remaining > 0 ? $"{FormatSize(remaining)} 可清理" : "—";
                ApplyVisibilityFilter(); 
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
                _cts?.Dispose();
                IsCleaning = false;
                SetIsBusy(false);
                RefreshBrowserProcessStates();
            }
        }


        
        
        
        
        
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
                        
                        
                        size += DiskUtils.FilesSize(winDir, "*.dmp");
                        
                        var memoryDumpFile = Path.Combine(winDir, "MEMORY.DMP");
                        if (File.Exists(memoryDumpFile))
                        {
                            try { size -= new FileInfo(memoryDumpFile).Length; } catch { }
                        }
                        return size;
                    case "windows_logs": return DirSize(Path.Combine(winDir, "Logs"));
                    case "thumbnail_cache":
                        
                        return ThumbIconCacheDbSize(Path.Combine(local, @"Microsoft\Windows\Explorer"));
                    case "inet_cache": return DirSize(Path.Combine(local, @"Microsoft\Windows\INetCache"));
                    case "wer": return DirSize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER"));
                    case "delivery_optimization":
                        return DirSize(Path.Combine(winDir, @"SoftwareDistribution\DeliveryOptimization"))
                             + DirSize(Path.Combine(winDir, @"SoftwareDistribution\Download"));
                    case "edge_cache":
                        return BrowserProfileCachesSize(Path.Combine(local, @"Microsoft\Edge\User Data"), new[] { "Cache", "Code Cache" });
                    case "edge_update_cache":
                        {
                            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\EdgeUpdate\Download");
                            return Directory.Exists(dir) ? DirSize(dir) : 0;
                        }
                    case "edge_bak":
                        {
                            var userData = Path.Combine(local, @"Microsoft\Edge\User Data");
                            if (!Directory.Exists(userData)) return 0;
                            long s = 0;
                            var dirs = new List<string> { userData };
                            try { dirs.AddRange(Directory.GetDirectories(userData, "Profile *")); } catch { }
                            foreach (var d in dirs) s += DiskUtils.FilesSize(d, "*.bak");
                            return s;
                        }
                    case "quark_cache":
                        {
                            long q = 0;
                            foreach (var root in QuarkUserDataRoots)
                            {
                                var userData = Path.Combine(local, root);
                                if (Directory.Exists(userData))
                                    q += BrowserProfileCachesSize(userData, QuarkCacheSubdirs);
                            }
                            var appCache = Path.Combine(local, @"Quark\Application\Cache");
                            if (Directory.Exists(appCache)) q += DirSize(appCache);
                            return q;
                        }
                    case "androws_cef_cache":
                        {
                            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                            var cefRoot = Path.Combine(roaming, @"Tencent\Androws\cef\CEF_Androws");
                            return Directory.Exists(cefRoot) ? BrowserProfileCachesSize(cefRoot, AndrowsCefCacheSubdirs) : 0;
                        }
                    case "github_cache":
                        {
                            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                            var cacheRoot = Path.Combine(roaming, @"GitHub Desktop\Cache");
                            if (!Directory.Exists(cacheRoot)) return 0;
                            return DirSize(Path.Combine(cacheRoot, "Cache_Data"))
                                 + DirSize(Path.Combine(cacheRoot, "No_Vary_Search"));
                        }
                    case "ie_cache":
                    case "legacy_edge_cache":
                        {
                            var dir = Path.Combine(local, @"Microsoft\Windows\WebCache");
                            return Directory.Exists(dir) ? DirSize(dir) : 0;
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
                        return ScanInvalidContextMenu(item);
                    case "invalid_file_association":
                        return InvalidFileAssociationSize();
                    case "invalid_shared_dlls":
                        return InvalidSharedDllsSize();
                    case "invalid_mui_cache":
                        return InvalidMuiCacheSize();
                    case "invalid_appcompat_layers":
                        return InvalidAppCompatLayersSize();
                    case "invalid_appcompat_store":
                        return InvalidCompatStoreSize();
                    case "invalid_firewall_rule":
                        return InvalidFirewallRulesSize();
                    case "explorer_traces":
                        return ExplorerTracesSize();
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

                    
                    case "wechat_cache":
                        return WeChatCacheSize();
                    case "qq_cache":
                        return QQCacheSize();
                    case "wxwork_cache":
                        return WXWorkCacheSize();
                    case "mailmaster_cache":
                        return DirSize(Path.Combine(local, @"MailMaster\Cache"))
                             + DirSize(Path.Combine(local, @"MailMaster\Application\Cache"));

                    
                    case "qq_browser_cache":
                        return BrowserProfileCachesSize(Path.Combine(local, @"Tencent\QQBrowser\User Data"), new[] { "Cache", "Code Cache" });
                    case "vivaldi_cache":
                        return BrowserProfileCachesSize(Path.Combine(local, @"Vivaldi\User Data"), new[] { "Cache", "Code Cache" });

                    
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

                    default:
                        if (AppLogDirectories().TryGetValue(item.Id, out var logDirs))
                            return AppLogDirsSize(logDirs);
                        return 0;
                }
            }
            catch { return 0; }
        }

        
        
        
        private static long LnkFilesSize(string dir)
        {
            
            
            return DiskUtils.FilesSize(dir, "*.lnk");
        }

        
        
        
        
        
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

        
        private static long FilesByPatternSize(string dir, string pattern)
        {
            return DiskUtils.FilesSize(dir, pattern);
        }

        
        private static long ThumbIconCacheDbSize(string explorerDir)
        {
            if (!Directory.Exists(explorerDir)) return 0;
            return DiskUtils.FilesSize(explorerDir, "thumbcache_*.db")
                 + DiskUtils.FilesSize(explorerDir, "iconcache_*.db");
        }

        
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

            
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var c in new[]
            {
                Path.Combine(local, @"Tencent\QQ\Cache"),
                Path.Combine(local, @"Tencent\QQ\Code Cache"),
                Path.Combine(local, @"Tencent\QQ\GPUCache")
            })
                if (Directory.Exists(c)) total += DirSize(c);

            return total;
        }

        
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

        
        private void RefreshItems()
        {
            
            foreach (var item in AllCleanItems(CleanGroups))
            {
                ApplyDefaultCheck(item);
            }
            StatusText = "已刷新";
            long total = AllCleanItems(CleanGroups)
                .Where(c => c.IsChecked == true && c.Risk != RiskLevel.Forbidden && c.Status != ScanStatus.Cleaned)
                .Sum(c => c.Size);
            RecommendedTotalText = total > 0 ? $"{FormatSize(total)} 可清理" : "—";
            ApplyVisibilityFilter();
            UpdateDriveInfo();
        }

        
        
        public bool IsElevated => SystemOptimizeService.IsRunningAsAdministrator();

        private async Task RunOptimizeAsync(string action, OptimizeTool specificTool = null)
        {
            
            var tool = specificTool ?? OptimizeTools.FirstOrDefault(t => t.Action == action);
            if (tool == null) return;

            
            if (IsBusy) return;

            
            if (tool.IsRunning) return;

            if (tool.RequiresAdmin && !SystemOptimizeService.IsRunningAsAdministrator())
            {
                const string msg = "本操作需要管理员权限，请右键以“管理员身份运行”本程序后重试。";
                tool.StatusText = msg;
                OptimizeLogList.Add($"{DateTime.Now:HH:mm:ss} [{tool.Name}] 需要管理员权限");
                StatusText = $"{tool.Name}：需要管理员权限";
                return;
            }

            
            var cts = new CancellationTokenSource();
            _toolCtsMap[tool] = cts;

            tool.ClearOutput();
            tool.StatusText = "执行中...";
            tool.IsRunning = true;
            var progress = new Progress<string>(msg =>
            {
                tool.AppendOutput(msg);
                tool.StatusText = msg;
            });
            try
            {
                var result = await _optimizeService.ExecuteAsync(action, progress, null, cts.Token);
                if (cts.IsCancellationRequested)
                {
                    result = "操作已取消。";
                }
                tool.AppendOutput(result);
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
                if (_toolCtsMap.TryRemove(tool, out var removed))
                {
                    removed.Dispose();
                }
            }
        }

        private void CancelOptimize(OptimizeTool tool)
        {
            if (tool == null || !tool.IsRunning) return;
            if (_toolCtsMap.TryGetValue(tool, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
        }

        
        private async Task ScanEmptyFoldersAsync()
        {
            if (IsBusy || AnyToolRunning) return;
            SetIsBusy(true);
            try
            {
                _cts = NewCts();
                StatusText = "正在扫描空文件夹...";
                EmptyFolderList.Clear();
                EmptyFolderReportText = "扫描中...";

                
                
                var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                var folders = await _spaceAnalyzer.GetEmptyFoldersAsync(root, _cts.Token);

                EmptyFolderList.Clear();
                foreach (var f in folders) EmptyFolderList.Add(f);
                if (folders.Count == 0)
                {
                    EmptyFolderReportText = "未发现空文件夹。";
                }
                else
                {
                    EmptyFolderReportText = string.Join(Environment.NewLine, folders)
                        + Environment.NewLine + Environment.NewLine
                        + $"找到了 {folders.Count} 个空文件夹";
                }
                EmptyFolderStatusText = "";
                StatusText = "空文件夹扫描完成";
                UpdateDriveInfo();
            }
            catch (OperationCanceledException)
            {
                EmptyFolderReportText = "扫描已取消。";
                EmptyFolderStatusText = "";
                StatusText = "已取消";
            }
            catch (Exception ex)
            {
                _logger.Error("空文件夹扫描失败", ex);
                EmptyFolderReportText = "扫描失败：" + ex.Message;
                EmptyFolderStatusText = "";
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
                EmptyFolderReportText = "没有可删除的空文件夹。";
                EmptyFolderStatusText = "";
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
                
                
                foreach (var dir in EmptyFolderList.ToList()
                             .OrderByDescending(d => d.Count(c => c == '\\'))
                             .ThenByDescending(d => d.Length))
                {
                    try
                    {
                        
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
                    EmptyFolderStatusText = "";
                    StatusText = "空文件夹清理完成";
                });
            });
        }
    }
}
