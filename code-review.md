# DiskCleaner 代码审查报告（静态扫描）

> 范围：`Interop/`、`Services/`、`ViewModels/`、`Models/`、`Converters/`、`App.xaml.cs`、`ContextMenuMapping.cs`
> 方式：人工通读 + 反模式扫描（`OpenSubKey`/`async void`/空 catch/线程安全）
> 结论：**架构成熟、工程纪律好**，无明显崩溃 / 数据丢失级缺陷。以下是值得打磨的点与可引入的更优雅方案。

> **修复状态（2026-08-19）**：以下 5 处已全部应用——① 死代码三元已改为直接 `TryAssocQueryCached(keyName)`（行 831 / 944）；② `server` 已加 `using` 释放（行 781）；③ `Win11CachedGuids` 已改为线程安全 `Lazy<HashSet<string>>`（`.Value` 于行 1085 / 1088）；④ `_cts` 已通过 `NewCts()` 助手 + 三处 `finally { _cts?.Dispose(); }`（行 3390 / 3494 / 4364 新建，3475 / 3665 / 4397 释放）规范释放；⑤ `SpaceAnalyzer.IsExcludedDir` 已改为去掉盘符根后按路径段精确匹配，杜绝 `D:\Games\windows` 误排除。编译需用户手动执行 `build.bat` 验证（按习惯不自动构建）。

---

## 一、值得修复的问题（按严重度排序）

### 1. 【中】死代码三元，逻辑意图丢失 — `ContextMenuMapping.cs:837`
```csharp
var progName = LooksLikeProgIdPointer(keyName) ? keyName : keyName; // 两边恒为 keyName
```
`LooksLikeProgIdPointer(keyName)` 的判定结果被完全丢弃，这个调用除了浪费一次正则/遍历外没有任何作用。原始意图很可能是：命中 ProgID 才直接走 `AssocQueryStringW`，否则先用 `LookupFallbackZhName(keyName)`（中文兜底）兜底。当前写法在 keyName 本身就是非 ProgID 的中文子键名时，会跳过兜底字典直接 `TryAssocQueryCached(keyName)`，对该类项的中文解析不够稳健。
**建议**：删除该三元，改为
```csharp
if (LooksLikeProgIdPointer(keyName))
{
    var rProg = TryAssocQueryCached(keyName);
    if (!string.IsNullOrWhiteSpace(rProg)) return StripAccelerator(rProg);
}
```

### 2. 【低-中】RegistryKey 句柄泄漏 — `ContextMenuMapping.cs:787-790`
```csharp
var server = clsid.OpenSubKey("InprocServer32", false)
            ?? clsid.OpenSubKey("LocalServer32", false)
            ?? clsid.OpenSubKey("InprocServer64", false)
            ?? clsid.OpenSubKey("LocalServer64", false);
// server 读取后未 using 释放
```
`server` 经 `??` 短路链打开，命中那一个 RegistryKey 永不 `Dispose`。RegistryKey 有终结器，不会永久泄漏，但在右键全量扫描（每个 CLSID 解析都走这里）时会瞬间堆积大量未释放句柄，直到 GC。项目里其它 `OpenSubKey` 几乎都正确 `using`，这里是个孤例漏网。
**建议**：包成 `using var server = ...;`（`??` 链整体赋值给 `using var` 即可覆盖那个非空句柄）。

### 3. 【低】懒加载缓存非线程安全 — `ContextMenuMapping.cs:269-294`（`Win11CachedGuids`）
自定义 `get` 里 `if (_win11CachedGuids == null) { ... new ... }` 无锁，多线程并发首次扫描可能重复初始化（结果等价，但浪费一次全注册表枚举）。
**建议**：换成 `private static readonly Lazy<HashSet<string>> Win11CachedGuids = new(...);` 一行解决，自带线程安全。

