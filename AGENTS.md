# ClassDailyLand —— Agent 快速上手手册

> 生成时间：2026-10-23 ｜ 目标读者：接手本仓库的 agent（先读第 0、9、10 节即可开工）

## 0. 30 秒速览

- **是什么**：把原 PySide6 版「班级日常」桌面应用重写为 **C# / .NET 8 / WPF 单工程**；**整个界面（设置中心 / 二级浮层 / 灵动岛 / 副岛 / 下拉面板 / 主窗）都是原生 XAML**，样式照 WebView 设计源逐条对位。
- 2026-10-24：灵动岛 / 副岛 / 下拉面板从 `OnRender` 手绘改为 XAML 视觉树（状态机仍留代码侧），动画收敛到 `Theme/Anim.cs`。改这三个窗口前先读 §7 的迁移说明。
- 2026-10-24：设置中心照 QML 版重做栅格与转场（1232×762 / 288 侧栏 / 280ms 整段平移 0.92 / 单条滑动强调条）；接入 DWM 云母与 Win10 亚克力（圆角只用系统原生）。
- 2026-10-24：插件宿主完成（契约工程 + 48 个 `IPluginHost` 成员 + 九个显示位，见 §12 与 `docs\plugins.md`）。
- 2026-10-24：两个内置集成搬进来 —— **媒体（SMTC）** 与 **灾害预警（地震倒计时 + 气象）**，见 §13。
- 2026-10-24：触摸上/下滑动全站可用（`PanningMode.VerticalOnly`），触屏优化开关只放大点击目标，见 §14。
- **工作目录**：`E:\Code\Class Daily Land 2 - 副本\Class Daily Land 2\`（slnx 在根，唯一工程在 `Class Daily Land 2\`，测试在 `tests\`）
- **三条命令**：
  ```powershell
  dotnet build "Class Daily Land 2.slnx"
  dotnet test  tests/ClassDailyLand.Core.Tests/ClassDailyLand.Core.Tests.csproj
  dotnet build "Class Daily Land 2\Class Daily Land 2.csproj"   # 改过 Core 后单独再跑，增量有时不传
  ```
- **改代码前必看**：第 8 节「坑清单」（PowerShell 编码 / XML 注释 / TemplateBinding / AlternationIndex），能省掉一整轮返工。

## 1. 三份源码的关系（谁是权威）

| 位置 | 角色 | 用它的什么 |
| --- | --- | --- |
| `E:\Code\Class Daily Land\app\app\*.py` | **原版 PySide6 完整实现** | 行为、文案、状态机、JSON 结构（`dynamic_island.py` / `gui.py` / `sub_island.py` / `panel_window.py` / `menu.py` / `schedule.py` / `randoms.py`） |
| `E:\Code\Class Daily Land\app\app\ui\` | **设置中心的 WebView 设计源**（唯一权威界面稿） | `styles\tokens.css`（色值/度量/动效）、`styles\app.css`（几何）、`lib\controls.js`（控件清单与交互）、`lib\editors.js`（浮层外壳）、`lib\icons.js`（图标）、`pages\app.js`（8 页字段与文案）、`pages\dialogs.js`（6 个二级浮层）、`pages\market.js`（插件页）、`docs\screenshots\*.png`（实拍对照图）、`README.md`（RPC 契约与踩坑记录） |
| 本项目 `Class Daily Land 2\` | 重写目标 | 只实现「.NET 原生」这一支；`ui/README.md` 明确：岛 / 主窗 / 面板保持原生，设置中心才 Web 化 —— 我们是按同一取舍做 XAML |

> 注意：机器上还装着**原版 exe**（`C:\Users\Admin\AppData\Local\Class Daily Land\app\Class Daily Land.exe`）。它的单实例 Mutex 名与本项目不同（`ClassDailyLand_SingleInstance_Mutex` vs `Local\ClassDailyLand.SingleInstance.v1`），互不干扰，但它会占桌面/前台，截图排查时先确认抓的是哪个进程。

## 2. 目录地图（每个文件一句职责）

```
Class Daily Land 2\Class Daily Land 2.csproj   net8.0-windows + UseWPF + UseWindowsForms + RollForward
                                               Using 补丁、settings\** 复制、CommunityToolkit.Mvvm 8.4.2
App.xaml(.cs)                                  入口：合并 6 本资源字典；单实例锁 + 命名事件唤醒 + 崩溃日志
AppController.cs                               装配中心：Manager 与窗口的唯一持有者；1s tick；设置分发；
                                               菜单/托盘/浮层入口（OpenDialog → 设置窗内浮层）
AssemblyInfo.cs                                ThemeInfo

Themes\                                        设计令牌与控件样式（XAML 版本的「ui/」）
  Tokens.Base.xaml                             字体/字号/行高/圆角/度量/动效（与深浅色无关）
  Tokens.Light.xaml / Tokens.Dark.xaml         两套色板（变量名对齐 WinUI3 资源键；rgba→#AARRGGBB）
  Icons.xaml                                   33 个描边 Geometry（由 icons.js 转写）
  Controls.xaml                                基础控件样式：Icon/Button 4 变体/标题栏按钮/TextBox 3 变体/
                                               FluentToggle/Slider/NumberBox/ComboBox/SelectorBar/
                                               ListBox/ScrollBar/ToolTip + FluentFocusVisual
  Controls.Sections.xaml                       SettingRow/LinkRow/Card/Badge/InfoBar/SettingGroup
  DialogStyles.xaml                            二级浮层 ed-* + 编辑控件尺寸 + EdCell/EdRowLabel 等

