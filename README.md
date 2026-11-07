# ClassDailyLand2（班级日常）

C# / WPF / .NET 桌面版班级日常管理应用（原 PySide6 版重写）。

托盘常驻 + 贴右主窗 + 顶部灵动岛，实时显示课表状态、作业、值日与出勤；
另带三个**内置集成**：天气（实时天气 + 天气动画）、媒体（Windows SMTC）
与灾害预警（地震倒计时 + 气象预警），以及一个**插件系统**（插件可往四处显示位挂内容）。

## 结构

单解决方案，四个工程 + 文档：

```
Class Daily Land 2/                     主工程（WinExe）
  App.xaml / App.xaml.cs                入口：合并主题字典、ShutdownMode、单实例锁、异常留痕
  AppController.cs                      装配中心：1s tick、上课/下课边沿、设置分发、集成调度
  App/Plugins/                          插件宿主（可卸载 ALC / 管理器 / IPluginHost 实现）
  App/Market/MarketService.cs           插件市场编排（拉索引 / 下载校验 / 交给安装器）
  Themes/                               设计令牌与控件样式
    Tokens.Base.xaml                    字体 / 字号 / 圆角 / 度量 / 动效曲线（与深浅色无关）
    Tokens.Light.xaml / Tokens.Dark.xaml 浅深两套色板（ThemeService 运行时整本替换）
    Icons.xaml                          32 个描边图标几何
    Controls.xaml                       Button/Toggle/Slider/NumberBox/ComboBox/TextBox/ScrollBar 等
    Controls.Sections.xaml              SettingRow / LinkRow / Card / SettingGroup / InfoBar / Badge
    DialogStyles.xaml                   二级浮层与编辑控件样式
  Views/
    SettingsWindow.xaml(.cs)            设置中心外壳：48px 自绘标题栏 + 288px 固定侧栏
                                        Win11 云母 / Win10 亚克力（圆角由 DWM 原生添加，不手绘）
                                        内容区 padding 28 / 列宽 900；二级页整页推进 + 面包屑
                                        所有提示都在窗口内（不弹 MessageBox）
    Pages/*.xaml                        首页 / 通用 / 灵动岛 / 媒体 / 天气 / 灾害预警 / 交互 /
                                        个性化 / 课表与提醒 / 课表编辑 / 作业 / 插件 / 高级 / 关于
    Pages/Pages.cs                      各页需要跑代码的少量交互（取 VM 的时机在 Loaded）
    Dialogs/                            周末作息 / 假期与调休 / 值日生 / 临时调课 /
                                        状态测试 / 作业编辑器，全部是窗口内浮层
    DialogLauncher.cs                   浮层统一入口 + 打开目录
  ViewModels/SettingsViewModel.cs       强类型绑定属性 + 滑块 200ms 防抖 + 外部改动按键回刷
  Core/                                 纯逻辑（无 UI）
    Engine/ScheduleEngine.cs            课表状态机（upcoming/ongoing/holiday/none/done）
    Engine/ScheduleText.cs              胶囊文案格式（与 Python 版一致）
    Models/                             ScheduleStatus / ScheduleFile / AppSettings / ...
    Managers/                           Settings / Schedule / Homework / StudentOnDuty / Agreement
    Plugins/                            插件纯逻辑：清单 / 扫描 / 启停 / 安装 / 显示位注册表
    Market/                             插件市场纯逻辑：索引解析 / 双源取数 / 状态判定 / 下载校验
    Storage/AtomicJson.cs               临时文件 + File.Replace 原子写
    Storage/DataPaths.cs                数据根（输出目录 settings\，不可写回退 LOCALAPPDATA）
  Integrations/                         内置集成（不是插件，随程序一起编译）
    Audio/SmtcBridge.cs                 PowerShell + WinRT 桥，每秒一行 JSON（曲名/歌手/状态/封面）
    Audio/AudioService.cs               采样翻译成"要不要显示、显示什么"（暂停 5s 后收起）
    Audio/MediaCardWindow.xaml(.cs)     点胶囊媒体片段后的展开卡（封面 64 + 播/停）
    Disaster/QuakeCalc.cs               烈度 / 横波到达时间 / 震级换算（逐行对齐 Python 版）
    Disaster/QuakeTables.cs             深度分档查表（由脚本从 Python 源码生成，勿手改）
    Disaster/Cities.cs                  全国省市县经纬度检索（数据内嵌在 Resources/cities.json）
    Disaster/DisasterSources.cs         ICL 主源 + Wolfx 四源兜底 + 中央气象台
    Disaster/DisasterService.cs         轮询 / 评估 / 红色倒计时 / 提醒节流 / 演练
    Weather/WeatherLocation.cs          IP 定位三源（pconline → ip-api → ipapi.co）+ 省市区查表回退 + 24h 缓存
    Weather/WeatherClient.cs            Open-Meteo 实况（免 key）+ 天气模块自己的 HTTP
    Weather/WeatherService.cs           编排与缓存；四个显示位：副岛 / 主窗区块 / 标题徽标 / 状态卡片
    Weather/WeatherArt.xaml(.cs)        11 种天气的循环动画控件（副岛、主窗、设置页预览共用一个）
    Weather/WeatherModels.cs            WMO 天气码 → 中文文案 + 动画种类（纯映射，可单测）
  Windows/
    MainBoardWindow.cs                  主窗：贴右贴顶，作业列表 + 插件区块 + 右键菜单
    DynamicIslandWindow.xaml(.cs)        灵动岛：XAML 视觉树 + 六态状态机 + 媒体片段 + 灾害红条
    SubIslandWindow.xaml(.cs)            副岛（收起 40×40 圆 / 展开成长条）
    PanelWindow.xaml(.cs)                主岛下拉面板（今日课程 + 插件面板，3s 自动收起）
    RollCallWindow.cs                   随机点名
    NumberInputDialog.cs                出勤人数弹窗
  Win32/Interop.cs                      全部 P/Invoke（置顶 / 全屏检测 / 云母 / 圆角 / 亚克力）
  Win32/WindowUtil.cs                   工作区 / DIP 换算
  Theme/Palette.cs                      岛 / 主窗 / 面板的色板（固定深色，不随主题变）
  Theme/Anim.cs                         动画统一入口（缓动 / 时长取自 Tokens.Base.xaml）
  Theme/TouchMode.cs                    触摸滑动（无条件开）+ 触屏点击目标放大（可开关）
  Theme/ThemeService.cs                 主题切换：换 Tokens.Light/Dark 字典 + 同步 Palette
  Controls/FluentControls.cs            自定义控件（Icon / NumberBox / SelectorBar / SettingRow /
                                        LinkRow / InfoBar / Badge / Card / SettingGroup / FluentToggle）
  Tray/TrayIcon.cs                      托盘（NotifyIcon）+ 插件菜单项
  Tray/ToastService.cs                  系统通知（Windows Toast）：AUMID 注册 / 进通知中心 /
                                        urgent 穿透专注助手；失败退回托盘气泡
  Updates/VersionInfo.cs                版本号与更新源等部署配置（唯一真源）
  Resources/cities.json                 全国 31 省 / 366 市 / 2817 区县经纬度（内嵌进程序集）
  Resources/icon.ico                    应用图标
  settings/                             出厂默认配置（构建时复制到输出目录）

ClassDailyLand2.Plugin.Abstractions/     插件契约（IPlugin / IPluginHost 50 成员 / 各显示位 Spec，无 WPF）
samples/HelloPlugin/                    可编译的示例插件（九个显示位用了一大半）
samples/GaokaoCountdown/                官方示例插件：下拉面板里的高考倒计时（插件市场首个条目）
docs/plugins.md                         插件开发指南（清单字段 / 显示位 / 50 个 API / 线程纪律 / 排错）
docs/market.md                          插件市场（索引格式 / 客户端结构 / 三道校验）
docs/ROADMAP.md                         阶段划分与完成状态
LICENSE.md                              MIT 许可证（独立文件）
tests/ClassDailyLand2.Core.Tests/        xUnit（课表引擎 / 原子写 / 设置模型 / 值日顺延 /
                                        插件清单·扫描·启停·安装安全·显示位竞争 /
                                        地震算法·城市检索·预警源解析 / 媒体暂停收起 /
                                        天气码映射·定位回退·Open-Meteo 解析 /
                                        作业提交时段排位）
```

