# Class Daily Land 2

> 常驻桌面的班级日常面板：课表状态、作业清单、值日生，以及一条会自动变形的灵动岛。

![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![WPF](https://img.shields.io/badge/WPF-Windows-0078D4)
![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4)
![Version](https://img.shields.io/badge/version-1.0.0--mvp-blue)

Class Daily Land 2 是原 PySide6 版「班级日常」的 C# 重写版，使用 **.NET 10 + 原生 WPF**，
不依赖任何第三方 UI 库。设置中心是 WinUI 3 风格的 XAML，灵动岛、主窗与下拉面板保留
`OnRender` 自绘，以便精确控制异形窗口、逐帧动画与像素级排版。

- **运行时**：.NET 10 桌面运行时
- **目标框架**：`net10.0-windows10.0.19041.0`
- **平台**：Windows 10 1809+ / Windows 11（推荐 Win11）
- **数据**：全部本地 JSON，默认随程序目录存放，可整目录拷走

---

## 目录

- [核心特性](#核心特性)
- [界面入口](#界面入口)
- [架构说明](#架构说明)
- [模块划分](#模块划分)
- [目录结构](#目录结构)
- [构建与运行](#构建与运行)
- [数据存储](#数据存储)
- [测试](#测试)
- [路线图](#路线图)
- [贡献](#贡献)
- [许可证](#许可证)

## 核心特性

### 灵动岛（Dynamic Island）
- 顶部居中、无边框、置顶，整窗 `OnRender` 手绘；状态机 `Hidden / Mini / Compact / Countdown / Alert`
- 上课、下课瞬间的边沿提示；倒计时进度条带流光与彗星光晕；超长文案跑马灯
- 全屏视频/PPT、Office（WPS / Word / Excel / PowerPoint）前台时自动收起，退出即恢复
- 每个 tick 重申 `TOPMOST`，不会被其他置顶窗口压住

### Win11 风格设置中心
- 48px 自绘标题栏（拖动与缩放交给 `WindowChrome`）、260px 可折叠导航（收起后 48px）、内容区最大 980px
- 10 个页面：首页 / 通用 / 灵动岛 / 交互 / 个性化 / 课表与提醒 / 作业 / 插件 / 高级 / 关于
- 7 个窗口内浮层：课表管理、周末作息、假期与调休、值日生名单、临时调课、状态测试、作业编辑器
- 改即存：离散值立即写盘，滑块 200ms 防抖并支持主窗实时预览
- 深浅色主题即时切换：XAML 换资源字典，自绘窗口同步 `Palette`

### 自绘主窗口
- 贴屏幕右缘、顶部对齐，纯文字列：标题、今日值日生、应到/实到、按学科分组编号的作业
- 作业自动滚动，间隔 / 步长 / 停顿时长可调；鼠标悬停暂停，手动滚动后让位 4 秒
- 字号、宽度、不透明度可调，字号与窗口高度 400ms 缓动同步
- 右键菜单覆盖值日生、出勤、作业、课表、调课、假期、点名与退出；`F1` 退出

### 副岛与面板
- 副岛默认折叠，支持自动收起；内容由内置提供者或（后续）插件供给
- 点击主岛展开「今日课程」面板：3 秒无操作自动收起，内容超高可滚轮滚动，当前节带 `▸` 标记

### 数据与可靠性
- 全部 JSON **原子写**（临时文件 + `File.Replace`），损坏文件自动隔离为 `.bad`
- 逐键类型校验，越界值回退默认；设置改动即时广播到各窗口
- 单实例运行：互斥体 + 命名事件，第二次启动只会唤起已运行实例
- 启动早期异常写入 `%LOCALAPPDATA%\ClassDailyLand\startup-crash.log`

### 高 DPI 适配
- 全窗口按 DIP 布局，工作区尺寸经 `WindowUtil.WorkAreaDip()` 统一换算
- 岛、主窗、面板的位置与尺寸在每次定位时重新按当前显示器 DPI 计算
- 图标使用 `Segoe Fluent Icons`，Win10 自动回落 `Segoe MDL2 Assets`

## 界面入口

| 入口 | 打开 |
| --- | --- |
| 托盘图标 → 设置 / 主窗右键 → 设置 | 设置中心 `SettingsWindow` |
| 主岛点击 | 今日课程面板 `PanelWindow` |
| 设置 → 课表与提醒 → 课表管理 | 多课表、节次时间、课程格、导入导出 |
| 同上 → 周末作息 / 假期与调休 / 值日生名单 / 临时调课 / 状态测试 | 对应浮层 |
| 设置 → 作业 | 内嵌作业编辑器（改动即时生效） |
| 主窗右键 → 编辑作业 | 独立作业编辑器（带取消 / 保存，支持 CSV / JSON 导出、JSON 导入） |
| 主窗右键 → 随机点名 | `RollCallWindow`（10 人冷却不重复） |
| `F1` | 退出（先保存数据） |
| `Esc` | 关闭设置窗口内最上层浮层 |

## 架构说明

- **框架**：.NET 10（`net10.0-windows10.0.19041.0`）+ 原生 WPF，**不依赖第三方 UI 库**
- **UI 层**：设置中心与二级浮层全部是原生 XAML；灵动岛、副岛、主窗、面板为 `OnRender` 自绘
- **MVVM**：`CommunityToolkit.Mvvm` 仅用于 INPC / 命令样板，不包含任何控件
- **装配**：`AppController` 是唯一同时持有 Manager 与窗口的地方；窗口只读数据、发请求，彼此不引用
- **驱动**：1 秒 tick 推进课表状态机、刷新岛/主窗/副岛，并检测上课、下课边沿
- **主题双轨**：XAML 侧靠 `DynamicResource` 整本替换字典，自绘侧靠 `Palette` 同步色值
- **可测试性**：`Core/` 不引用任何 WPF 类型，可在无窗口环境下单测

## 模块划分

| 层 | 位置 | 职责 |
| --- | --- | --- |
| **Core** | `Core/Engine`、`Core/Models`、`Core/Storage` | 课表状态机、胶囊文案、数据模型、原子写与数据路径；无 UI 依赖 |
| **Managers** | `Core/Managers` | 数据引擎：设置、课表、作业、值日生、协议；负责加载 / 校验 / 保存 / 广播 |
| **View** | `Views/`、`ViewModels/`、`Themes/` | XAML 设置中心（页面、浮层）、强类型 VM、设计令牌与控件模板 |
| **Windows** | `Windows/` | 自绘窗口：主窗、灵动岛、副岛、下拉面板、随机点名、人数输入 |
| **Infrastructure** | `Win32/`、`Theme/`、`Tray/`、`Controls/` | P/Invoke、工作区换算、自绘色板、托盘、自定义控件与转换器 |

## 目录结构

```
Class Daily Land 2.slnx              解决方案（主工程 + 测试）
Class Daily Land 2/                  唯一 WPF 工程（WinExe）
  App.xaml(.cs)                      入口：主题字典、单实例锁、命名事件唤醒、崩溃留痕
  AppController.cs                   装配中心：Manager / 窗口 / 1s tick / 设置分发
  Core/
    Engine/                          课表状态机与胶囊文案
    Models/                          ScheduleStatus / ScheduleFile / AppSettings / Homework
    Managers/                        设置 / 课表 / 作业 / 值日生 / 协议
    Storage/                         原子写、数据根探测与回退
    Defaults.cs / Support.cs         首启默认数据、Logger、缓动
  Themes/                            Tokens.Base/Light/Dark、Controls、Controls.Sections、Icons、DialogStyles
  Views/
    SettingsWindow.xaml(.cs)         设置外壳：标题栏 + 折叠导航 + 内容宿主 + 浮层遮罩
    Pages/*.xaml                     10 个设置页（代码后置统一在 Pages.cs）
    Dialogs/                         课表 / 周末 / 假期 / 值日生 / 调课 / 状态测试 / 作业编辑器
    DialogLauncher.cs                浮层统一入口
  ViewModels/SettingsViewModel.cs    强类型绑定、防抖落盘、实时预览事件
  Windows/                           主窗、灵动岛、副岛、下拉面板、点名、人数输入
  Controls/                          Icon / NumberBox / SelectorBar / SettingRow / LinkRow / Card / InfoBar 等
  Theme/                             自绘色板与主题服务
  Win32/                             P/Invoke 与工作区 / DIP 换算
  Tray/                              托盘（WinForms NotifyIcon）
  Resources/                         应用图标、版本信息
  settings/                          出厂默认 JSON（构建时复制到输出目录）
tests/ClassDailyLand.Core.Tests/     32 个 xUnit 用例
docs/ROADMAP.md                      分阶段交付与后续计划
```

## 构建与运行

### 环境要求
- Windows 10 1809+ / Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)（含桌面开发工作负载）

### 常用命令

```powershell
# 构建解决方案
dotnet build "Class Daily Land 2.slnx"

# 运行（调试）
dotnet run --project "Class Daily Land 2"

# 单元测试
dotnet test "tests/ClassDailyLand.Core.Tests/ClassDailyLand.Core.Tests.csproj"

# 发布（框架依赖，目标机需安装 .NET 10 桌面运行时）
dotnet publish "Class Daily Land 2/Class Daily Land 2.csproj" -c Release -r win-x64 --self-contained false
```

自包含单文件发布追加：

```powershell
-p:PublishSingleFile=true --self-contained true
```

## 数据存储

运行期数据默认写入**程序目录的 `settings\`**，整个文件夹可随 exe 一起拷贝，保持便携：

```
settings/
  settings.json     全部设置项（类型校验 + 每页归属，用于「恢复本页默认」）
  schedule.json     多套课表、节次时间、假期、调休、周末作息、临时调课
  homework.json     作业条目（学科 / 日期 / 内容）
  name.json         值日生名单、应到 / 实到
  config.json       值日生轮换指针与最近日期
  agreement.json    使用协议状态
  app.log           运行日志
```

- 首次启动时，若缺少文件，会从程序目录的 `settings\` 复制出厂默认值
- 若程序目录不可写（例如安装在 `Program Files`），自动回退到
  `%LOCALAPPDATA%\ClassDailyLand\settings\`，并在托盘中提示
- 写入采用「临时文件 + `File.Replace`」原子替换，异常退出不会留下半截 JSON

## 测试

32 个 xUnit 用例，覆盖：

- 课表状态机：假期 / 调休 / 周末 / 时间偏移 / 提前提醒 / 跨午夜 / 临时调课
- 存储：原子写、损坏文件隔离、默认值复制
- 设置模型：键类型不变量、页面归属、`ResetAll` 单次广播、未知键跨重启保留
- 值日生：轮换与顺延

```powershell
dotnet test "tests/ClassDailyLand.Core.Tests/ClassDailyLand.Core.Tests.csproj"
```

## 路线图

- **已完成**：阶段 1（MVP）、1.5（设置中心 XAML 化）、2（面板与编辑器）
- **进行中**：插件系统——设置中心「插件」页已有版式骨架，`Assembly.LoadFrom` 加载、显示位分配与插件市场尚未接入
- **待办**：Mica 材质（个性化页开关目前仅保存设置）、Banner 态警报条、启动检查更新的真实数据源

详细计划见 [docs/ROADMAP.md](docs/ROADMAP.md)。

## 贡献

1. Fork 并创建特性分支：`git checkout -b feature/your-feature`
2. 保持改动聚焦，遵循现有分层：UI 不进 `Core/`，数据逻辑不进 `Windows/`
3. 提交前运行 `dotnet test`，并确保不引入第三方 UI 库
4. 提交 Pull Request，说明动机、实现方式与验证步骤

## 许可证

本仓库暂未附带 `LICENSE` 文件，默认保留所有权利。如需分发或商用，请先联系作者。