Views\
  SettingsWindow.xaml(.cs)                     设置中心外壳：48px 标题栏（WindowChrome）+288px 固定侧栏
                                               （药丸导航 + 单条滑动强调条）+内容区 28/900
                                               +二级页推进层（SubScroll）+窗口内确认框+提示条槽
  Pages\{General,Appearance,Island,Schedule,Homework,Plugins,Advanced,About}Page.xaml
                                               8 个页面（版式与文案对位 app.js）
  Pages\Pages.cs                               8 个 partial 类的代码后置（取 VM 一律在 Loaded）
  Dialogs\DialogPanel.cs                       浮层基类（头/身/脚 + EdPanel 样式 + 卡片/行/输入小构件）
  Dialogs\ScheduleDialogs.cs                   HolidaysDialog / WeekendDialog / DutyDialog / TempAdjustDialog
  Dialogs\TimetableDialog.cs                   课表管理（多课表 + 节次 + 课程格，仅周一~周五）+ 导入导出 + 命名小窗
  Dialogs\StatusTestDialog.cs                  状态测试（调引擎试算 + 胶囊预览）
  Dialogs\HomeworkEditor.cs                    HomeworkEditor（内嵌/独立两用）+ HomeworkDialog
  DialogLauncher.cs                            浮层统一入口 + OpenFolder + 窗口内确认框

Integrations\（内置集成，不是插件，随程序编译）
  Audio\SmtcBridge.cs                          SMTC 桥（PS + WinRT），每 400ms 读一次；
                                               换歌时把封面写成 art.png（先 .tmp 再改名）
  Audio\AudioService.cs                        "要不要显示、显示什么"：暂停留 5s、上课停轮询、
                                               封面按 rev 缓存（BitmapImage OnLoad + Freeze）
  Audio\MediaCardWindow.xaml(.cs)              点岛上的媒体片段展开的卡（封面 64 + 播/停，6s 自动收）
  Disaster\QuakeCalc.cs                        Haversine / 烈度 / 横波到达 / 震级换算（逐行对齐 Python）
  Disaster\QuakeTables.cs                      深度分档查表（脚本从 quake_calc.py 生成，勿手改）
  Disaster\Cities.cs                           省市县检索，数据是内嵌的 Resources\cities.json
  Disaster\DisasterSources.cs                  ICL 主源 + Wolfx 四源 + nmc 气象；8s 超时 + 指数退避
  Disaster\DisasterService.cs                  轮询 / 评估 / 红条接管 / 提醒去重节流 / 演练

Theme\TouchMode.cs                             触摸滑动（无条件开 PanningMode）+ 触屏点击目标放大（可开关）
Resources\cities.json                          31 省 / 366 市 / 2817 区县经纬度（EmbeddedResource）

ViewModels\SettingsViewModel.cs                强类型绑定属性（约 30 个）+ 滑块 200ms 防抖 +
                                               外部改动按键回刷 + 页面/键归属；文件头有 web↔本项目键映射表

Core\                                          纯逻辑，无任何 UI 依赖（测试只覆盖这里）
  Engine\ScheduleEngine.cs                     状态机：Compute/CoursesFor/PeriodsFor/CoursesOfDay/DayTable
  Engine\ScheduleText.cs                       胶囊文案（与 Python 版逐字一致）
  Models\{ScheduleStatus,ScheduleModels,HomeworkModels,AppSettings}.cs
  Managers\{SettingsManager,ScheduleManager,DataManagers}.cs
  Storage\AtomicJson.cs                        临时文件 + File.Replace 原子写 + 坏文件隔离 .bad
  Storage\DataPaths.cs                         数据根（exe 旁 settings\，不可写回退 LOCALAPPDATA）
  Defaults.cs / Support.cs                     出厂数据 / Logger / Easing

Windows\                                       浮窗（XAML 视觉树 + 代码留状态机）
  MainBoardWindow.cs                           贴右贴顶、纯文字列（标题/值日/应到实到/作业按学科分组编号）
  DynamicIslandWindow.xaml(.cs)                灵动岛六态状态机；动画走 Anim，弧用 Path+ArcSegment
  SubIslandWindow.xaml(.cs)                    副岛（SetProvider 是插件注入点）
  PanelWindow.xaml(.cs)                        主岛下拉面板（今日课程；3s 自动收起）
  RollCallWindow.cs                            随机点名（10 人冷却）
  NumberInputDialog.cs                         出勤人数弹窗（web 设计里没有，属原生功能）

Plugins\（Core\Plugins）                       插件纯逻辑：Manifest / Catalog / State / Installer / Registry
App\Plugins\                                   插件宿主：LoadContext（可卸载 ALC）/ Manager / HostContext
..\ClassDailyLand.Plugin.Abstractions\         插件契约（IPlugin / IPluginHost 48 成员 / Specs，无 WPF）
..\samples\HelloPlugin\                        可编译示例插件
..\docs\plugins.md                             插件开发指南

Theme\Palette.cs                               岛/主窗/面板色板（固定深色，不随主题变）
Theme\Anim.cs                                  动画统一入口：缓动/时长取自 Tokens.Base.xaml
Theme\ThemeService.cs                          换 Tokens 字典 + 同步 Palette（含「同值不重复换」保护）
Controls\FluentControls.cs                     自定义控件实现（Icon/NumberBox/SelectorBar/SettingRow/
                                               LinkRow/InfoBar/Badge/Card/SettingGroup/FluentToggle）