## 插件系统

插件是「一个程序集 + 一份 `plugin.json`」，装在 `plugins\<id>\` 下，宿主启动时反射加载，
跑在各自可卸载的 `AssemblyLoadContext` 里 —— 所以**禁用/重载插件不用重启应用**。

九个注册点、五个显示区域：

| 区域 | 显示位 | 竞争方式 |
| --- | --- | --- |
| 灵动岛 | 文案补充、加长段、右侧徽标 | 单值（Priority 大者胜，平级先注册者胜） |
| 副岛 | 标题 / 值 / 进度 | 单值 |
| 下拉面板 | 与「今日课程」并排的面板 | 多值 |
| 班级日常主窗 | 区块、标题徽标、右键菜单项 | 区块/菜单多值，徽标单值 |
| 托盘 | 右键菜单项 | 多值 |

`IPluginHost` 提供 **50 个成员**：元信息与日志 8、显示位注册 11、只读数据 14、
插件私有存储 5、宿主动作 7、事件 3、插件设置页 2。完整说明、可抄的样板与排错表见 **[docs/plugins.md](docs/plugins.md)**。

已安装插件在「设置 → 插件」里管理：启用开关、重载、卸载、安装包（`.cblplugin` / `.zip`）、
从文件夹安装、重新扫描、运行日志（同时写进 `app.log` 的 `[plugin:<id>]` 行）。

### 插件自己的设置页

插件可以在 `Initialize` 里用 `host.RegisterSettings(...)` **声明**一页设置（文本 / 数字 / 开关 / 下拉），
宿主就会在已安装列表里给这一行亮出「设置」按钮，点开按声明渲染成原生控件：

- 插件不碰任何 WPF 类型，主题、触屏放大、键盘操作与设置中心其余页面一致；
- 值存在插件自己的 `data/state.json`，保存后触发 `SettingChanged`，插件立刻生效、不用重载；
- 没声明的插件不会出现按钮 —— 空设置页比没有更糟。

```csharp
host.RegisterSettings(new SettingsPageSpec(
    Title: "高考倒计时",
    Fields: new[]
    {
        new SettingField("grade", "届别", SettingFieldKind.Choice, Options: grades),
        new SettingField("show_on_island", "也在主岛显示", SettingFieldKind.Toggle, DefaultValue: "false"),
    },
    Summary: () => $"{_grade} 届 · 还有 {_days} 天"));
