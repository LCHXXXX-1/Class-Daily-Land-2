# ClassDailyLand（班级日常）

C# / WPF / .NET 8 重写版班级日常管理桌面应用（原 PySide6 版）。

## 结构

单工程架构（沿用原模板工程布局），工程与数据约定都在 `Class Daily Land 2\`：

```
Class Daily Land 2/                     唯一 WPF 工程（WinExe）
  App.xaml / App.xaml.cs                入口：合并主题字典、ShutdownMode、单实例锁、异常留痕
  AppController.cs                      装配中心：1s tick、上课/下课边沿、设置分发、浮层入口
  AssemblyInfo.cs                       ThemeInfo
  Themes/                               设计令牌与控件样式（逐条对位 ui\styles\tokens.css）
    Tokens.Base.xaml                    字体 / 字号 / 圆角 / 度量 / 动效曲线（与深浅色无关）
    Tokens.Light.xaml / Tokens.Dark.xaml 浅深两套色板（ThemeService 运行时整本替换）
    Icons.xaml                          32 个描边图标几何（由 ui/lib/icons.js 转写）
    Controls.xaml                       Button/Toggle/Slider/NumberBox/ComboBox/SelectorBar/
                                        TextBox/ListBox/ScrollBar/ToolTip 样式与模板
    Controls.Sections.xaml              SettingRow / LinkRow / Card / SettingGroup / InfoBar / Badge
    DialogStyles.xaml                   二级浮层（ed-*）与编辑控件尺寸
  Views/
    SettingsWindow.xaml(.cs)            设置中心外壳：48px 自绘标题栏 + 264px 导航（含搜索过滤）
                                        + 内容区（max 980）+ 浮层遮罩宿主 + 页内提示条
    Pages/*.xaml                        通用 / 外观 / 灵动岛 / 课表与提醒 / 作业 / 插件 / 高级 / 关于
    Pages/Pages.cs                      各页需要跑代码的少量交互（取 VM 的时机在 Loaded）
    Dialogs/                            课表管理 / 周末作息 / 假期与调休 / 值日生 / 临时调课 /
                                        状态测试 / 作业编辑器（内嵌 + 独立两用），全部是窗口内浮层
    DialogLauncher.cs                   浮层统一入口 + 打开目录
  ViewModels/SettingsViewModel.cs       强类型绑定属性 + 滑块 200ms 防抖 + 外部改动按键回刷
  Core/                                 纯逻辑（无 UI）
    Engine/ScheduleEngine.cs            课表状态机（upcoming/ongoing/holiday/none/done）
    Engine/ScheduleText.cs              胶囊文案格式（与 Python 版一致）
    Models/                             ScheduleStatus / ScheduleFile / AppSettings / ...
    Managers/                           Settings / Schedule / Homework / StudentOnDuty / Agreement
    Storage/AtomicJson.cs               临时文件 + File.Replace 原子写
    Storage/DataPaths.cs                数据根（输出目录 settings\，不可写回退 LOCALAPPDATA）
    Defaults.cs / Support.cs            首启默认数据 / Logger 与缓动
  Windows/MainBoardWindow.cs            主窗（贴右、贴顶、纯文字列版式）
  Windows/DynamicIslandWindow.cs        灵动岛（全部 OnRender 手绘 + 状态机）
  Windows/SubIslandWindow.cs            副岛
  Windows/PanelWindow.cs                主岛下拉面板（今日课程；3s 自动收起）
  Windows/RollCallWindow.cs             随机点名
  Windows/NumberInputDialog.cs          出勤人数弹窗
  Win32/Interop.cs                      全部 P/Invoke 集中在此
  Win32/WindowUtil.cs                   工作区/DIP 换算
  Theme/Palette.cs                      自绘界面（岛 / 主窗 / 面板）用的色板
  Theme/ThemeService.cs                 主题切换：换 Tokens.Light/Dark 字典 + 同步 Palette
  Controls/FluentControls.cs            自定义控件（Icon / NumberBox / SelectorBar / SettingRow /
                                        LinkRow / InfoBar / Badge / Card / SettingGroup / FluentToggle）
  Controls/Converters.cs                少量绑定转换器
  Tray/TrayIcon.cs                      托盘（框架自带 NotifyIcon）
  Resources/config.json                 版本信息（阶段 4 检查更新用）
  Resources/icon.ico                    应用图标
  settings\                             出厂默认配置（构建时复制到输出目录）
tests/ClassDailyLand.Core.Tests/        xUnit 32 例（ScheduleEngine / 原子写 / 设置模型与页面归属 / 值日生顺延）
```

设置中心与二级浮层是**原生 XAML**（样式令牌照 `ui/` 那套 WebView 设计逐条搬过来）；
灵动岛、副岛、右侧主窗、下拉面板**有意保持自绘**，与 `ui/README.md` 的取舍一致。

运行期数据写在 `Class Daily Land 2\bin\...\settings\`（发布后是 exe 旁 `settings\`），
`AppDomain.UnhandledException` 兜底日志在 `%LOCALAPPDATA%\ClassDailyLand\startup-crash.log`。

## 构建与运行

需要 .NET SDK（本仓库用 SDK 10 编译 net8.0 目标）：

```
dotnet build "Class Daily Land 2\Class Daily Land 2.csproj"
dotnet test  tests/ClassDailyLand.Core.Tests/ClassDailyLand.Core.Tests.csproj
dotnet run --project "Class Daily Land 2"
```

> 运行时：目标框架为 net8.0-windows。若机器未安装 .NET 8 桌面运行时，本项目已配置
> `RollForward=LatestMajor`，可直接使用已安装的更高版本桌面运行时（如 .NET 10）。
> 要严格锁定 .NET 8 运行时，装上 [Desktop Runtime 8](https://aka.ms/dotnet/8.0/desktop-runtime)，
> 或用 `dotnet publish -r win-x64 --self-contained` 打包。

## 验收清单对照（阶段 1）

| 验收点 | 实现 |
| --- | --- |
| 双击运行 → 托盘 → 主窗贴右 + 岛顶部居中 | 单实例 Mutex（非阻塞探测）+ 命名事件唤醒；MainBoardWindow 贴右居中；岛每秒 SetWindowPos 保置顶 |
| 全屏视频/PPT → 岛 1s 内收起，退出恢复 | `Interstitial.IsForegroundFullscreen()`（DWM 边框 + 显示器矩形）每 tick 检测 |
| 距下节课 ≤ 60s → 宽 600 + 蓝色进度条 | `island_countdown_sec`；进度条 #0a84ff + 流光 + 前缘彗星光晕 |
| 上课/下课瞬间提示 | AppController 边沿检测 → Alert 态（绿字/白字，停留 `island_alert_hold_ms`） |
| 深色主题即时生效 | `ThemeService` 换 Tokens.Light/Dark 资源字典（XAML 侧 DynamicResource 整窗换色）+ 同步 `Palette`（岛 / 主窗 / 面板自绘部分） |
| 改「提前提醒」生效 | settings.json 为准，同步进 schedule.json；文案 `下节 X · N分钟后` |
| 重启后数据原样恢复 | 全部 JSON 原子写（tmp + File.Replace），坏文件自动隔离为 `.bad` |

## 设置中心与浮层（XAML 版，对位 ui/ 的 WebView 设计）

| 入口 | 界面 |
| --- | --- |
| 托盘 / 主窗右键 → 设置 / 主岛菜单 | `Views\SettingsWindow`：8 页（通用 · 外观 · 灵动岛 · 课表与提醒 · 作业 · 插件 · 高级 · 关于），搜索框即时过滤导航 |
| 设置中心 → 课表与提醒 → 课表管理 | `TimetableDialog`：多课表（新建 / 复制 / 重命名 / 启用 / 删除）+ 节次时间 + 课程格（只周一~周五，周末走「周末作息」）+ 导入导出 JSON |
| 同上 → 周末作息 | `WeekendDialog`：上午分界点 + 下午节次 + 周末课程 |
| 同上 → 假期与调休 | `HolidaysDialog`：假期区间 + 调休补课日（按周几上课） |
| 同上 → 值日生名单 | `DutyDialog`：名单增删改序 + 应到 / 实到 |
| 同上 → 临时调课 | `TempAdjustDialog`：选日期 + 逐节覆盖（预填原课表） |
| 同上 → 状态测试 | `StatusTestDialog`：按宿主引擎试算任意时间点的岛状态并预览胶囊 |
| 设置中心 → 作业 | 内嵌 `HomeworkEditor`（改动即时生效，无确认按钮）；主窗右键「编辑作业」打开同一编辑器的独立浮层（带取消 / 保存），支持导出 CSV/JSON、导入 JSON |
| 主岛点击 | 今日课程下拉面板（`IslandPanelHost`，3 秒自动收起） |
| 主窗右键 → 随机点名 | `RollCallWindow`（10 人冷却不重复） |
| F1 / 退出 | 退出前保存 settings + schedule |

> 浮层一律画在设置窗口内部的遮罩上（对位 web 版 `ed-host`），不另开窗口；
> ESC 关闭最上层浮层。