Controls\Converters.cs                         BoolVis / StrVis / NotFirstRow / TimeOffset
Win32\Interop.cs, WindowUtil.cs                全部 P/Invoke、工作区与 DIP 换算
Tray\TrayIcon.cs                               托盘（WinForms NotifyIcon，三开关 + 设置 + 退出）
Resources\config.json, icon.ico                版本信息（阶段 4 用）、图标
settings\                                      出厂配置（构建时 PreserveNewest 复制到输出）
tests\ClassDailyLand.Core.Tests\               32 例 xUnit
docs\ROADMAP.md                                4 阶段路线图（含「阶段 1.5 = 设置中心 XAML 化」）
README.md                                      结构 / 命令 / 验收对照表 / 设置中心与浮层清单
```

## 3. 架构与三条主链路

**启动链**：`App.xaml.cs` → 单实例锁（非阻塞 `WaitOne(0)`，`AbandonedMutexException` 视为拿到）→ `AppController.Startup()`：`DataPaths` → Logger → 5 个 Manager `Load()` → `ThemeService.Apply(theme)` → 建三窗口（`Main/Island/SubIsland` + `IslandPanelHost`）→ 事件接线 → 托盘 → `DispatcherTimer` 1s tick。

**运行时链**：`Tick()` 每秒：`Duty.RollOverIfNeeded` → `Schedule.Compute(now)` → `Island.SetText/Update` → `SubIsland.Tick` → `DetectEdges`（上课/下课边沿 + `_alertInFlight` 防重）。岛的帧率只在动画/跑马灯/倒计时期间开（33ms）。

**设置链**：XAML 控件 → `SettingsViewModel` 属性 setter → `SettingsManager.Set/SetMany`（**立即原子写盘 + 广播**）→ `AppController.OnSettingsChanged`（主题 / 提醒参数分流 + 重新 `ApplySettings` 给三个窗口 + 托盘勾选）→ VM 的 `OnSettingsChangedExternally` 按 key→属性名精确回刷（自己触发的改动不回刷，避免打断拖动）。滑块走 `WriteDelayed`（200ms 合并落盘）。

**浮层链**：页面/菜单 → `DialogLauncher.Show(this, new XxxDialog(vm))` → `SettingsWindow.ShowOverlay`（遮罩 + 缩放淡入）→ ESC 或按钮 `CloseOverlay()`。菜单项（主窗右键）同样落到设置窗内的浮层，与 web 版一致。

## 4. 设置键

- 命名：本项目沿用 **snake_case**，键的定义与默认值/范围集中在 `Core\Models\AppSettings.cs` 的 `All` 列表；`PageKeys` 决定「本页恢复默认」覆盖哪些键。
- **web 键 → 本项目键**（差异集中在 `SettingsViewModel` 文件头）：
  `main_auto_scroll→homework_auto_scroll`、`main_scroll_interval→homework_scroll_interval_ms`、`main_scroll_step→homework_scroll_step`、`main_scroll_pause→homework_scroll_pause_ms`、`theme_mode→theme`、`anim_fade_window→fade_animation`、`anim_duration→animation_duration_ms`、`island_show_wakeup_anim→island_wake_animation`、`sub_island_collapsed→sub_island_default_collapsed`、`check_update_on_start→check_update_on_startup`；`settings_mica` / `anim_fade_panel` / `anim_fade_text` / `block_update_on_source` 为新增同名键。
- 单位差异：web 的 `main_width_ratio` / `main_opacity` 是 0..1 小数，本项目是**整数百分比**（映射层换算）。
- 类型安全：`AppSettings.FromRaw` 逐键校验，类型不符回退默认并记进 `RepairedKeys`；未知键放 `Extras` 原样保留（`Get` 已修成能读回 Extras，否则运行时新写的未知键存不进盘）。
- 现有键（30 个）：通用 10（含新 `block_update_on_source`）、外观 6（含新 3 个）、灵动岛 10、课表提醒 2、作业 1、`last_update_check`。

## 5. 数据文件与引擎语义

| 文件 | 结构 | 关键点 |
| --- | --- | --- |
| `settings.json` | 扁平键值 | 类型不符回退默认；未知键保留；改即写 |
| `schedule.json` | `ScheduleFile`：`timetables{name→{periods[],timetable{"0".."n"→[7]}}}`、`active`、`temp_adjust`、`advance_minutes`、`time_offset_seconds`、`holidays[]`、`makeup_days[]`、`weekend` | **周末课不进 timetable 的 5/6 列**，只在 `weekend`（否则「课表填了周末课、岛还是今日无课」） |
| `homework.json` | `[{subject,content,date}]` | 主窗按**学科分组 + 组内编号**显示 |
| `name.json` / `config.json` | 名单数组 / `{current_index,last_date,should,actual}` | 跨天自动顺延一位；`should/actual` 是**字符串** |
| `agreement.json` | `{agreed}` | 协议 |

- `Period` 三字段：`Name`（节次名）/`Start`/`End`（"HH:mm"）；`ScheduleStatus` 的 `Course` 是课程名、`Name` 是假期名 —— 别写反。
- 每日课程来源优先级：`TempAdjust`（当天覆盖）→ `MakeupDay`（按指定周几）→ 周末（上午取平日课 + 下班自定节次）→ 平日按周几列。

## 6. 界面规格速查（改观感时照这个）

| 项 | 值 |
| --- | --- |
| 外壳 | 标题栏 48px；侧栏 **288px 固定（QML 版没有汉堡折叠）**；内容区 `padding 28` + 列宽 900；页面转场 **280ms 整段平移 0.92、不淡化、按导航上下/子页左右**；导航强调条单条 200ms 跟随 |
| 窗口材质 | **Win11 → 云母（Mica，DWM 38=2）**；**Win10 1803+ → 亚克力（Acrylic，SetWindowCompositionAttribute）**；更老/关掉 → 不透明底色。**圆角只用 DWM 原生（33=2），Win10 保持原生直角，不做手工圆角** |
| 圆角 | 控件 6 / 卡片 12 / 磁贴 9（分区头磁贴 7）/ 浮层 14 / 导航药丸 8 |
| 行 | `min-height 62`、`padding 18,14`、行间 1px 分隔线（第一行不画） |
| 控件 | 按钮 32 高；开关 40×20（圆钮 12→14）；滑块 4px 轨道 + 20px 外圆 + 12px 内点、控件宽 170；数字框带 ▲▼ + 单位；ComboBox 浮出 10 圆角带勾选位；滚动条 12px（thumb 4px 透明边） |
| 色值 | 强调色 浅 `#005FB8` / 深 `#60CDFF`；卡片 浅 `rgba(255,255,255,.7)` / 深 `rgba(255,255,255,.0512)`；分隔线 浅 `rgba(0,0,0,.0803)` / 深 `rgba(255,255,255,.0837)`；关闭悬停 `#C42B1C` |
| 动效 | 167 / 187 / 200 / 250 / 350 / 667 ms；曲线用 `PowerEase(Power=3/4/5)` 与 `BackEase` 近似（WPF 的 `CubicEase` 没有控制点） |
| 岛 | 胶囊 `#1c1c1e` 不透明、20px 圆环（轨道 `#3a3a3c`、上课 `#30d158`、即将上课 `#4da3ff`）、白字 14px、倒计时蓝条 `#0a84ff` + 流光/彗星、状态 Hidden/Mini/Compact/Countdown/Alert/Banner。XAML 里圆环是 `Ellipse` 轨道 + `Path`(ArcSegment) 弧，`RenderTransform` 转 -90° 从 12 点起笔 |
| 面板 | `#1c1c1e` / 圆角 8 / 13px `#e6e6e6` / 内边距 12；点击主岛展开、3s 自动收起、超高滚轮 + 底部渐隐（原生 ScrollViewer + Rectangle 渐变） |
| 8 页 | 通用 · 外观 · 灵动岛 · 课表与提醒 · 作业 · 插件 · 高级 · 关于（字段逐条对位 `app.js`） |
| 6 浮层 | 课表管理 · 周末作息 · 假期与调休 · 值日生名单 · 临时调课 · 状态测试（+ 作业编辑器独立版） |