```

### 插件市场

同一页的「插件市场」段直接列出全部可装插件：搜索、状态徽标（官方/认证/社区、可更新、需要更高版本）、
一键安装 / 更新 / 降级，装完立即生效。

市场**不需要服务器**：索引与安装包都在仓库的 `plugins` 分支上，客户端读 raw 地址
（Gitee 主源 + GitHub 备源，包地址用相对路径拼，主备共用一条记录）。

- 索引 `index.json` 一个文件装全部插件；作者只维护 `entries/<id>.json`，
  由 `tools/market.mjs build` 聚合（`pack` 打包、`add` 上架）。
- 安装前过三道校验：索引里的 sha256/size → 包内 `plugin.json` 的 id/version 必须等于索引条目 →
  包内必须有 entry 指向的 dll 且不带契约程序集。都过了才交给 `PluginInstaller`。
- 兼容性提前拦：`api_version` / `min_app_version` / `target_sdk_version` 不满足就灰显并说明原因，
  不让用户等到"装完加载失败"。
- 断网时显示上次缓存并标注时间；两个源都失败会把**每个源各自的错因**列出来。
- 市场页右上角有**「立即同步」**：绕过 10 分钟缓存强制拉一次索引，成功/失败都给明确回音；
  已安装行会跟着亮出「可更新 vX」并可直接更新。

详见 **[docs/market.md](docs/market.md)**；上架规则在 `plugins` 分支的 `docs/publish.md`。

## 内置集成

这三块**不是插件**，直接编进程序，和课表引擎同一个 tick 驱动。

### 天气（实时天气 + 天气动画）

副岛上默认展开显示「24° 晴」，左侧是循环播放的天气动画；**5 分钟没操作自动收成 40×40 的圆，
只留动画**（`weather_idle_collapse_sec`，0 = 不收）。11 种天气全覆盖：
晴 / 多云 / 阴 / 雾 / 毛毛雨 / 雨 / 阵雨 / 冻雨 / 雪 / 雷阵雨 / 冰雹，晴天还分昼夜（太阳 / 月牙）。

- **位置**：`auto_locate` 开着就走 IP 定位（太平洋电脑网 → ip-api → ipapi.co 三源按序试，
  24 小时缓存），三个源全挂退回「灾害预警」页里手选的省市区查内嵌城市表。
  关掉 `auto_locate` 则回到手填经纬度 > 省市区查表。
  ★ IP 是**市级**精度，所以状态卡片一直标出当前坐标是哪来的 ——
  它同时喂给气象预警过滤与地震倒计时，嫌偏可以随时关掉自动定位。
- **数据**：Open-Meteo `current` 实况（免 key），默认 15 分钟刷新一次，失败保留上次快照并标注多久没更新。
- **显示位置**（`weather_show_on`）：
  - `sub`（默认）：副岛；
  - `main`：主窗（作业窗口）的插件区块 + 标题行「24° 晴」徽标；
  - `off`：不显示。
  - **位移规则**：选了副岛却出现**气象预警**时，预警占住副岛、天气自动挤到作业窗口，
    预警过去再回来（优先级：地震 &gt; 气象预警 &gt; 插件 &gt; 媒体 &gt; 实时天气 &gt; 课程）。
- **主窗那一路走的是插件 API**：`BoardSection.Art`（契约 v3 纯新增，2 参主构造不变，
  老插件二进制照旧可用），插件只描述 `BoardArt(Kind, Code, Value, IsDay, Animated)`，宿主负责画。
- **设置 → 天气**：开关 / 显示位置 / 自动定位 / 刷新间隔 / 无操作收起时长 / 动画开关，
  外加**全天气动画预览网格**（11 种同时循环播，晴天拆日 / 夜两格）—— 不用等真的下雨就能核对每种画得对不对。

### 媒体（Windows SMTC）

主岛右侧加长出一段媒体片段：封面 + 歌名 / 歌手 + 播放暂停按钮，胶囊随之变宽。
点正文展开一张媒体卡（封面 64 + 歌名 + 播/停，6 秒自动收）。

- 数据来自 Windows 的全局媒体会话（`GlobalSystemMediaTransportControlsSessionManager`），
  所以 Spotify / 网易云 / 浏览器 / 系统播放器都能读到，不需要每个播放器单独适配。
- 接入方式是 PowerShell + WinRT（`SmtcBridge`），宿主每 400ms 读一次 JSON 快照；
  换歌时桥会把封面 PNG 写到 `settings\integrations\audio\art.png`（先写 `.tmp` 再改名，避免读到半个文件）。
- **暂停后保留 5 秒**（`audio_hide_after_sec`）再收起；重新播放立刻回来。
  判定依据是"最后一次真正在播放的时刻"，不是"有没有曲目信息"—— 暂停时 SMTC 依然报着歌名，
  拿曲目信息判断会导致片段永远收不起来。
- 上课期间整块隐藏并停掉后台轮询（省电、也不至于在讲台上弹歌名）。

### 灾害预警

地震预警走「红色倒计时接管主岛」这条路，气象预警走副岛 / 下拉面板。

- 数据源：ICL 主源（列表 + 详情）+ Wolfx 四个备份源 + 中央气象台，10 秒超时、指数退避、
  按 uid 合并去重；同一事件只提醒一次（等级升级会再提醒）。
- 算法与 Python 版逐行对齐：Haversine 距离 → 按震源深度分档查表（0~2181 km，101 列）→
  超出表尾线性外推，得到**横波到达倒计时**；另有烈度估算与震级换算。
  查表 `QuakeTables.cs` 与城市表 `Resources/cities.json` 都是脚本从 Python 源码生成的，
  不手抄（手抄少一位小数，倒计时就差好几秒）。
- 主岛：地震时进入倒计时态，**红条**（`#FF453A`）+ 文案「灾害预警 · 震中 M6.4 · 横波还有 N 秒」，
  条宽 = 剩余 / 总倒计时。**此时插件的加长段、徽标、媒体片段一律让位**，红条独占 600px。
  点胶囊 = 确认收起。
