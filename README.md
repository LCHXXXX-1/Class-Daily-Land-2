# ClassDailyLand（班级日常）

C# / WPF / .NET 桌面版班级日常管理应用（原 PySide6 版重写）。

托盘常驻 + 贴右主窗 + 顶部灵动岛，实时显示课表状态、作业、值日与出勤；
另带两个**内置集成**：媒体（Windows SMTC）与灾害预警（地震倒计时 + 气象预警），
以及一个**插件系统**（插件可往四处显示位挂内容）。

## 结构

单解决方案，四个工程 + 文档：

```
Class Daily Land 2/                     主工程（WinExe）
  App.xaml / App.xaml.cs                入口：合并主题字典、ShutdownMode、单实例锁、异常留痕
  AppController.cs                      装配中心：1s tick、上课/下课边沿、设置分发、集成调度
  App/Plugins/                          插件宿主（可卸载 ALC / 管理器 / IPluginHost 实现）
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
    Pages/*.xaml                        首页 / 通用 / 灵动岛 / 媒体 / 灾害预警 / 交互 /
                                        个性化 / 课表与提醒 / 作业 / 插件 / 高级 / 关于
    Pages/Pages.cs                      各页需要跑代码的少量交互（取 VM 的时机在 Loaded）
    Dialogs/                            课表管理 / 周末作息 / 假期与调休 / 值日生 / 临时调课 /
                                        状态测试 / 作业编辑器，全部是窗口内浮层
    DialogLauncher.cs                   浮层统一入口 + 打开目录
  ViewModels/SettingsViewModel.cs       强类型绑定属性 + 滑块 200ms 防抖 + 外部改动按键回刷
  Core/                                 纯逻辑（无 UI）
    Engine/ScheduleEngine.cs            课表状态机（upcoming/ongoing/holiday/none/done）
    Engine/ScheduleText.cs              胶囊文案格式（与 Python 版一致）
    Models/                             ScheduleStatus / ScheduleFile / AppSettings / ...
    Managers/                           Settings / Schedule / Homework / StudentOnDuty / Agreement
    Plugins/                            插件纯逻辑：清单 / 扫描 / 启停 / 安装 / 显示位注册表
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
  Resources/config.json                 版本信息
  Resources/cities.json                 全国 31 省 / 366 市 / 2817 区县经纬度（内嵌进程序集）
  Resources/icon.ico                    应用图标
  settings/                             出厂默认配置（构建时复制到输出目录）

ClassDailyLand.Plugin.Abstractions/     插件契约（IPlugin / IPluginHost 48 成员 / 各显示位 Spec，无 WPF）
samples/HelloPlugin/                    可编译的示例插件（九个显示位用了一大半）
docs/plugins.md                         插件开发指南（清单字段 / 显示位 / 48 个 API / 线程纪律 / 排错）
docs/ROADMAP.md                         阶段划分与完成状态
tests/ClassDailyLand.Core.Tests/        xUnit（课表引擎 / 原子写 / 设置模型 / 值日顺延 /
                                        插件清单·扫描·启停·安装安全·显示位竞争 /
                                        地震算法·城市检索·预警源解析 / 媒体暂停收起）
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

`IPluginHost` 提供 **48 个成员**：元信息与日志 8、显示位注册 11、只读数据 14、
插件私有存储 5、宿主动作 7、事件 3。完整说明、可抄的样板与排错表见 **[docs/plugins.md](docs/plugins.md)**。

已安装插件在「设置 → 插件」里管理：启用开关、重载、卸载、安装包（`.cblplugin` / `.zip`）、
从文件夹安装、重新扫描、运行日志（同时写进 `app.log` 的 `[plugin:<id>]` 行）。

## 内置集成

这两块**不是插件**，直接编进程序，和课表引擎同一个 tick 驱动。

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

- 数据源：ICL 主源（列表 + 详情）+ Wolfx 四个备份源 + 中央气象台，8 秒超时、指数退避、
  按 uid 合并去重；同一事件只提醒一次（等级升级会再提醒）。
- 算法与 Python 版逐行对齐：Haversine 距离 → 按震源深度分档查表（0~2181 km，101 列）→
  超出表尾线性外推，得到**横波到达倒计时**；另有烈度估算与震级换算。
  查表 `QuakeTables.cs` 与城市表 `Resources/cities.json` 都是脚本从 Python 源码生成的，
  不手抄（手抄少一位小数，倒计时就差好几秒）。
- 主岛：地震时进入倒计时态，**红条**（`#FF453A`）+ 文案「灾害预警 · 震中 M6.4 · 横波还有 N 秒」，
  条宽 = 剩余 / 总倒计时。**此时插件的加长段、徽标、媒体片段一律让位**，红条独占 600px。
  点胶囊 = 确认收起。