## 7. 构建 / 运行 / 环境

- 本机 SDK：`10.0.401`；目标框架 `net8.0-windows`；**本机没有 .NET 8 桌面运行时**，靠 `RollForward=LatestMajor` 跑在 .NET 10 桌面运行时上；缺运行时的报错只会写 stderr（`Start-Process -RedirectStandardError` 才看得到）。
- 输出：`Class Daily Land 2\bin\Debug\net8.0-windows\Class Daily Land.exe`（AssemblyName 带空格）。
- 数据：exe 旁 `settings\`；不可写时回退 `%LOCALAPPDATA%\ClassDailyLand\settings`；崩溃日志 `%LOCALAPPDATA%\ClassDailyLand\startup-crash.log`；运行日志 `settings\app.log`。
- 测试：`tests\ClassDailyLand.Core.Tests`（**103 例**）：课表引擎 / 原子写 / 设置模型 / 值日生顺延 /
  插件清单·扫描·启停·安装安全·显示位竞争 / 地震算法与城市检索 / 预警源解析 / 媒体暂停收起。
  UI 无自动化测试，靠临时冒烟工程（引用已生成的 dll + 手动合并主题字典）真开窗口断言。

## 8. 坑清单（踩过并已修，别重犯）

1. **XML 注释不能含 `--`**（连字符分隔线和 CSS 变量名 `--elevation-*` 都会让 XAML 编译失败 MC3000）。
2. **`TemplateBinding` 不支持 Converter**；要转换就用 `{Binding X, RelativeSource={RelativeSource TemplatedParent}, Converter=...}`。
3. **WPF 的 `CubicEase` 不暴露 `ControlPoint1/2`**（那是 UWP）；用 `PowerEase`（属性名是 **`Power`** 不是 `Exponent`）和 `BackEase`。
4. **缓动类型要显式映射命名空间**：`xmlns:anim="clr-namespace:System.Windows.Media.Animation;assembly=PresentationCore"`。
5. **`ItemsControl.AlternationIndex` 的落点分两种情况**：数据项会被包进生成的 `ContentPresenter` 容器，索引在容器上；**如果条目本身是 UIElement，ItemsControl 会让它自己当容器（`IsItemItsOwnContainerOverride` 返回 `item is UIElement`），索引就落在条目自身**。设置中心的 `SettingRow` / `LinkRow` 是后一种：分隔线用 `RelativeSource={RelativeSource TemplatedParent}` 读索引；改成 `AncestorType ContentPresenter` 会匹配到页面宿主的 ContentPresenter（索引恒 0），所有线都会消失。见第 10 节第 1 条。
6. **模板里具名的 `Transform` 不能当 Setter 的 TargetName**；要改就整体设 `RenderTransform`。
7. **WPF 没有 `color-mix()`**：用「原色 + `Opacity` 叠加层」实现磁贴 15%、导航选中 12%、InfoBar 10%/28%。
8. **隐式 TextBox 样式会渗进自定义控件的模板内部**（NumberBox 的 `PART_Input` 必须显式 `BorderThickness=0 / Background=Transparent / Height=Auto`）。
9. **`WindowChrome` 下的自绘按钮**要标 `shell:WindowChrome.IsHitTestVisibleInChrome="True"`，否则点不到。
10. **主题重复应用**：`ThemeService` 必须先判断「已经是目标主题」再换，否则 `MergedDictionaries` 会堆多份色板（也是个「值没变也当变化」的经典坑）。
11. **PowerShell 5.1 按 ANSI 读 .ps1**：脚本里出现中文就解析失败 → 脚本只写 ASCII，输出用 `Write-Output`；改文件一律走 `edit`/`write` 工具，或 `[IO.File]::ReadAllText/WriteAllText(path, UTF8Encoding($false))`（**文件是无 BOM 的 UTF-8**）。
12. **PowerShell 管道/控制台显示中文会乱码**：判定结果用 `$LASTEXITCODE`，或把输出 `Out-File -Encoding UTF8` 后 `Select-String`。
13. **改 Core 后单独 build 主工程**：`dotnet build` 的增量判断有时不把 Core 变更传到 App 输出。
14. **陈旧进程会占单实例锁**：冒烟前先 `Get-Process "Class Daily Land"` 清场。
15. **岛/面板/副岛已 XAML 化**，没有 `OnRender` 了；`_wakeStep` 定时器链仍要显式停旧的（曾出现重入竞态）。
16. **主题字典的 pack URI 要带程序集名**：`pack://application:,,,/Themes/x.xaml` 是相对**入口程序集**解析的，
  程序被别的宿主加载（冒烟测试/内嵌）就会 IOException。已改成 `"/<程序集名>;component/Themes/x.xaml"`。