- 红条占多久分三种情况：**横波未到 → 一直占着到抵达**（这段读秒就是预警的全部价值）；
  红级 → 留到用户点确认；其余已到达的小震 → 再留 `disaster_small_hold_sec`（默认 5 秒）
  的「已到达」提示，随后把主岛交还课程（事件仍在副岛 / 下拉面板里）。
- 设置页有省市县三级联动（只选到省也能用，自动退到省会）与**「模拟一次地震预警」**，
  不用等真地震就能看效果。

## 作业与提交时间

每条作业都可以挂一个**提交时间**（编辑作业时的下拉框）：选今天某一节课，就以**那节课下课**
为提交时间、**下节课上课**为截止，这段时间就是这条作业的**提交时段** `[下课, 下节课上课)`。

- **下拉框**列今天**全部**节次（含没排课名的晚自习之类），文案：
  - 课名唯一：`第3节 数学 · 10:40 下课`
  - 课名重复（下午三节数学）：`10:40 数学 · 第3节 下课`（时间打头，不会选串）
  - 没排课名（晚自习）：`晚自习 · 21:30 下课`；节次名也重名时 → `21:30 晚自习 · 第9节 下课`
  - 另有三项：**自定义文本…**（自由填一个提交时间）、**补交**、**不交**
- **排位规则**（纯计算，不动 `homework.json` 里的顺序；单位是**整个科目板块**）：

  | 状态 | 位置 |
  | --- | --- |
  | 提交时段内（本节下课 → 下节上课） | 整个科目板块顶到作业区最前，条目行尾标「10:40 自习(1)」 |
  | 时段还没到 / 没设提交时间 | 按原顺序（正常） |
  | 补交 | 正常作业之后、已过提交时间之前 |
  | 时段已过（下节课都上课了） | 沉到最底下（「已过提交时间」） |

  同一科目有几条作业时，只要其中一条还在时段内，整个板块都在最前；全过完再一起沉底。