### 4. 【低】`CancellationTokenSource` 未释放 — `MainViewModel.cs:39` + `DeepClean.cs:3390/3493/4362`
每次扫描 `new CancellationTokenSource()` 复写到 `_cts` 字段，旧实例从不 `Dispose()`。`CTS` 实现 `IDisposable`，持有内核 wait 句柄。虽被 `ScanCommand` 的 `!IsBusy` 谓词挡住并发扫描，但每次仍泄漏一个。
**建议**：重建前 `if (_cts != null) { _cts.Cancel(); _cts.Dispose(); }`，或在 `ScanAsync` 收尾 `finally` 中 Dispose。

### 5. 【低】空文件夹扫描的目录排除用后缀匹配，存在误伤 — `SpaceAnalyzer.cs:19-45`
`IsExcludedDir` 用 `EndsWith(suffix)`，其中 `\windows`、`\program files` 等是**尾段**匹配。若用户磁盘上真有 `D:\Games\windows` 这类目录，会被整棵跳过、不列为空文件夹结果也不递归——功能上无害（不删系统目录），但结果**不准确**。
**建议**：改为「路径段精确相等」判定（按分隔符 split 后比对最后一段），或至少把 `\windows` 收窄为完整盘根下的 `\\windows$` 形式。

### 6. 【低】大量 `catch { }` 吞异常，排查困难
贯穿 `ContextMenuMapping` / `DeepClean` / `UserFolderService` 多处 `catch { }`（或仅 `catch (Exception ex) { _logger?.Error }`）。在稳定路径上可接受，但像 `IsSystemContextMenuItem`、`ResolveClsidName` 这种判定失败直接 `return false/null`，失败时与「确实不是系统项」无法区分。
**建议**：对关键判定路径至少记录 `Debug` 级日志（项目已用 `Debug.WriteLine` 做了一部分，可统一到 `_logger?.Debug`）；纯可忽略的（如「用户无权限读某键」）保留空 catch 但加行内注释说明为何可忽略。

### 7. 【信息】`LoadIndirectString` 与 `TryResolveIndirectString` 双重 `@` 前缀判断 — `NativeMethods.cs:27-40` vs `ContextMenuMapping.cs:848-855`
`TryResolveIndirectString` 已先判断 `!s.StartsWith("@")` 直接返回；而 `LoadIndirectString` 内部又判断一次。冗余但无害。可统一只在外层判断，或让 `LoadIndirectString` 契约明确「仅接受 @ 形式」并去掉内层判断。

---

## 二、可引入更优雅 / 更"高深"方法的地方

### A. 【最大维护收益】超大映射表改为数据驱动
`_displayNameFallback`（~188 条）、`_clsidResText`（30 条）、`BuiltInSystemVerbs`、`MapMemoryBrand`、`TranslateProductName` 的 exact 字典、`IsVirtualGpuName` 列表——都是**硬编码的纯数据**。
- 现状问题：新增一个软件的中文名要改代码、重新编译；社区想贡献映射也得碰 C#。
- 更优方案：抽成**嵌入的 JSON / .resx 资源**（`EmbeddedResource`），启动时一次性反序列化到字典。既保持 `OrdinalIgnoreCase` 查找性能，又把"数据"与"逻辑"解耦，欢迎 PR 也更容易测试。

### B. `StaShellExecutor` 用 `Channel<T>` / `GetConsumingEnumerable` 取代轮询
现状：`BlockingCollection` + `TryTake(out item, 250)` **每 250ms 轮询一次**，且 wedged 线程是"永久卡死 + 下个 Run 才重建"的延迟策略。
- 更优雅：`BlockingCollection.GetConsumingEnumerable()` 配合 `CancellationToken` 即可**零延迟唤醒 + 干净退出**；或升级为 `System.Threading.Channels.Channel<WorkItem>`（无锁、背压天然、取消一流支持）。退出时 `CompleteAdding()` + `foreach` 自然结束，无需靠 `_thread == Thread.CurrentThread` 这种"自我淘汰"判断。

### C. `[GeneratedRegex]`（.NET 7+ 源生成正则）
`_clsidRegex`、`_accelRegex` 已用 `RegexOptions.Compiled`，但 `Compiled` 仍是**运行时 JIT 编译**一把。右键扫描每条目都走这些正则，属热路径。
- 更优：用 `[GeneratedRegex(...)]` 源生成器，编译期生成类型化匹配器，**零运行时 JIT 开销 + 编译期语法校验**（把"正写出了 bug 只在运行时爆"提前到编译时）。项目已是 net8.0，直接可用。