17. **确认提示一律走 `SettingsWindow.Confirm()`**（回调式，画在窗口里），不要再加 `MessageBox.Show` ——
  用户明确要求"所有提示都在这个窗口里弹"。
18. **XAML 里枚举属性别写全名**：`ResizeMode="NoResize"` 而不是 `ResizeMode="ResizeMode.NoResize"`——后者编译期不报错，运行期 `EnumConverter` 才抛（表现是 `InitializeComponent` 崩）。
19. **动画前 Left/Top/Width/Height 必须有具体值**：WPF 默认 `NaN`，拿 NaN 当 `DoubleAnimation` 起点会抛 `AnimationException`（副岛就这么崩过）。
20. **云母/亚克力背板要求窗口不是 `AllowsTransparency`**：得用 `WindowStyle=None` +
  透明 `Background` + `GlassFrameThickness=-1` + `DwmExtendFrameIntoClientArea(-1)`；
  一旦开了 AllowsTransparency，DWM 就没有背景可画，云母永远不生效。
21. **窗口圆角不要手绘**：Win11 只用 `DWMWA_WINDOW_CORNER_PREFERENCE=2`，Win10 原生就是直角。
  用 Border+CornerRadius 假圆角会和 DWM 的裁剪打架，四角会露出底色。
22. **主窗（MainBoardWindow）`ShowInTaskbar=false`**：它是贴边挂件，任务栏只应留设置窗口一个条目。
23. **整页转场要给元素套 `BitmapCache`**，否则每帧重画整棵可视树，掉帧非常明显；
  动画结束后立刻 `CacheMode=null`，不然文字会发虚。
24. **首次布局别和动画抢同一帧**：先摆好起点位置，`Dispatcher.BeginInvoke(DispatcherPriority.Render,...)`
   下一帧再启动动画（进子页时的"顿一下"就是这么来的）。
25. **动画不要动 Canvas.Top 这类布局属性**，用 `TranslateTransform`；布局属性每帧都会触发父级重排。
26. 跑马灯要两份文字首尾相接，但**不滚时必须把第二份 Collapse**，否则它会以 +40px 间距露出头。
27. **媒体"暂停后要收起"不能看有没有曲目信息**：SMTC 暂停时**依然报着 title/artist**，
   拿 `info.HasContent` 判断会让片段永远收不起来。要看「最后一次真正在播放的时刻」
   （`_lastPlayingUtc` 只在 `Playing=true` 时刷新）。
28. **WPF 的 ScrollViewer 默认 `PanningMode=None`，手指按住是彻底不会动的**（只有滚轮/滚动条能滚）。
   手指滑动必须显式开 `PanningMode.VerticalOnly`；这是"基础能力"，不要藏在设置开关后面。
29. **灾害预警接管胶囊时，插件的加长段/徽标/媒体片段必须全部让位**：胶囊宽度是
   `基础 + 插件 + 徽标 + 媒体` 相加的，红条按 600 铺满时会和右边挂着的东西错位。
   `IslandInput.Emergency != null` 时 `ExtraTotal` 直接归零。