- **到点提醒**：进入提交时段那一刻弹一条**系统通知**（`homework_notify_enabled` 可关），
  只在开始那一刻弹一次；程序刚好不在场（错过 2 分钟宽容窗）就不再补弹。
- 作业区每一行行尾都有淡色小字标注这条的提交状态（`10:40 自习(1)` / `已到点` / `补交` / `已过点`），
  同科目多条一眼能分清。
- 编辑入口两个：设置 → 作业（**科目卡片**：一科一色、卡片可拖动排序、每条自带提交时间下拉框、
  改动即时保存）；主窗右键 → 编辑作业（独立窗口，带取消/保存）。

## 系统通知（Windows Toast）

所有提醒（作业到点、灾害、更新…）走 `Tray\ToastService`：

- **进通知中心**：横幅错过了还能回去补看；带应用名与图标（发送前在 HKCU 登记 AUMID，
  未打包的桌面程序必须先注册，否则 `CreateToastNotifier` 直接抛异常）。
- 灾害类才标成 `urgent`（**重要**，能穿透专注助手 / 免打扰）；其余按普通通知发。
- 环境不支持（AUMID 被策略禁掉等）自动退回老的托盘气泡，不会因此丢提醒。
- Windows 的限制：没有任何受支持的开关能让通知"关不掉"，用户始终能在通知中心划掉、
  或关掉本应用的通知权限。所以真正要命的警报（地震红条）走主岛置顶红条那条"跑不掉"的通道。

## 触屏

分两件互不相干的事，实现都在 `Theme/TouchMode.cs`：

| 能力 | 默认 | 说明 |
| --- | --- | --- |
| **手指上下滑动** | **始终开启** | WPF 的 `ScrollViewer` 默认 `PanningMode=None`，手指按住往下拖是**一点都不会动**的（只有滚轮/滚动条能滚）。设置页、侧栏、二级页、下拉面板、作业列表、浮层内容与下拉框全部开了 `VerticalOnly` + 惯性衰减 0.001，鼠标用户完全无感 |
| **点击目标放大** | 关（设置 → 交互 → 触屏优化） | 控件 32→44、小控件 28→38、设置行 62→82、导航项 40→54。切换即时生效（改窗口资源的 `DynamicResource`，不重建可视树） |

> 滑动**不依赖**上面那个开关 —— 关掉触屏优化也照样能滑，开关只负责让手指更容易点中。

## 构建与运行

需要 .NET SDK（本仓库用 SDK 10 编译，目标框架 `net10.0-windows`）：

```bash
dotnet build "Class Daily Land 2\Class Daily Land 2.csproj"
dotnet test  tests/ClassDailyLand2.Core.Tests/ClassDailyLand2.Core.Tests.csproj
dotnet run --project "Class Daily Land 2"
```

编译示例插件并试装：

```bash
dotnet build samples/HelloPlugin/HelloPlugin.csproj
# 然后：设置 → 插件 → 从文件夹安装… → samples/HelloPlugin/bin/Debug/net10.0
```

> 运行时：目标框架 `net10.0-windows`，工程带 `RollForward=LatestMajor`，
> 装了更高版本的桌面运行时也能直接跑。
> 要发给别人，用 `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`。