### D. `ConcurrentDictionary.GetOrAdd` 取代 `lock (_assocCache)`
`_assocCache` 用 `lock` 保护读写，逻辑正确。但 `AssocQueryStringW` 调用本身较重，用 `ConcurrentDictionary.GetOrAdd(key, _ => NativeMethods.GetAssocFriendlyDocName(key))` 可去掉显式锁、减少锁竞争，且代码更短。

### E. `ReadOnlySpan<char>` 路径解析（热路径微优化）
`IsUnderWindowsDirectory` / `ExtractFirstExecutablePath` / `IsSystemResourceReference` 大量 `Trim`/`Substring`/`IndexOf` 产生中间字符串。右键全量扫描时每条目多次调用，分配压力可观。
- 可改用 `ReadOnlySpan<char>`（`s.AsSpan().Trim().Slice(...)`）**零分配**完成路径前缀/分隔符判定。属于锦上添花，非必须。

### F. `Path.Exists`（.NET 7+）替代 `File.Exists` + `.exe` 兜底
`IsDeadFileReference` 的 `if (!Path.HasExtension(exe) && File.Exists(exe + ".exe"))` 可简化为 `Path.Exists(exe) || Path.Exists(exe + ".exe")`（语义更清晰，且 `Path.Exists` 对无扩展名的文件/目录统一处理）。

---

## 三、架构亮点（确认无回归，保持现状）

- ✅ **`IsDeadFileReference` 的 `ResolveExecutableFromUnquoted` 修复到位**：未加引号命令从前往后累加空格片段找首个真实 exe，不再在第一个空格截断误杀 `C:\Program Files\WinRAR\Uninstall.exe`。
- ✅ **WOW6432Node 双视图扫描到位**：`GetInvalidFileAssociations` 显式扫 64/32 位 HKCU/HKLM，32 位程序（Winamp 等）的失效关联不再漏判。
- ✅ **STA 线程执行 shell 调用 + 超时收敛 + wedged 重建**：`StaShellExecutor.Run` 把任意超时收敛到 120s 上限，绝不无限等待，超时后重建线程接管。
- ✅ **reparse point 全程跳过**：`EnumerationOptions.AttributesToSkip = ReparsePoint` 配合 `RemoveReparsePointChildren` 仅删链接不跟随，杜绝误删 junction 目标。
- ✅ **回收站兜底 + 重启删除兜底**：`CleanService` 先送回收站、失败 `File.Delete`、仍失败 `MoveFileEx` 标记重启删；统计支持部分成功，不因个别文件失败把整项标失败。
- ✅ **右键删除前 `.reg` 备份 + 运行期可撤销**：`DeleteContextMenuItemCore` 先导出备份再删，退出才清备份，安全可逆。
- ✅ **枚举即取大小**：`EnumerateFileSystemInfos` + 缓存 `entry.Length`，避免逐文件 `new FileInfo().Length` 重复 stat。

---

## 四、优先级建议

| 优先级 | 项 | 收益 |
|---|---|---|
| P1 | #1 死代码三元（显示名边缘 case） | 修正潜在中文名解析错误，改动小 |
| P1 | #2 RegistryKey 泄漏 | 大规模扫描句柄卫生 |
| P2 | #3 Lazy 包装 / #4 CTS Dispose | 消除重复初始化与句柄泄漏 |
| P2 | A 大字典数据驱动 | 最大长期维护收益，欢迎社区贡献 |
| P3 | B Channel / C GeneratedRegex / D ConcurrentDictionary | 现代化、性能与可读性 |
| P3 | #5 排除匹配精确化 / E span / F Path.Exists | 准确性 / 微优化 |

整体评价：**代码质量在同类国产系统工具里属上乘，方向是对的**。上述 P1/P2 是"顺手就能改"的卫生项，A/B/C 是"值得投入"的架构升级。不建议为了"高深"而引入不必要的复杂度——当前 STA 执行器与注册表清理逻辑已经相当工程化。