30. **SMTC 是 WinRT 类型，纯 P/Invoke 拿不到**：`GlobalSystemMediaTransportControlsSessionManager`
   只能通过 WinRT 投影访问。本项目走独立进程 + `System.WindowsRuntimeSystemExtensions.AsTask`
   （`Add-Type -AssemblyName System.Runtime.WindowsRuntime`）。踩过的点：
   `Dispose` 包装流会把底层 WinRT 流一起关掉（读完再 dispose）；文件要放在能读的路径，
   OneDrive 占位文件会让 `StorageFile.GetFileFromPathAsync` 抛异常。
31. **海量常量表用脚本从源生成，不要手抄**：`QuakeTables.cs` / `cities.json` 都是从 Python 源码生成的。
   手抄时把外推用的斜率四舍五入到 6 位小数，倒计时会差 6 秒（外推是 1e-8 级敏感）。
32. **冒烟测试别用 `dotnet run` 起完整 AppController 后再等 timer**：宿主每秒 tick 会去
   `Island.Show()`，而验证进程里 `Application.Current` 可能已收尾 → 抛"关闭窗口后无法 Show"。
   要么先反射停掉 `_tick`，要么只测静态状态（`Audio.Display` / 窗口资源）不触发 tick。

## 9. 当前状态

**已完成且已验证**（最后一次全绿：`build` 0 错 0 警 + `test` 32/32）
- 阶段 1：MVP 骨架（主窗 / 岛 / 副岛 / 面板 / 设置 / 托盘 / 引擎 / 原子写 / 单测）
- 阶段 1.5（本轮新增）：设置中心与全部二级浮层 **XAML 化**，8 页 + 7 个浮层（含作业编辑器两用）；令牌/图标/控件库三本资源字典；`ThemeService`；删除旧的代码绘制版（`Windows\SettingsWindow.cs(.Pages.cs)`、`Windows\Editors\*`、`Controls\BasicControls.cs`）；补 4 个设置键；单测 27→32。
- 依赖：`CommunityToolkit.Mvvm 8.4.2`（仅 INPC/命令，无控件）。

**已编译、待视觉验收** —— 用户报「显示 BUG、控件逻辑不起作用」后的一批修复，`build` 0 错 0 警、`test` 32/32（2026-10-23 实测）；还没在真实界面上逐处比对：
- `Themes\Controls.xaml`：搜索框占位文字改为默认 `Collapsed`（原来永不隐藏，输入的字压在提示上）；新增 `FluentFocusVisual` 并给 11 处交互控件样式挂上（原来导航项有 WPF 虚线焦点框）。
- `Controls\FluentControls.cs`：`SettingGroup` 由 `Control` 改为 **`ItemsControl`**（去掉 `Content` DP，保留 `CardPadding`）；`Themes\Controls.Sections.xaml` 的 SettingGroup 样式加 `AlternationCount=1000` + `ItemsPanel=StackPanel`、内容位置换成 `ItemsPresenter`。
- 7 个页面 XAML 去掉 `<fl:SettingGroup.Content><StackPanel>` 包装，行直接作为分区子项；`HomeworkPage.xaml` 已改为 `CardPadding="18"` 单独重写（脚本曾误删它的 `</StackPanel>`，现文本已平衡）。
- `SettingRow` / `LinkRow` 的分隔线：`TemplatedParent` 读 `AlternationIndex` 是对的（行是 UIElement，会自己当容器，索引就在行上；见第 8 节坑 5），并把线移到内边距之外，压在行边界上。**曾误改成 `AncestorType ContentPresenter`，导致匹配到页面宿主的 CP（索引恒 0），所有分隔线又消失。**

**已知未做 / 有意不做**
- 深浅色切换是整本字典替换（瞬间换色），没做 web 那套 200ms 交叉淡入；`theme=system` 时**没有监听系统主题变化**（改系统深浅需重开设置窗）。
- 插件市场（GitHub/Gitee/官方三源）未做：阶段 3 只做了本地安装与宿主，见 `docs\plugins.md`。
- 插件市场（GitHub / Gitee / 官方三源）未做（本地安装 + 宿主已完成）。
- 触屏只做了「手指滑动 + 点击目标放大」，没做手写输入 / 长按菜单 / 双指缩放。
- 出勤人数仍走 `NumberInputDialog`（web 稿里没有这个界面）。
- 导航搜索框的匹配是「标签 **或 页面 id**」，会让输入 `d` 命中 灵动岛/课表与提醒/高级（按 id 命中）；web 版只匹配标签 —— 需定夺是否收紧。

## 10. 下一步任务清单（按优先级，含精确修法）

1. **行分隔线**（已完成 2026-10-23，见第 8 节坑 5）：`SettingRow` / `LinkRow` 是 `ItemsControl` 的直接子项且是 UIElement，会自己当容器，所以 `AlternationIndex` 落在行控件本身；模板里的分隔线用 `RelativeSource={RelativeSource TemplatedParent}`。**不要**改成 `AncestorType={x:Type ContentPresenter}`（会匹配到页面宿主的 CP，索引恒 0 → 全部分隔线 Collapsed）。
2. **跑起来对照实拍图**：启动 → 打开设置 → 与 `ui\docs\screenshots\settings-general-light.png`、`settings-appearance-dark.png`、`settings-island-light.png`、`dialog-weekend.png` 逐页比对；顺便验证「控件逻辑」（开关是否改 settings.json、滑块是否 200ms 后落盘、导航是否切页、浮层是否开关正常）。建议顺手加一个 `--settings` 启动参数（直接开设置窗），比点托盘快得多。
3. 导航搜索收敛为「只匹配标签」（或标签 + 页面关键词，不含 id），与 web 版一致。
4. 主题：加系统主题变化监听（`Microsoft.Win32.SystemEvents.UserPreferenceChanged`）+ 可选的 200ms 颜色过渡。
5. ~~阶段 3：插件宿主~~ **已完成 2026-10-24**，见第 12 节。
6. 阶段 4：插件市场、Mica、Banner 态、真实更新源（`Resources\config.json` 的 `version_url` 已在）。