- 设置页有省市县三级联动（只选到省也能用，自动退到省会）与**「模拟一次地震预警」**，
  不用等真地震就能看效果。

## 触屏

分两件互不相干的事，实现都在 `Theme/TouchMode.cs`：

| 能力 | 默认 | 说明 |
| --- | --- | --- |
| **手指上下滑动** | **始终开启** | WPF 的 `ScrollViewer` 默认 `PanningMode=None`，手指按住往下拖是**一点都不会动**的（只有滚轮/滚动条能滚）。设置页、侧栏、二级页、下拉面板、作业列表全部开了 `VerticalOnly` + 惯性衰减 0.0006，鼠标用户完全无感 |
| **点击目标放大** | 关（设置 → 交互 → 触屏优化） | 控件 32→44、小控件 28→38、设置行 62→82、导航项 40→54。切换即时生效（改窗口资源的 `DynamicResource`，不重建可视树） |

> 滑动**不依赖**上面那个开关 —— 关掉触屏优化也照样能滑，开关只负责让手指更容易点中。

## 构建与运行

需要 .NET SDK（本仓库用 SDK 10 编译，目标框架 `net10.0-windows`）：

```bash
dotnet build "Class Daily Land 2\Class Daily Land 2.csproj"
dotnet test  tests/ClassDailyLand.Core.Tests/ClassDailyLand.Core.Tests.csproj
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
`AppDomain.UnhandledException` 兜底日志在 `%LOCALAPPDATA%\ClassDailyLand\startup-crash.log`。

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
| **云雾模糊** | Win11 云母（`DWMWA_SYSTEMBACKDROP_TYPE=2`）；Win10 1803+ 退到亚克力；更老/关掉则用不透明底色 |
| **手指上下滑动** | 所有滚动区 `PanningMode=VerticalOnly`（无条件），惯性 0.0006 |
| 改「提前提醒」生效 | settings.json 为准，同步进 schedule.json；文案 `下节 X · N分钟后` |
| 重启后数据原样恢复 | 全部 JSON 原子写（tmp + File.Replace），坏文件自动隔离为 `.bad` |

## 设置中心与浮层

| 入口 | 界面 |
| --- | --- |
| 托盘 / 主窗右键 → 设置 / 主岛菜单 | `Views\SettingsWindow`：12 页，1232×762，288px 固定侧栏（不可折叠），页内搜索 |
| 设置 → 课表与提醒 → 课表管理 | `TimetableDialog`：多课表 + 节次时间 + 课程格（只周一~周五）+ 导入导出 JSON |
| 同上 → 周末作息 | `WeekendDialog`：上午分界点 + 下午节次 + 周末课程 |
| 同上 → 假期与调休 | `HolidaysDialog`：假期区间 + 调休补课日 |
| 同上 → 值日生名单 | `DutyDialog`：名单增删改序 + 应到 / 实到 |
| 同上 → 临时调课 | `TempAdjustDialog`：选日期 + 逐节覆盖（预填原课表） |
| 同上 → 状态测试 | `StatusTestDialog`：按宿主引擎试算任意时间点的岛状态并预览胶囊 |
| 设置 → 媒体 / 灾害预警 | `MediaPage` / `DisasterPage`：媒体可注入测试曲目预览；灾害可省市县联动 + 模拟演练 |
| 设置 → 作业 | 内嵌 `HomeworkEditor`（改动即时生效）；主窗右键打开同一编辑器的独立浮层（带取消/保存） |
| 主岛点击 | 今日课程 + 插件面板（`IslandPanelHost`，3 秒自动收起） |
| 主窗右键 → 随机点名 | `RollCallWindow`（10 人冷却不重复） |

> 浮层一律画在设置窗口内部的页内推进层上，不另开窗口；ESC 关闭最上层浮层。

## 许可证

[MIT](LICENSE)