运行期数据写在 `settings\`（发布后是 exe 旁），
`AppDomain.UnhandledException` 兜底日志在 `%LOCALAPPDATA%\ClassDailyLand2\startup-crash.log`。

## 验收清单

| 验收点 | 实现 |
| --- | --- |
| 双击运行 → 托盘 → 主窗贴右 + 岛顶部居中 | 单实例 Mutex（非阻塞探测）+ 命名事件唤醒；MainBoardWindow 贴右居中；岛每秒 SetWindowPos 保置顶 |
| **任务栏只有一个条目** | 主窗 `ShowInTaskbar=false`；岛 / 副岛 / 面板是 tool window（`WS_EX_TOOLWINDOW`），入口走托盘 |
| 全屏视频/PPT → 岛 1s 内收起，退出恢复 | `Interop.IsForegroundFullscreen()`（DWM 边框 + 显示器矩形）每 tick 检测 |
| 距下节课 ≤ 60s → 宽 600 + 蓝色进度条 | `island_countdown_sec`；进度条 `#0A84FF` + 流光 + 前缘彗星光晕 |
| 上课/下课瞬间提示 | AppController 边沿检测 → Alert 态（绿字/白字，停留 `island_alert_hold_ms`） |
| 深色主题即时生效 | `ThemeService` 换 Tokens.Light/Dark 字典（DynamicResource 整窗换色）+ 同步 `Palette` |
| **圆角** | 只用 DWM 原生圆角（`DWMWA_WINDOW_CORNER_PREFERENCE=2`），没有任何手绘假圆角；Win10 保持原生直角 |
| **云雾模糊** | Win11 云母（`DWMWA_SYSTEMBACKDROP_TYPE=2`）；Win10 1803+ 退到亚克力；更老/关掉则用不透明底色。内容区只压 25% 薄雾、卡片 35%（普通窗口是 50%/70% —— 叠起来 85% 白会把云母盖死），所以有内容的地方也透得出云母 |
| **手指上下滑动** | 所有滚动区 `PanningMode=VerticalOnly`（无条件），惯性 0.001 |
| 改「提前提醒」生效 | settings.json 为准，同步进 schedule.json；文案 `下节 X · N分钟后` |
| 重启后数据原样恢复 | 全部 JSON 原子写（tmp + File.Replace），坏文件自动隔离为 `.bad` |

## 设置中心与浮层

| 入口 | 界面 |
| --- | --- |
| 托盘 / 主窗右键 → 设置 / 主岛菜单 | `Views\SettingsWindow`：14 页，1232×762，288px 固定侧栏（不可折叠），页内搜索 |
| 设置 → 课表编辑 | `Pages\TimetableEditorPage`：节次 + 开始/结束 + 周一~周日 的可编辑表格，加/删课节、导入导出 JSON、取消/保存（编辑的是副本，保存才落盘） |
| 设置 → 课表与提醒 → 周末作息 | `WeekendDialog`：上午分界点 + 下午节次 + 周末课程 |
| 同上 → 假期与调休 | `HolidaysDialog`：假期区间 + 调休补课日 |
| 同上 → 值日生名单 | `DutyDialog`：名单增删改序 + 应到 / 实到 |
| 同上 → 临时调课 | `TempAdjustDialog`：选日期 + 逐节覆盖（预填原课表） |
| 同上 → 状态测试 | `StatusTestDialog`：按宿主引擎试算任意时间点的岛状态并预览胶囊 |
| 设置 → 媒体 / 天气 / 灾害预警 | `MediaPage` / `WeatherPage` / `DisasterPage`：媒体可注入测试曲目预览；天气带 11 种动画预览网格；灾害可省市县联动 + 模拟演练 |
| 设置 → 作业 | 内嵌卡片版 `HomeworkEditor`（一科一色、卡片可拖动排序、每条带提交时间下拉框，改动即时生效）；主窗右键打开独立「编辑作业」窗口（带取消/保存） |
| 主岛点击 | 今日课程 + 插件面板（`IslandPanelHost`，3 秒自动收起） |
| 主窗右键 → 随机点名 | `RollCallWindow`（10 人冷却不重复） |

> 浮层一律画在设置窗口内部的页内推进层上，不另开窗口；ESC 关闭最上层浮层。

## 许可证

本项目以 **MIT License** 发布，全文见 **[LICENSE.md](LICENSE.md)**（独立文件，不在本 README 里重复）。

© 2026 ClassDailyLand2（班级日常）。自由使用、修改、分发与商用，保留版权声明与许可声明即可。