## 11. 调试手册

- **截图**：`Add-Type System.Windows.Forms/Drawing` → `CopyFromScreen` 存 PNG；窗口被遮挡时用 `PrintWindow` 按句柄抓，并用 `GetWindowThreadProcessId` 过滤出自己进程的窗口。
- **看进程**：`Get-Process "Class Daily Land" | Select Id,MainWindowTitle,MainWindowHandle`；进程活着但没窗口 → 多半死在「请安装 .NET 框架」对话框（用 stderr 重定向确认）。
- **验证设置写入**：改一个开关后 diff `bin\...\settings\settings.json`（原子写不留 `.tmp`；坏文件会被改名 `.bad`）。
- **驱动界面做冒烟**：可用 `System.Windows.Automation`（UIA）按 Name 找到开关/导航项并 Invoke，再回读 JSON 判定 —— 无需人工点。

## 12. 插件系统（阶段 3，2026-10-24 完成）

### 分层

| 层 | 位置 | 职责 |
| --- | --- | --- |
| 契约 | `ClassDailyLand.Plugin.Abstractions`（独立工程，**无 WPF**） | `IPlugin` / `IPluginHost`（48 成员）/ 9 个显示位 Spec |
| 纯逻辑 | `Class\Plugins\` | 清单校验、目录扫描、启停状态、安装、显示位注册表 |
| 宿主 | `App\Plugins\` | 可卸载 ALC 加载器、PluginManager、IPluginHost 实现 |
| 界面 | `Views\Pages\PluginsPage.xaml(.cs)` | 已安装列表 / 启停 / 重载 / 卸载 / 安装 / 日志 |

### 显示位与竞争

单值位（岛文案 / 岛加长段 / 岛徽标 / 副岛 / 主窗徽标）按 `Priority` 取最大；
**回调返回 null 视为"本秒没内容"，继续问下一个候选**（`FirstNonNull`），
所以高优先级插件临时没东西时不会把显示位空着。
多值位（面板 / 主窗区块 / 主窗菜单 / 托盘菜单）全部显示，菜单按 `Order` **升序**。

### 宿主接入点

`AppController.WirePlugins()` 把注册表接到：
`PluginIslandTextProvider`、`PluginExtraWidthProvider`、`IslandInput.Badge`、
`SubIsland.SetProvider`（插件优先、内置兜底）、`IslandPanelHost.PluginPanelsProvider`、
`MainBoardWindow.BoardSectionsProvider / BoardBadgeProvider / BoardActionsProvider`、
`TrayIcon.PluginActions`。
窗口侧只拿 `Func`，不认识 `PluginManager` —— 没有插件时窗口能独立跑（冒烟直接 new）。

### 踩过的坑

1. **契约 DLL 必须由默认上下文提供**：`PluginLoadContext.Load` 对
   `ClassDailyLand.Plugin.Abstractions` 返回 `null`，否则插件加载到自己的副本，
   `typeof(IPlugin).IsAssignableFrom` 恒为 false、`Initialize` 永远不被调用。
   示例工程因此写 `Private=false` + `ExcludeAssets=runtime`。
2. **岛徽标宽度必须在算几何之前定**：`Update()`/`SetState()` 里先 `ApplyBadge()`（用
   `FormattedText` 量，不依赖布局），否则胶囊宽度慢一帧。
3. **载入失败要连带清注册**：插件在 `Initialize` 中途抛异常时，已经注册的显示位会指向
   半死的实例 → `catch` 里 `Registry.RemovePlugin(id)`。
4. **主窗区块要按内容签名去重**：每秒都会问一次，直接重建会让作业列表滚动条每秒被拉回顶部。
5. **安装解压要逐条校验落点**：`ZipArchive` 的 `ExtractToDirectory` 之外自己实现，
   防 `../`、限条目数与 64MB 总量；覆盖安装先改名备份、失败回滚。
6. **插件日志可能来自插件线程**：`LogAppended` 订阅方要自己切回 UI 线程。

## 13. 内置集成：媒体 / 灾害预警（2026-10-24 搬自 Python 版）

> 这两块**不是插件**，随主程序编译，和课表引擎同一个 tick 驱动。
> 入口都在 `AppController`：`Audio.Tick(status.IsOngoing)`、`Disaster.Tick()`。

### 13.1 媒体（`Integrations\Audio`）

```
SmtcBridge      独立进程跑 WinRT SMTC，stdout 每行一条 JSON（title/artist/playing/rev/art）
   ↓ 事件
AudioService    翻译成"要不要显示、显示什么"；封面按 rev 缓存
   ↓ MediaDisplay
DynamicIslandWindow.MediaHost   Border：封面 24 + 歌名/歌手 + 播/停按钮（Grid 三列，按钮贴右端）
   ↓ 点击
MediaCardWindow  展开卡（封面 64 + 歌名 + 播/停，6 秒自动收）
```

- **为什么不用 P/Invoke**：`GlobalSystemMediaTransportControlsSessionManager` 是 WinRT 类型，
  传统 P/Invoke 拿不到；加 Windows SDK 投影又要装包。所以走"另起一个进程读 WinRT、用 JSON 传回来"。
- **暂停 5 秒收起**（`audio_hide_after_sec`）：判定看 `_lastPlayingUtc`（只在 `Playing=true` 时刷新），
  **不是**看有没有曲目信息 —— 暂停时 SMTC 依然报着歌名，看曲目信息会导致永远收不起来。
- **上课期间整块隐藏 + 停轮询**：省电，也避免在讲台上弹歌名。
- **胶囊宽度**：媒体片段宽度由 `audio_max_width`（120~400）决定，并**加进** `ExtraTotal`。
- 设置页「媒体」页有「注入测试媒体 / 清除测试」，没有正在播放的音乐也能预览片段。

### 13.2 灾害预警（`Integrations\Disaster`）

```
DisasterSources   ICL 列表/详情 + Wolfx 四源兜底 + nmc 气象（8s 超时、指数退避、按 uid 合并去重）
   ↓ 事件
DisasterService   评估 → 是否需要预警 → Emergency（文案/比例/条色）或 SubIsland / Panel
   ↓ IslandInput.Emergency
DynamicIslandWindow  进 Countdown 态：红条 #FF453A + 「灾害预警 · 震中 M6.4 · 横波还有 N 秒」
```

- **算法逐行对齐 Python**：Haversine → 按震源深度分档查表（0~2181 km，101 列）→ 超出表尾线性外推。
  查表 `QuakeTables.cs` 与城市表 `Resources\cities.json` 都是脚本从 Python 源码生成的（见坑 31）。
- **红条接管时全部让位**（坑 29）：`ExtraTotal` 归零，徽标与媒体片段 `Collapsed`，胶囊固定 600。
- **点胶囊 = 确认收起**（`AppController.OnIslandClicked` 里先判 `Disaster.Emergency`）。
- **位置只选到省也能用**：`Cities.CoordOf` 拿不到时退到省会（省的第一个地级市），否则倒计时永远算不出来。
- 提醒：uid 去重 + 节流（`disaster_throttle_sec`）+ 等级升级再提醒。
- 设置页「灾害预警」有省市县三级联动与**「模拟一次地震预警」**（`StartDrill()`），不用等真地震。

### 13.3 相关设置键

```
audio_enabled / audio_show_on(main|sub) / audio_hide_after_sec / audio_max_width / audio_show_title
disaster_enabled / disaster_quake_enabled / disaster_weather_enabled
disaster_province / disaster_city / disaster_district / disaster_lat / disaster_lon
disaster_threshold_x10 / disaster_quake_interval_sec / disaster_weather_interval_sec
disaster_throttle_sec / disaster_notify_enabled / disaster_banner_enabled
disaster_small_hold_sec / disaster_show_status_card
```

改了这些键 → `AppController` 的 `SettingsChanged` 里按前缀分发：
`audio_*` → `Audio.ApplySettings()`，`disaster_*` → `Disaster.ApplySettings()`。

---

## 14. 触屏（2026-10-24）

实现在 `Theme\TouchMode.cs`，**两件互不相干的事**：

| 能力 | 默认 | 实现 |
| --- | --- | --- |
| **手指上/下滑动** | **始终开启** | `EnableTouchScroll(viewer)`：`PanningMode=VerticalOnly` + `PanningDeceleration=0.0006`。设置页（内容区/侧栏/二级页）、下拉面板、主窗作业列表都调了 |
| **点击目标放大** | 关（设置 → 交互 → 触屏优化） | `Apply(window, on)`：覆盖窗口资源的 `ControlHeight 32→44`、`ControlHeightSm 28→38`、`RowMinHeight 62→82`、`NavItemHeight 40→54` |

要点：

- **滑动不能藏在开关后面**（坑 28）。WPF 的 `ScrollViewer` 默认 `PanningMode=None`，
  手指按住是**彻底不动**的 —— 触摸屏用户会以为界面卡死。所以 `EnableTouchScroll` 无条件调用。
- **尺寸用窗口资源覆盖**，不是改主题字典：模板里全是 `DynamicResource`，索引器一赋值整窗重解析，
  不重建可视树、不重启；关掉设置窗就还原，不会把触屏尺寸带到岛和主窗。
- 不要设 `IsManipulationEnabled=true`：`PanningMode` 内部已经接管 manipulation，
  再开会把子元素的单击吞掉（按钮点不动）。
- 验证姿势：断言 `ScrollViewer.PanningMode == VerticalOnly` **且** `IsManipulationEnabled == true`
  （后者是"ScrollViewer 真的接管了触摸"的直接证据）；`ScrollContentPresenter.PanningMode` 读不到，
  别拿它当断言（它没有公开的属性包装）。

---

## 15. 文档与许可证

| 文件 | 内容 |
| --- | --- |
| `README.md` | 项目介绍 / 结构 / 插件系统 / 内置集成 / 触屏 / 构建 / 验收清单 |
| `AGENTS.md` | 本文件（agent 上手手册） |
| `docs\plugins.md` | 插件开发完整指南（清单字段 / 九个显示位 / 48 个 API / 实战样例 / 打包 / 调试 / 排错） |
| `docs\ROADMAP.md` | 阶段划分与完成状态 |
| `LICENSE` | MIT |

> 改完代码记得同步文档：这几份是唯一的交接材料，代码在而文档没跟上，下一个人（或 agent）会走一遍你踩过的坑。
