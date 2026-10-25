# Class Daily Land 插件开发与 API 接口规范

> 版本：v1.0 · 对应契约 `apiVersion = 1` · 宿主：Class Daily Land（.NET 10 / WPF）
> 本文档包含两部分：**第一部分·插件开发文档**（从零到发布的完整教程）与 **第二部分·API 接口规范**（每个成员的精确签名与语义）。
> 契约源码：仓库 `ClassDailyLand.Plugin.Abstractions/` · 官方示例：`samples/HelloPlugin/`

---

## 总目录

**第一部分 · 插件开发文档**

1. [这份文档是什么](#1-这份文档是什么)
2. [五分钟结论](#2-五分钟结论)
3. [快速开始：10 分钟做出第一个插件](#3-快速开始10-分钟做出第一个插件)
4. [插件是什么：架构一页纸](#4-插件是什么架构一页纸)
5. [plugin.json 完全参考](#5-pluginjson-完全参考)
6. [生命周期](#6-生命周期)
7. [九个显示位详解](#7-九个显示位详解)
8. [数据模型](#8-数据模型)
9. [插件私有存储](#9-插件私有存储)
10. [宿主动作与事件](#10-宿主动作与事件)
11. [读取宿主设置](#11-读取宿主设置)
12. [打包与分发](#12-打包与分发)
13. [安装、升级与卸载](#13-安装升级与卸载)
14. [调试手册](#14-调试手册)
15. [性能预算与反模式](#15-性能预算与反模式)
16. [安全与边界](#16-安全与边界)
17. [版本兼容](#17-版本兼容)
18. [错误速查表](#18-错误速查表)
19. [提交前检查清单](#19-提交前检查清单)
20. [FAQ](#20-faq)
21. [附录 A：完整样板插件](#附录-a完整样板插件)
22. [附录 B：目录结构一览](#附录-b目录结构一览)

**第二部分 · API 接口规范**

1. [范围与约定](#1-范围与约定-1)
2. [类型总览](#2-类型总览)
3. [IPlugin —— 插件入口](#3-iplugin--插件入口)
4. [IPluginHost —— 宿主门面（48 成员）](#4-ipluginhost--宿主门面48-成员)
5. [Specs —— 规格与数据记录](#5-specs--规格与数据记录)
6. [HostStatus 与 BadgeTone](#6-hoststatus-与-badgetone)
7. [宿主实现要点（影响可观察行为）](#7-宿主实现要点影响可观察行为)
8. [plugin.json 模式](#8-pluginjson-模式)
9. [线程与异常契约](#9-线程与异常契约)
10. [版本兼容规则](#10-版本兼容规则)
11. [附录：成员速查索引](#11-附录成员速查索引)

---
---

# 第一部分 · 插件开发文档

---

## 1. 这份文档是什么

这份文档教你为 **Class Daily Land** 写插件：一个能被主程序加载、往界面里加内容、能跟着主程序一起分发的小程序集。

插件能碰的地方：

| 位置 | 能做什么 |
| --- | --- |
| 灵动岛（主岛） | 追加文案、加宽、右侧徽标 |
| 副岛 | 提供标题 / 数值 / 进度环 |
| 下拉面板 | 点击主岛弹出的面板，多插件并排 |
| 班级日常主窗 | 追加区块、标题行徽标、右键菜单项 |
| 托盘 | 右键菜单项 |
| 数据 | 只读课表 / 作业 / 值日 / 设置 |
| 存储 | 每个插件独立的 `data/` 目录 |
| 动作 | 弹通知、打开设置、打开链接、显隐窗口 |

插件**不能**做的事：改宿主的课表与设置（只能读）、画自定义 WPF 窗口（显示位都是文字/徽标协议）、访问插件目录与程序目录之外的路径。详见[第 16 章](#16-安全与边界)。

---

## 2. 五分钟结论

如果你只想快点跑起来，记住这七条：

1. 插件 = 一个 **.NET 10 类库工程** + 一个 `plugin.json`，编译产物整个就是一个插件。
2. 只引用 **一个**程序集：`ClassDailyLand.Plugin.Abstractions`，且必须 `Private=false` + `ExcludeAssets=runtime`（[4.3](#43-为什么必须-privatefalse)）。
3. 实现 `IPlugin`，入口是 `Initialize(IPluginHost host)`；**所有注册都发生在这里**。
4. 界面内容通过「显示位」注册：`host.RegisterXxx(...)` 传一个**每秒被调用一次的委托**，返回文字/徽标/面板行。
5. 回调里**禁止 IO、网络、锁等待、大计算**——慢 1 毫秒整个界面卡 1 毫秒。要干重活自己起线程，回调只读缓存。
6. 插件数据放自己的 `data/` 目录：`host.ReadState/WriteState`（键值）或 `host.ReadJson/WriteJson`（整文件）。
7. 打包 = 把编译输出打成 zip、扩展名改成 `.cblplugin`（`plugin.json` 必须在包根目录）。

---

## 3. 快速开始：10 分钟做出第一个插件

### 3.1 环境准备

| 项 | 要求 |
| --- | --- |
| SDK | .NET 10 SDK（`dotnet --version` ≥ 10） |
| IDE | Visual Studio 2022+ / VS Code + C# 插件 / Rider（任选） |
| 契约源码 | 本仓库 `ClassDailyLand.Plugin.Abstractions/`（已随解决方案，无需额外装） |
| 宿主 | 能跑起来的 Class Daily Land（开发期直接用 `bin/Debug/net10.0-windows/` 的输出） |

### 3.2 建工程

```bash
mkdir MyPlugin && cd MyPlugin
dotnet new classlib -n MyPlugin -f net10.0
```

工程文件（**关键就三处**，抄 `samples/HelloPlugin/HelloPlugin.csproj` 即可）：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>MyPlugin</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>

  <ItemGroup>
    <!-- 只引契约；Private=false + ExcludeAssets=runtime 是硬要求，见 4.3 -->
    <ProjectReference Include="..\..\ClassDailyLand.Plugin.Abstractions\ClassDailyLand.Plugin.Abstractions.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>

  <ItemGroup>
    <!-- plugin.json 跟着输出走，装的时候整个输出目录就是一个插件 -->
    <None Update="plugin.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

路径按你的仓库实际位置调整（上面假设插件工程与 `ClassDailyLand.Plugin.Abstractions` 隔一级）。

### 3.3 写 plugin.json

```json
{
  "id": "my-plugin",
  "name": "我的插件",
  "version": "1.0.0",
  "description": "演示主岛徽标与下拉面板。",
  "author": "你的名字",
  "entry": "MyPlugin.dll",
  "type": "MyPlugin.MyPlugin",
  "apiVersion": 1
}
```

字段含义与校验规则见[第 5 章](#5-pluginjson-完全参考)。**`entry` 必须与 `AssemblyName` 一致**（带 `.dll`），`type` 是实现 `IPlugin` 的完整类型名。

### 3.4 写代码

```csharp
using ClassDailyLand.Plugin;

namespace MyPlugin;

public sealed class MyPlugin : IPlugin
{
    IPluginHost? _host;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        host.Log("我的插件启动了");

        // 主岛右侧徽标
        host.RegisterIslandBadge(new IslandBadgeSpec(
            () => new IslandBadge("HI", BadgeTone.Accent)));

        // 下拉面板：点主岛能看到
        host.RegisterPanel(new PanelSpec("我的面板", 200, () => new[]
        {
            "我的第一个插件",
            $"宿主版本 {host.HostVersion}",
            $"现在 {host.Now:HH:mm}",
        }));
    }

    public void Shutdown() => _host?.Log("我的插件卸载了");
}
```

### 3.5 编译并安装

```bash
dotnet build -c Release
```

两种装法（**不用重启应用**）：

1. **装包**：把 `bin/Release/net10.0/` 打成 zip 改名为 `.cblplugin` → 设置 → 插件 → 「安装插件包…」。
2. **装文件夹**（开发期推荐）：设置 → 插件 → 「从文件夹安装…」→ 直接选 `bin/Release/net10.0/`（宿会自动找带 `plugin.json` 的那一层）。

### 3.6 应该看到什么

- 灵动岛右侧出现蓝色 `HI` 徽标；
- 点主岛，下拉面板里出现「我的面板」，内容每秒刷新；
- 设置 → 插件页列出你的插件：名称、版本、注册数（2 个显示位）；
- 插件页「运行日志」里有 `我的插件启动了`；
- `app.log` 里同步有 `[plugin:my-plugin] 我的插件启动了`。

---

## 4. 插件是什么：架构一页纸

### 4.1 目录布局

```
<程序目录>/
├─ Class Daily Land.exe          ← 宿主
├─ plugins/                      ← 插件根（数据目录回退时也在这里，始终不搬家）
│  ├─ hello/                     ← 一个目录 = 一个插件，目录名 = id
│  │  ├─ plugin.json             ← 清单（必需）
│  │  ├─ HelloPlugin.dll         ← 入口程序集（plugin.json 的 entry 指向它）
│  │  ├─ 其它依赖.dll            ← 可选，随你的插件一起解决
│  │  └─ data/                   ← 插件私有数据（宿主创建，升级时保留）
│  │     ├─ state.json           ← ReadState/WriteState 的落点
│  │     └─ *.json               ← ReadJson/WriteJson 的落点
│  └─ my-plugin/
└─ settings/                     ← 用户数据（课表/作业/设置/plugins.json 启用状态）
```

### 4.2 加载模型

- 启动时扫描 `plugins/` 下**一级子目录**（隐藏目录 `.` / `_` 开头跳过）；
- 每个插件一个**可卸载的** `AssemblyLoadContext`：插件之间、插件与宿主的依赖各找各的，禁用/重载时能真正卸载，不用重启应用；
- 插件依赖解析顺序：先看插件自己目录里有没有同名 dll，没有再交给默认上下文（框架/宿主程序集）；
- 加载失败（清单不合法、类型找不到、构造函数炸……）不会拖垮宿主：撤掉已注册的显示位、记错误，设置中心**把这个插件列出来并显示原因**。

### 4.3 为什么必须 `Private=false`

契约程序集（`ClassDailyLand.Plugin.Abstractions.dll`）由**宿主提供**。如果你的插件输出目录里也躺着一份（ProjectReference 默认行为），插件上下文会加载到自己那份，于是：

- 插件里的 `IPlugin` 与宿主里的 `IPlugin` 是**两个不同类型**；
- 表现为 `typeof(IPlugin).IsAssignableFrom(你的类型) == false`，加载器报「没有实现 IPlugin」——**插件坑里最经典的第一个**。

`<Private>false</Private>` 让契约 dll 不被复制到输出目录；`<ExcludeAssets>runtime</ExcludeAssets>` 连运行时也不带。输出目录里只应出现：`plugin.json` + 你的插件 dll（+ 你自己的依赖）。

---

## 5. plugin.json 完全参考

### 5.1 字段表

| 字段 | 必填 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- | --- |
| `id` | ✅ | string | — | 插件唯一 id，**同时是插件目录名**。规则见 5.2 |
| `name` | | string | = `id` | 显示名 |
| `version` | | string | `1.0.0` | 显示用版本，宿主**不做**比较、不阻止降级 |
| `description` | | string | `""` | 一句话描述 |
| `author` | | string | `""` | 作者 |
| `entry` | ✅ | string | — | 入口 dll **文件名**（不允许路径），必须以 `.dll` 结尾 |
| `type` | ✅ | string | — | 实现 `IPlugin` 的完整类型名（`命名空间.类名`） |
| `apiVersion` | | int | `1` | 插件需要的契约大版本。大于宿主支持的版本 → 拒绝加载 |
| `homepage` | | string | — | 可选，主页链接 |

JSON 解析宽容：大小写不敏感、允许注释、允许尾随逗号。

### 5.2 id 命名规则

- 正则：`^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`（字母数字开头，可含 `.` `_` `-`，总长 1~64）；
- 不允许路径分隔符——卸载按 id 定位目录，这是硬安全线；
- **强烈建议目录名 = id**：安装时宿主以 id 建目录；id 与目录名不一致不报错，但两个插件撞 id 会忽略后扫到的那个。

### 5.3 常见写法错误

| 写法 | 结果 |
| --- | --- |
| `entry: "bin/MyPlugin.dll"` | 拒绝：「只能是插件目录下的文件名」 |
| `entry: "MyPlugin.exe"` | 拒绝：「必须是 .dll」 |
| `type: "MyPlugin"`（缺命名空间） | 拒绝/加载失败：找不到类型 |
| `apiVersion: 2` | 拒绝：「插件需要契约 v2，本版本只支持到 v1」 |
| 入口 dll 不在包里 | 拒绝：「找不到入口程序集」 |
| `id` 用了中文 | 拒绝：id 不合法 |

---

## 6. 生命周期

### 6.1 加载时序

```
宿主启动
  └─ PluginCatalog.Scan(plugins/)          只读文件系统，不加载程序集
       └─ 每个合法且未禁用的目录
            ├─ new PluginLoadContext(插件目录)      可卸载 ALC
            ├─ 加载 entry dll → 找 type → new 出来
            ├─ instance.Initialize(host)            ★ 只在 UI 线程调用一次
            └─ 记日志：已加载 X vY，注册 N 个显示位
```

半途失败（类型找不到/构造函数炸/Initialize 抛异常）会把这次已经注册的显示位全部撤掉再记错误——不会留指向半死插件的回调。

### 6.2 四种「卸载」路径

| 触发 | 行为 |
| --- | --- |
| 用户在插件页**禁用** | FlushState → Shutdown → 清注册 → 卸载 ALC。界面立刻消失，**不用重启** |
| 用户点**重载** | 先卸载再加载（改完 dll 不用重启应用） |
| 插件目录被删 | 下次扫描/刷新时自动卸载 |
| **应用退出** | 全部 Shutdown，启用状态落盘 |

`Shutdown()` 里不要抛异常（宿主会接住并记账，但日志会很难看）；返回后宿主清掉你的全部注册，之后不会再有任何回调。

### 6.3 线程归属（最重要的一条纪律）

| 时机 | 线程 |
| --- | --- |
| `Initialize` / `Shutdown` | UI 线程 |
| 你注册的所有显示位回调（每秒一次） | UI 线程 |
| `Tick` / `SettingsChanged` / `IslandClicked` 事件 | UI 线程 |
| `Log/Warn/Error` | 任意线程都可以调（内部有锁） |

回调里禁止：IO、网络、锁等待、大数据计算。**慢 1 毫秒 = 整条界面卡 1 毫秒。**

### 6.4 重载会保留什么

| 保留 | 丢失 |
| --- | --- |
| `data/` 里的全部文件（state.json、你的 json） | 进程内的全部字段（计数器、缓存） |
| 启用/禁用状态（在 `settings/plugins.json`） | 已注册的显示位（重新走 Initialize） |

### 6.5 数据保存在哪里

| 数据 | 位置 | 谁写的 |
| --- | --- | --- |
| 插件启用/禁用 | `<数据目录>/settings/plugins.json` | 宿主 |
| 插件私有文件 | `<程序目录>/plugins/<id>/data/` | 你（通过 host） |

---

## 7. 九个显示位详解

九个显示位分两类：

- **单值位**（主岛文案/加长段/徽标、副岛、主窗徽标）：同一时刻只出一个——`Priority` 最大的胜，平级先注册的胜；
- **多值位**（下拉面板、主窗区块、两个菜单）：全都要，按 `Priority` 降序 + 注册顺序排；菜单项用 `Order`（**越小越靠上**，宿主默认项从 100 起）。

所有回调返回 `null` 表示「本秒没内容」：单值位会**继续问次高优先级的插件**（不是把显示位空着），多值位则这一秒不显示你。

### 7.1 主岛文案补充 `RegisterIslandText`

```csharp
host.RegisterIslandText(new IslandTextSpec(
    () => _pending > 0 ? $"待办 {_pending}" : null,   // null = 本秒不追加
    Priority: 0));
```

- 非空时追加在核心文案后：`正在上课 · 数学 · 待办 3`；
- 只读缓存，别在委托里算。

### 7.2 主岛加长段 `RegisterIslandExtra`

```csharp
host.RegisterIslandExtra(new IslandExtraSpec(Width: 64, Priority: 0));
```

- 把胶囊加宽 `Width` 像素（DIP，钳制 0~400），给徽标/长文案留位；
- 与徽标位同时注册时宽度自动叠加。

### 7.3 主岛徽标 `RegisterIslandBadge`

```csharp
host.RegisterIslandBadge(new IslandBadgeSpec(
    () => new IslandBadge("DEMO", BadgeTone.Accent)));
```

- 画在预留宽度里，不覆盖文案；
- 颜色只用 `BadgeTone` 五档（Default/Accent/Success/Warning/Danger），宿主按主题取色——**不要自己传颜色**，深浅色两套主题都好看。

### 7.4 副岛 `RegisterSubIsland`

```csharp
host.RegisterSubIsland(new SubIslandSpec(
    () => new SubIslandInfo("倒计时", "12:34", 0.42)));
```

- 副岛平时收起成 40×40 的圆，点击展开成长条；
- `Progress` 取 0..1，收起态画成圆环里的进度点。

### 7.5 下拉面板 `RegisterPanel`

```csharp
host.RegisterPanel(new PanelSpec("我的面板", 220, () => new[]
{
    "第一行",
    "",                       // 空行也算内容，用来分段
    "第三行",
}, MaxHeightRatio: 1.0 / 3.0));
```

- 所有插画面板与内置「今日课程」并排；总宽超过屏幕 80% 时从右往左丢；
- `Width` 钳制 90~640 DIP；`MaxHeightRatio` 是面板最大高度占工作区的比例；
- 返回空列表或 null = 本秒不显示该面板。

### 7.6 主窗区块 `RegisterBoardSection`

```csharp
host.RegisterBoardSection(new BoardSectionSpec(() =>
    new BoardSection("我的区块", new[] { "第一行", "第二行" })));
```

- 追加在作业列表下面，纯文字排版（标题 + 若干行）。

### 7.7 主窗徽标 `RegisterBoardBadge`

```csharp
host.RegisterBoardBadge(new BoardBadgeSpec(
    () => new BoardBadge("Wi-Fi 正常", BadgeTone.Success)));
```

- 班级日常标题行右侧的小徽标。

### 7.8 主窗右键菜单 `RegisterBoardAction`

```csharp
host.RegisterBoardAction(new BoardActionSpec(
    "我的：打个招呼", () => host.Toast("我的插件", "你好"), Order: 10));
```

- `Order` 越小越靠上（宿主默认项从 100 起，想置顶就给它个小数字）；
- `Invoke` 在 UI 线程执行，可以干重活（用户主动触发，不在刷新路径上）。

### 7.9 托盘菜单 `RegisterTrayAction`

```csharp
host.RegisterTrayAction(new TrayActionSpec(
    "我的：打开插件页", () => host.OpenSettings("plugins"), Order: 10));
```

- 排在「设置」之后、「退出」之前。

### 7.10 注销与主动刷新

```csharp
var id = host.RegisterPanel(...);
host.Unregister(id);      // 传不存在的 id 不报错
host.Invalidate();        // 催宿主立刻重算（默认每秒一次）
```

---

## 8. 数据模型

全部来自 `host` 的只读属性，**都是快照**，拿去随便用，但不要改。

### 8.1 `HostSnapshot`（课表状态，每秒刷新）

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `Status` | string | `upcoming` / `ongoing` / `holiday` / `none` / `done`（常量见 `HostStatus`） |
| `StatusText` | string | 状态文案，如「即将上课」 |
| `Course` | string | 相关课程名 |
| `Index` | int | 课程序号（-1 表示无） |
| `Start` / `End` | string | 该节课的 `HH:mm`（**已叠加时间偏移**） |
| `RemainMinutes` | int | 距下课还有多少分钟 |
| `UntilMinutes` | int | 距上课还有多少分钟 |
| `HolidayName` | string? | 假期名（`holiday` 状态时） |
| `Now` | DateTime | 宿主视角的「现在」 |

### 8.2 `CourseEntry`（一节课）

| 字段 | 说明 |
| --- | --- |
| `Index` | 第几节（从 1 开始） |
| `Course` | 课程名 |
| `Start` / `End` | `HH:mm`，已叠加偏移 |
| `IsOngoing` | 此刻是否正在上 |

获取：`host.CoursesOfDay(host.Now)`（某天全部课）、`host.CurrentCourse` / `host.NextCourse`（没课为 null）。

### 8.3 `DutySnapshot`（值日）

`TodayName`（今天值日生，没排为 ""）、`ShouldCount`（应到）、`ActualCount`（实到）。

### 8.4 `HomeworkEntry`（作业）

`Subject`（科目）、`Content`（内容）、`Date`（日期）。

### 8.5 `HostWorkArea`（主屏工作区，DIP）

`X` / `Y` / `Width` / `Height` / `Scale`。配合 `host.DpiScale`（1.0 = 100%）做自适应布局。

---

## 9. 插件私有存储

每个插件一个独立目录 `<程序目录>/plugins/<id>/data/`（宿主建好），插件之间互相看不到。

### 9.1 键值状态（小数据）

```csharp
var n = int.TryParse(host.ReadState("launches"), out var v) ? v + 1 : 1;
host.WriteState("launches", n.ToString());
host.FlushState();                 // 立刻落盘；卸载/禁用前宿主也会替你 Flush 一次
```

- 落点 `data/state.json`，**原子写**，插件崩了不会写坏文件；
- 内存即时生效，忘了 `FlushState` 也只是晚点落盘。

### 9.2 JSON 文件（结构化数据）

```csharp
var config = host.ReadJson<MyConfig>("config") ?? new MyConfig();
config.Counter++;
host.WriteJson("config", config);        // → data/config.json，原子写
```

- 名字会自动规范成 `data/<名字>.json`；含 `/` `\` `..` `:` 的名字被拒绝（防路径穿越）；
- 文件不存在或损坏返回 `default`，不抛异常。

### 9.3 升级不丢数据

覆盖安装时 `data/` 目录**原样保留**（见 [13.3](#133-升级覆盖安装)），所以状态、缓存不会因为升级丢。

---

## 10. 宿主动作与事件

### 10.1 宿主动作

| 方法 | 行为 | 边界 |
| --- | --- | --- |
| `Toast(title, message)` | 弹托盘气泡（系统通知） | title 为空时用「班级日常」 |
| `OpenSettings(pageKey?)` | 打开设置中心，可定位到某页 | 见下面的页键表 |
| `OpenUrl(url)` | 默认浏览器打开 | **只允许 http/https**，其它协议拒绝并记日志 |
| `OpenFolder(path)` | 资源管理器打开目录 | **只允许程序目录内部**（插件目录就在其中） |
| `ShowMainWindow(bool)` | 显隐班级日常主窗 | 实际写设置项 `show_main_window` |
| `ShowIsland(bool)` | 显隐灵动岛 | 实际写设置项 `show_island` |
| `SetSubIslandExpanded(bool)` | 展开/收起副岛 | 收起是 40×40 圆，展开是长条 |

`OpenSettings` 的页键：`general` / `appearance` / `island` / `media` / `disaster` / `interaction` / `schedule` / `homework` / `plugins` / `advanced` / `about`（不传 = 首页）。

### 10.2 事件（都在 UI 线程）

```csharp
host.Tick += (_, e) =>                // 每秒一次，与界面刷新同一个 tick
{
    var snap = e.Snapshot;            // 同 host.Snapshot
    _cache = ComputeCheap(snap);      // 只更新缓存，别在这里干重活
};

host.SettingsChanged += (_, e) =>     // 用户改了设置，e.Keys 是被改的键
    host.Log("设置变了：" + string.Join(", ", e.Keys));

host.IslandClicked += (_, _) =>       // 用户点了灵动岛
    host.Toast("我的插件", "被点了");
```

`Tick` 是给「想比显示位更早拿到数据」的插件用的；大多数插件用显示位委托就够了。

---

## 11. 读取宿主设置

```csharp
var raw   = host.GetSetting("main_font_size");            // string?（不存在为 null）
var size  = host.GetSettingInt("main_font_size", 16);     // 缺失/类型不符用 16
var dark  = host.GetSettingBool("theme_dark", false);
```

- 读的是 `settings.json` 的原始值（bool 会归一成 `"true"` / `"false"`）；
- **只读**：插件不能改宿主设置（`ShowMainWindow` / `ShowIsland` 是仅有的两个例外，且只动自己那两个键）；
- 设置改了会收到 `SettingsChanged` 事件。

---

## 12. 打包与分发

### 12.1 `.cblplugin` 是什么

**就是一个 zip**，扩展名不同。结构（`plugin.json` 必须在包根目录）：

```
my-plugin-1.0.0.cblplugin   (= zip)
  plugin.json
  MyPlugin.dll
  （你自己的依赖 dll）
```

也允许整包只套一层文件夹（从 GitHub 下 zip 再打包时常见），宿主会找「唯一子目录」里的清单。

### 12.2 打包命令

PowerShell：

```powershell
cd bin\Release\net10.0
Compress-Archive -Path * -DestinationPath ..\..\..\my-plugin-1.0.0.zip -Force
Rename-Item ..\..\..\my-plugin-1.0.0.zip my-plugin-1.0.0.cblplugin
```

bash / Git Bash：

```bash
cd bin/Release/net10.0
zip -r ../../../my-plugin-1.0.0.cblplugin . -x '*.pdb'
```

### 12.3 不要打包的东西

| 别打包 | 原因 |
| --- | --- |
| `ClassDailyLand.Plugin.Abstractions.dll` | 和宿主那份冲突 → 加载失败（见 4.3） |
| `*.pdb` | 体积大（排查崩溃时可以临时带） |
| `data/` 目录 | 用户数据，覆盖安装会踩掉用户状态 |
| `*.log`、调试缓存 | 没必要 |

### 12.4 版本号建议

`version` 宿主不比较，但用户靠它判断要不要更新——用语义化版本 `1.2.3`。

---

## 13. 安装、升级与卸载

### 13.1 安装

设置 → 插件页：

- **安装插件包…**：选 `.cblplugin` 或 `.zip`；
- **从文件夹安装…**：选插件输出目录（开发期最方便，宿会自动下找一层带 `plugin.json` 的目录）；
- **重新扫描**：手动扫 `plugins/`（往目录里拷文件后用）。

装完立刻加载（除非用户之前禁用了它）。装的时候如果加载失败，错误会**原样报给用户**，不用去翻日志。

### 13.2 卸载

插件页对每个插件有「卸载」：先卸载运行实例，再删 `plugins/<id>/` 整个目录，启用状态一并忘掉。

### 13.3 升级（覆盖安装）

1. 用同 `id` 的新包安装；
2. 宿主发现已存在 → 提示「已装过 X，要覆盖吗」；
3. 确认后：旧目录改名 `.bak-<时间戳>` → 写入新目录 → 成功删备份；**失败回滚旧目录**；
4. `data/` 保留（状态不丢）。

---

## 14. 调试手册

### 14.1 日志在哪

| 日志 | 位置 | 内容 |
| --- | --- | --- |
| 插件页「运行日志」 | 设置 → 插件 | 最近 400 条，含宿主扫描/加载/卸载与你的 `host.Log` |
| `app.log` | `<数据目录>/app.log` | 同样内容带 `[plugin:<id>]` 前缀，出问题不用截图设置页 |

### 14.2 改完代码怎么生效

| 改了什么 | 怎么办 |
| --- | --- |
| 插件代码 | 重新 build → 插件页「重载」（不用重启应用） |
| plugin.json | 「重新扫描」 |
| 契约/宿主 | 重启应用 |

### 14.3 附加调试器

插件跑在宿主进程里，直接附加到 `Class Daily Land.exe` 下断点即可（VS：调试 → 附加到进程）。`Initialize` 在启动路径上，断点要趁早。

### 14.4 造数据

- 作业/课表/值日都在设置里改，改完插件立刻能读到；
- `host.Now` 带时间偏移（`schedule_time_offset_seconds`），调试时间相关逻辑用设置里的偏移，别改系统时钟。

---

## 15. 性能预算与反模式

### 15.1 预算

| 项 | 预算 |
| --- | --- |
| 单个显示位回调 | **< 1 ms**（九个位加起来就是界面的一帧） |
| 每秒总回调耗时 | 控制在几毫秒内 |
| `Initialize` | 只做注册与轻量初始化（IO/网络会拖慢启动） |

### 15.2 反模式对照

| ❌ 反模式 | ✅ 正确做法 |
| --- | --- |
| 回调里 `File.ReadAllText` / `HttpClient.GetAsync` | 后台线程算好写进字段，回调只读字段 |
| 回调里 `lock` 等锁 | 无锁快照（`volatile` 字段 / 不可变对象整体替换） |
| 每秒 `new List<string>()` 拼几百行 | 缓存结果，数据没变就返回同一份 |
| `Initialize` 里读网络配置 | 先注册空态，后台线程拿到数据后 `Invalidate()` |
| 在回调里 `Thread.Sleep` | 永远不要 |
| 徽章颜色自己写 `#RRGGBB` | 用 `BadgeTone`，宿主按主题取色 |

标准模式（后台算 + 回调读缓存 + 主动催）：

```csharp
sealed class MyPlugin : IPlugin
{
    IPluginHost? _host;
    volatile string[] _lines = Array.Empty<string>();   // 整体替换 = 无锁快照

    public void Initialize(IPluginHost host)
    {
        _host = host;
        host.RegisterPanel(new PanelSpec("远程数据", 220, () => _lines));
        _ = Task.Run(RefreshLoopAsync);                  // 重活自己起线程
    }

    async Task RefreshLoopAsync()
    {
        while (_host is not null)
        {
            try
            {
                var data = await FetchAsync();            // 网络/IO 在这
                _lines = Format(data);
                _host?.Invalidate();                      // 催宿主立刻重算
            }
            catch (Exception ex) { _host?.Warn("刷新失败：" + ex.Message); }

            await Task.Delay(TimeSpan.FromMinutes(1));
        }
    }
    // ...
}
```

---

## 16. 安全与边界

### 16.1 插件能做什么

读课表/作业/值日/设置；往九个显示位放文字内容；弹通知；打开设置/链接；显隐主窗与灵动岛；在自己的 `data/` 里读写文件。

### 16.2 宿主替你挡了什么

| 攻击/失误 | 宿主的防线 |
| --- | --- |
| 插件包解压穿越（`../`、绝对路径） | 逐条校验落点，越界整包拒绝 |
| 解压炸弹 | 条目 ≤ 2000、总体积 ≤ 64 MB |
| `plugin.json` 写恶意路径 | `entry` 只允许文件名；`id` 只允许字母数字 `.` `_` `-` |
| 回调抛异常拖垮界面 | 全部接住，该秒按「没有内容」处理并记日志 |
| 打开任意文件/目录 | `OpenFolder` 限程序目录内；`OpenUrl` 限 http/https |
| 读写别人数据 | 存储 API 全部限死在自己的 `data/` |
| 契约版本不符 | `apiVersion` 大于宿主支持 → 拒绝加载 |

### 16.3 不要做的事

- 不要在回调里做重活（[第 15 章](#15-性能预算与反模式)）；
- 不要往插件目录里塞 `ClassDailyLand.Plugin.Abstractions.dll`；
- 不要依赖「先注册就一定显示」——单值位永远可能被更高优先级的插件挤掉；
- 不要在 `Shutdown` 里抛异常；
- 不要假设目录名等于 id（安装时以 id 建目录，但老目录可能是历史名）。

---

## 17. 版本兼容

### 17.1 契约版本怎么谈

- `plugin.json` 的 `apiVersion` 声明「我需要的最低契约大版本」；
- 宿主只实现一个大版本（当前 **1**）。插件要 2 → 拒绝加载并提示升级宿主；
- `host.ApiVersion` 可以在 `Initialize` 里比对，决定要不要降级运行（少注册几个显示位）。

### 17.2 宿主的承诺

- **大版本内只增不改**：新增成员不打倒旧插件；改语义/删成员会升大版本；
- 显示位的**求值语义**（优先级、null 含义、钳制范围）视为契约的一部分；
- 数据记录的字段只增不减（旧插件忽略新字段即可）。

### 17.3 插件侧的做法

```csharp
if (host.ApiVersion.Major > 1)
{
    host.Log("契约比预期新，按保守模式运行");
    // 只注册确信存在的显示位
}
```

---

## 18. 错误速查表

| 现象 | 原因 | 解法 |
| --- | --- | --- |
| 「没有实现 IPlugin」 | 输出目录里有契约 dll，类型对不上 | `Private=false` + `ExcludeAssets=runtime`，清输出目录重编 |
| 「找不到类型 X」 | `type` 写错，或类不是 `public`、没有无参构造 | 对照 `entry` 程序集与 `命名空间.类名` |
| 装完「加载失败」 | 缺依赖 dll / 目标框架不对 | 把依赖放进插件目录；`TargetFramework=net10.0` |
| 「已经装过 X」 | 同 id 已存在 | 覆盖安装，或先卸载 |
| 「包里找不到 plugin.json」 | 清单不在根、也不在唯一子目录 | 重打包，别多套文件夹 |
| 徽标/文案不显示 | 回调返回 null，或被更高优先级挤掉 | 先返回固定值试；`Priority` 调大 |
| 界面卡顿 | 回调干了重活 | 按[第 15 章](#15-性能预算与反模式)改造 |
| 卸载报「文件被占用」 | 插件线程还在跑 | 退出前 join 自己的线程；或重启应用再卸 |
| `OpenUrl` 没反应 | 协议不是 http/https | 换链接（日志里有拒绝记录） |
| 升级后状态丢了 | 把 `data/` 打进了包 | 打包排除 `data/`（见 12.3） |

---

## 19. 提交前检查清单

- [ ] `plugin.json`：`id` 合法、`entry` 与 AssemblyName 一致、`type` 完整、`apiVersion` ≤ 1
- [ ] 输出目录里**没有** `ClassDailyLand.Plugin.Abstractions.dll`、没有 `*.pdb`、没有 `data/`
- [ ] 入口类 `public` + 无参构造 + `sealed`（推荐）
- [ ] 所有回调 < 1 ms：无 IO / 网络 / 锁 / 大计算
- [ ] `Initialize` 里只做注册与轻量初始化
- [ ] `Shutdown` 不抛异常、能重复调用
- [ ] 自己的后台线程在 `Shutdown` 时能退出（别留野线程）
- [ ] 徽标/面板文案做了空值与长度兜底
- [ ] 用 `host.Now` 而不是 `DateTime.Now`
- [ ] 打包成 `.cblplugin` 后用真安装路径装一遍验证
- [ ] 禁用 → 启用 → 重载 → 卸载 全走过一遍

---

## 20. FAQ

**Q：插件能弹自己的 WPF 窗口吗？**
A：不能。显示位是文字/徽标协议。要富交互就用 `Toast` + `OpenUrl`（跳到你的网页），或在菜单项里 `OpenSettings` 带到插件页。

**Q：两个插件能互相调用吗？**
A：不能，也不打算支持。各自独立加载、数据隔离。共享数据走文件（约定一个格式）或各自读宿主的同一份设置。

**Q：插件里能用 NuGet 包吗？**
A：能。依赖会进你的输出目录，安装时一起进插件目录，加载时优先从插件目录解析。注意体积和许可证。

**Q：为什么我的回调从来没被调用？**
A：三种可能：插件被禁用；注册后宿主还没到下一个 tick（等 1 秒）；`Shutdown` 之后（不会再有回调）。

**Q：`Priority` 和 `Order` 有什么区别？**
A：`Priority` 用于单值位竞争——越大越优先；`Order` 只用于菜单项——越小越靠上。平级都是先注册的胜。

**Q：禁用和卸载有什么区别？**
A：禁用只是不加载（目录和数据都留着，随时可再启用）；卸载删掉整个插件目录。

**Q：插件会导致主程序崩溃吗？**
A：不会。加载与运行期的异常都被隔离；最坏情况是你的插件不显示 + 日志里一条错误。

---

## 附录 A：完整样板插件

一个把常用能力都用一遍的模板（可直接改）。目录：`MyPlugin/`

`MyPlugin.csproj`（见 [3.2](#32-建工程)）、`plugin.json`（见 [3.3](#33-写-pluginjson)）、`MyPlugin.cs`：

```csharp
using ClassDailyLand.Plugin;

namespace MyPlugin;

/// <summary>模板插件：主岛徽标 + 副岛 + 面板 + 主窗区块 + 托盘菜单 + 私有状态。</summary>
public sealed class MyPlugin : IPlugin
{
    IPluginHost? _host;
    int _visits;
    string[] _panelLines = Array.Empty<string>();

    public void Initialize(IPluginHost host)
    {
        _host = host;

        // ── 私有状态：第几次启动 ──
        _visits = int.TryParse(host.ReadState("visits"), out var v) ? v + 1 : 1;
        host.WriteState("visits", _visits.ToString());
        host.FlushState();

        host.Log($"模板插件启动（第 {_visits} 次），数据目录 {host.DataDirectory}");

        // ── 主岛：文案 + 徽标 ──
        host.RegisterIslandText(new IslandTextSpec(
            () => _visits > 1 ? $"模板×{_visits}" : "模板", Priority: 0));

        host.RegisterIslandBadge(new IslandBadgeSpec(
            () => new IslandBadge("TPL", BadgeTone.Accent)));

        // ── 副岛 ──
        host.RegisterSubIsland(new SubIslandSpec(
            () => new SubIslandInfo("模板", $"第 {_visits} 次", 0.5)));

        // ── 下拉面板：读宿主数据，只做轻量拼接 ──
        host.RegisterPanel(new PanelSpec("模板面板", 220, () =>
        {
            var snap = host.Snapshot;
            return new[]
            {
                "模板插件",
                "",
                snap.Status switch
                {
                    HostStatus.Ongoing => $"正在上 · {snap.Course}",
                    HostStatus.Upcoming => $"下节 · {snap.Course}",
                    HostStatus.Holiday  => $"假期 · {snap.HolidayName}",
                    HostStatus.Done     => "今天已放学",
                    _                   => "今天没有课",
                },
                $"启动 {_visits} 次",
            };
        }));

        // ── 班级日常：区块 + 右键菜单 ──
        host.RegisterBoardSection(new BoardSectionSpec(() =>
        {
            var duty = host.Duty;
            var name = string.IsNullOrWhiteSpace(duty.TodayName) ? "未排" : duty.TodayName;
            return new BoardSection("模板区块", new[]
            {
                $"值日：{name}",
                $"应到 {duty.ShouldCount} 人 · 实到 {duty.ActualCount} 人",
            });
        }));

        host.RegisterBoardAction(new BoardActionSpec(
            "模板：弹个提示", () => host.Toast("模板插件", "这是右键菜单项"), Order: 10));

        // ── 托盘菜单 ──
        host.RegisterTrayAction(new TrayActionSpec(
            "模板：打开插件页", () => host.OpenSettings("plugins"), Order: 10));

        // ── 事件 ──
        host.SettingsChanged += (_, e) =>
            host.Log("设置变了：" + string.Join(", ", e.Keys));
    }

    public void Shutdown()
    {
        _host?.Log("模板插件卸载");
        _host = null;
    }
}
```

## 附录 B：目录结构一览

```
samples/HelloPlugin/          ← 官方示例（与本模板等价，随解决方案可直接构建）
  HelloPlugin.cs
  HelloPlugin.csproj
  plugin.json

ClassDailyLand.Plugin.Abstractions/   ← 契约源码（只读，别改；要改先升 apiVersion）
  IPlugin.cs
  IPluginHost.cs
  Specs.cs
```

> 精确到每个成员的签名、线程与异常语义，见**第二部分 · API 接口规范**。

---
---

# 第二部分 · API 接口规范

> 程序集：`ClassDailyLand.Plugin.Abstractions` · 命名空间：`ClassDailyLand.Plugin` · 目标框架：`net10.0`
> 本部分是契约程序集的**权威参考**：每个成员的签名、语义、线程归属与异常行为均以实现代码为准。
> 使用教程见**第一部分 · 插件开发文档**。

---

## 1. 范围与约定

### 1.1 本文档覆盖

`ClassDailyLand.Plugin.Abstractions` 程序集公开的全部 API：

- 接口：`IPlugin`、`IPluginHost`
- 枚举：`BadgeTone`
- 静态类：`HostStatus`
- 规格记录（插件传给宿主的协议）：`IslandTextSpec`、`IslandExtraSpec`、`IslandBadge`、`IslandBadgeSpec`、`SubIslandInfo`、`SubIslandSpec`、`PanelSpec`、`BoardSection`、`BoardSectionSpec`、`BoardBadge`、`BoardBadgeSpec`、`BoardActionSpec`、`TrayActionSpec`
- 数据记录（宿主给插件的数据）：`CourseEntry`、`HostSnapshot`、`DutySnapshot`、`HomeworkEntry`、`HostWorkArea`
- 事件参数：`HostTickEventArgs`、`HostSettingsEventArgs`

### 1.2 约定

| 约定 | 说明 |
| --- | --- |
| 「UI 线程」 | 宿主的 WPF 主线程。`Initialize`/`Shutdown`/全部显示位回调/全部事件都在其上调用 |
| 「本秒」 | 宿主每秒一次的求值周期；显示位回调每周期调用一次 |
| `null` 返回 | 显示位回调返回 `null` 表示「本周期没有内容」，语义见各成员 |
| 钳制 | 宿主对传入值有范围钳制（如面板宽度 90~640），超出不报错、按边界生效 |
| 大小写 | JSON 字段名大小写不敏感 |

### 1.3 兼容性总原则

契约大版本内**只增不改**：新增成员不影响旧插件；修改语义或删除成员将升大版本号。当前 `HostApiVersion = 1`。

---

## 2. 类型总览

| 类型 | 种类 | 角色 |
| --- | --- | --- |
| `IPlugin` | interface | 插件入口，宿主反射实例化并驱动 |
| `IPluginHost` | interface | 宿主门面，插件的一切能力入口（48 成员） |
| `BadgeTone` | enum | 徽标语义色调（5 档） |
| `HostStatus` | static class | 课表状态字符串常量 |
| `IslandTextSpec` | record | 主岛文案补充位协议 |
| `IslandExtraSpec` | record | 主岛加长段协议 |
| `IslandBadge` | record | 主岛徽标数据 |
| `IslandBadgeSpec` | record | 主岛徽标位协议 |
| `SubIslandInfo` | record | 副岛内容数据 |
| `SubIslandSpec` | record | 副岛内容位协议 |
| `PanelSpec` | record | 下拉面板协议 |
| `BoardSection` | record | 主窗区块数据 |
| `BoardSectionSpec` | record | 主窗区块位协议 |
| `BoardBadge` | record | 主窗徽标数据 |
| `BoardBadgeSpec` | record | 主窗徽标位协议 |
| `BoardActionSpec` | record | 主窗右键菜单项协议 |
| `TrayActionSpec` | record | 托盘菜单项协议 |
| `CourseEntry` | record | 一节课 |
| `HostSnapshot` | record | 课表状态快照 |
| `DutySnapshot` | record | 值日出勤快照 |
| `HomeworkEntry` | record | 一条作业 |
| `HostWorkArea` | record | 主屏工作区（DIP） |
| `HostTickEventArgs` | class | Tick 事件参数 |
| `HostSettingsEventArgs` | class | SettingsChanged 事件参数 |

---

## 3. IPlugin —— 插件入口

```csharp
public interface IPlugin
{
    void Initialize(IPluginHost host);
    void Shutdown();
}
```

宿主通过 `plugin.json` 的 `entry`（程序集文件名）与 `type`（完整类型名）反射定位实现类。**硬性要求**：

- 实现类必须 `public`；
- 必须有无参公共构造函数（`Activator.CreateInstance`）；
- 一个程序集可有多个 `IPlugin` 实现，但一个 `plugin.json` 只指向一个；
- 入口类名与 `type` 不一致时，宿主会退而求其次找程序集里第一个公开的非抽象 `IPlugin` 实现。

### 3.1 `Initialize(IPluginHost host)`

| 项 | 契约 |
| --- | --- |
| 调用时机 | 加载成功后**一次**，早于任何显示位求值与事件 |
| 线程 | UI 线程 |
| 应当做 | 保存 `host` 引用、注册显示位、订阅事件、读私有状态做轻量初始化 |
| 不应当做 | IO、网络、耗时计算（直接拖慢应用启动） |
| 抛异常 | 本次加载判失败：已注册的显示位被全部撤掉、错误被记录并在设置中心展示；插件不会进入运行 |

### 3.2 `Shutdown()`

| 项 | 契约 |
| --- | --- |
| 调用时机 | 禁用 / 重载 / 目录消失 / 应用退出，各一次 |
| 线程 | UI 线程 |
| 应当做 | 停自己的后台线程、`FlushState`（宿主也会替你做一次）、释放资源 |
| 不应当做 | 抛异常（宿主会接住并记账，但日志难看）；再假设任何回调还会来 |
| 返回后 | 宿主清掉该插件注册的全部显示位；此后不应再收到任何回调 |

---

## 4. IPluginHost —— 宿主门面（48 成员）

七组能力。注册类方法均返回**注册 id**（string），可传给 `Unregister` 注销。

### 4.1 第一组：元信息与日志（8）

```csharp
Version ApiVersion { get; }              // 契约版本，宿主实现为 new Version(1, 0)
string HostVersion { get; }              // 宿主版本，如 "1.1.0"
string PluginId { get; }                 // 本插件在 plugin.json 声明的 id
string DataDirectory { get; }            // plugins/<id>/data/（宿主已创建）
string PluginDirectory { get; }          // plugins/<id>/
void Log(string message);                // 普通日志 → 插件页运行日志 + app.log
void Warn(string message);               // 警告（行首加 ⚠ ）
void Error(string message);              // 错误（行首加 ✖ ）
```

- `ApiVersion` 供插件在 `Initialize` 里比对，决定是否降级运行（见[第 10 章](#10-版本兼容规则)）。
- `Log/Warn/Error` **任意线程可调**（内部加锁）；`message` 为 null 时按空串处理。
- 日志同时进 `app.log`（带 `[plugin:<id>]` 前缀），出问题不必截图设置页。

### 4.2 第二组：显示位注册（11）

```csharp
string RegisterIslandText(IslandTextSpec spec);        // 主岛文案补充（单值）
string RegisterIslandExtra(IslandExtraSpec spec);      // 主岛加长段（单值）
string RegisterIslandBadge(IslandBadgeSpec spec);      // 主岛右侧徽标（单值）
string RegisterSubIsland(SubIslandSpec spec);          // 副岛内容（单值）
string RegisterPanel(PanelSpec spec);                  // 下拉面板（多值，并排）
string RegisterBoardSection(BoardSectionSpec spec);    // 主窗追加区块（多值）
string RegisterBoardBadge(BoardBadgeSpec spec);        // 主窗标题行徽标（单值）
string RegisterBoardAction(BoardActionSpec spec);      // 主窗右键菜单项（多值）
string RegisterTrayAction(TrayActionSpec spec);        // 托盘菜单项（多值）
void Unregister(string registrationId);                // 按注册 id 注销；不存在不报错
void Invalidate();                                     // 催宿主立刻重算（默认每秒一次）
```

通用语义：

- 注册 id 形如 `<插件id>#<序号>`，插件内唯一；
- `spec` 传 null 会抛 `ArgumentNullException`（参数校验在宿主）——不要传 null；
- 卸载/禁用时宿主会清掉该插件**全部**注册，不需要手动 `Unregister`；
- `Invalidate` 内部失败只记警告，不抛给调用方。

### 4.3 第三组：只读数据（14）

```csharp
DateTime Now { get; }                       // 应用内"现在" = DateTime.Now + 时间偏移
HostSnapshot Snapshot { get; }              // 课表状态快照（每秒刷新）
IReadOnlyList<CourseEntry> CoursesOfDay(DateTime day);   // 某天全部课程
CourseEntry? CurrentCourse { get; }         // 正在上的课；没有为 null
CourseEntry? NextCourse { get; }            // 下一节课；没有为 null
DutySnapshot Duty { get; }                  // 值日与出勤
IReadOnlyList<HomeworkEntry> Homework { get; }           // 全部作业（录入顺序）
string? GetSetting(string key);             // 设置原始值；不存在为 null
int  GetSettingInt(string key, int fallback);
bool GetSettingBool(string key, int fallback);  // 缺失/类型不符返回 fallback
HostWorkArea WorkArea { get; }              // 主屏工作区（DIP）
double DpiScale { get; }                    // 1.0 = 100%，保留两位小数
bool IsMainWindowVisible { get; }
bool IsIslandVisible { get; }
```

语义与边界：

- **时间一律用 `Now`**：它叠加了设置里的 `schedule_time_offset_seconds`，与界面显示一致；插件自己用 `DateTime.Now` 会在测试/调时错位；
- `CoursesOfDay(day)` 按 `day.Date` 取课表，返回的上课时间**已叠加偏移**；参数传任意日期均可（查明天/昨天都行）；
- `CurrentCourse`/`NextCourse` 在「此刻无课」时为 null，**不要**假定非空；
- `Homework` 返回新列表（快照），可安全枚举；阶段语义为「不区分日期，按录入顺序」；
- `GetSetting*` **只读**宿主 `settings.json`：bool 归一为 `"true"`/`"false"`；`GetSettingInt` 用固定区域性解析；
- `DpiScale` 取自主窗，主窗不存在时返回 1.0；
- 这些属性的内部异常都被宿主吞掉并降级（如 `Duty` 失败返回空快照），但插件仍应做空值兜底。

### 4.4 第四组：插件私有存储（5）

```csharp
string? ReadState(string key);              // 读键值；key 空或不存在为 null
void WriteState(string key, string? value);// 写键值；value 为 null = 删除该键
void FlushState();                          // 立刻落盘 data/state.json
T? ReadJson<T>(string name);                // 读 data/<name>.json；不存在/损坏返回 default
void WriteJson<T>(string name, T value);    // 原子写 data/<name>.json
```

- `state.json` 位于 `plugins/<id>/data/`，原子写；崩盘不会写坏文件；
- `WriteState` 内存即时生效；宿主在卸载/禁用前也会 `FlushState` 一次；
- `name` 会被规范成 `<name>.json`（已带 `.json` 后缀则原样用）；含 `/` `\` `..` `:` 的名字**被拒绝**（防路径穿越），只记一条警告；
- `ReadJson` 的 JSON 损坏/解析失败返回 `default(T)` 并记警告，不抛。

### 4.5 第五组：宿主动作（7）

```csharp
void Toast(string title, string message);           // 托盘气泡（系统通知）
void OpenSettings(string? pageKey = null);          // 打开设置中心，可定位页
void OpenUrl(string url);                           // 默认浏览器打开
void OpenFolder(string path);                       // 资源管理器打开目录
void ShowMainWindow(bool show);                     // 显隐主窗（写设置 show_main_window）
void ShowIsland(bool show);                         // 显隐灵动岛（写设置 show_island）
void SetSubIslandExpanded(bool expanded);           // 展开/收起副岛
```

安全边界（违反不抛异常，记警告后拒绝）：

| 方法 | 约束 |
| --- | --- |
| `OpenUrl` | 仅 `http`/`https`，其它协议（`file:`、`mailto:`、自定义协议）拒绝 |
| `OpenFolder` | 仅**程序目录内部**（`AppContext.BaseDirectory` 之内，插件目录就在其中） |
| `Toast` | title 为空时用「班级日常」；message 为 null 按空串 |
| `OpenSettings` | pageKey 白名单：`general`/`appearance`/`island`/`media`/`disaster`/`interaction`/`schedule`/`homework`/`plugins`/`advanced`/`about`；null = 首页 |
| `ShowMainWindow`/`ShowIsland` | 仅这两个设置键可写，是插件唯一能改宿主设置的两个口子 |

### 4.6 第六组：事件（3）

```csharp
event EventHandler<HostTickEventArgs>? Tick;            // 每秒一次（UI 线程）
event EventHandler<HostSettingsEventArgs>? SettingsChanged;  // 设置改动（UI 线程）
event EventHandler? IslandClicked;                      // 用户点了灵动岛（UI 线程）
```

- `Tick` 与宿主界面刷新同一个 tick；参数里的 `Snapshot` 与 `host.Snapshot` 等价；
- `SettingsChanged.Keys` 给出**被改动的键**列表（一次改动可能多个键）；
- `IslandClicked` 无参数；用户每次点击触发一次；
- 事件处理函数抛异常不会拖垮宿主（每个插件的调用被单独 try 住并记日志），但会污染日志——自己 try 住更好。

---

## 5. Specs —— 规格与数据记录

全部为 `sealed record`（值语义、不可变、支持 `with`）。

### 5.1 显示位规格

#### `IslandTextSpec` —— 主岛文案补充（单值）

```csharp
public sealed record IslandTextSpec(Func<string?> Text, int Priority = 0);
```

| 成员 | 约束 |
| --- | --- |
| `Text` | 每秒在 UI 线程调用；返回 null/空白 = 本秒不追加（单值位会继续问次高优先级） |
| `Priority` | 越大越优先；平级先注册者胜 |

显示效果：`正在上课 · 数学 · 你的文案`。

#### `IslandExtraSpec` —— 主岛加长段（单值）

```csharp
public sealed record IslandExtraSpec(int Width, int Priority = 0);
```

`Width`（DIP）被钳制在 **0~400**；与徽标位同时注册时宽度自动叠加。

#### `IslandBadge` / `IslandBadgeSpec` —— 主岛右侧徽标（单值）

```csharp
public sealed record IslandBadge(string Text, BadgeTone Tone = BadgeTone.Default);
public sealed record IslandBadgeSpec(Func<IslandBadge?> Badge, int Priority = 0);
```

`Text` 空白 = 本秒无徽标；`Tone` 由宿主按主题取色（见 [6.2](#62-badgetone)）。

#### `SubIslandInfo` / `SubIslandSpec` —— 副岛内容（单值）

```csharp
public sealed record SubIslandInfo(string Title, string Value, double Progress = 0);
public sealed record SubIslandSpec(Func<SubIslandInfo?> Content, int Priority = 0);
```

`Progress` 钳制 **0..1**；收起态（40×40 圆）画成圆环进度点。`Title` 与 `Value` 都空 = 本秒无内容。

#### `PanelSpec` —— 下拉面板（多值）

```csharp
public sealed record PanelSpec(
    string Title, double Width, Func<IReadOnlyList<string>?> Lines,
    double MaxHeightRatio = 1.0 / 3.0, int Priority = 0);
```

| 成员 | 约束 |
| --- | --- |
| `Title` | 暂用于日志与无障碍名称 |
| `Width` | DIP，钳制 **90~640** |
| `Lines` | 每秒调用一次；null 或空列表 = 本秒不显示该面板；元素允许为 null（渲染时过滤） |
| `MaxHeightRatio` | 面板最大高度占工作区高度比例 |

所有插画面板与内置「今日课程」并排；总宽超过屏幕 80% 时从右往左丢弃。

#### `BoardSection` / `BoardSectionSpec` —— 主窗追加区块（多值）

```csharp
public sealed record BoardSection(string Title, IReadOnlyList<string> Lines);
public sealed record BoardSectionSpec(Func<BoardSection?> Section, int Priority = 0);
```

追加在作业列表下方，纯文字排版。`Title` 空且 `Lines` 空 = 本秒无内容。

#### `BoardBadge` / `BoardBadgeSpec` —— 主窗标题行徽标（单值）

```csharp
public sealed record BoardBadge(string Text, BadgeTone Tone = BadgeTone.Default);
public sealed record BoardBadgeSpec(Func<BoardBadge?> Badge, int Priority = 0);
```

#### `BoardActionSpec` —— 主窗右键菜单项（多值）

```csharp
public sealed record BoardActionSpec(string Text, Action Invoke, int Order = 50);
```

- `Order` **越小越靠上**（宿主默认项从 100 起）；
- `Invoke` 在 UI 线程、用户点击时执行——可以干重活（不在刷新路径上）；
- `Text` 空白的项被忽略。

#### `TrayActionSpec` —— 托盘菜单项（多值）

```csharp
public sealed record TrayActionSpec(string Text, Action Invoke, int Order = 50);
```

排在「设置」之后、「退出」之前；`Order` 语义同上。

### 5.2 数据记录（只读快照）

#### `CourseEntry` —— 一节课

```csharp
public sealed record CourseEntry(int Index, string Course, string Start, string End, bool IsOngoing);
```

| 字段 | 说明 |
| --- | --- |
| `Index` | 课程序号（-1 表示无） |
| `Course` | 课程名 |
| `Start` / `End` | `HH:mm`，**已叠加时间偏移** |
| `IsOngoing` | 此刻是否正在上 |

#### `HostSnapshot` —— 课表状态快照（每秒刷新）

```csharp
public sealed record HostSnapshot(
    string Status, string StatusText, string Course,
    int Index, string Start, string End,
    int RemainMinutes, int UntilMinutes,
    string? HolidayName, DateTime Now);
```

| 字段 | 说明 |
| --- | --- |
| `Status` | `upcoming`/`ongoing`/`holiday`/`none`/`done`（常量见 `HostStatus`） |
| `StatusText` | 状态文案（如「即将上课」） |
| `Course` | 相关课程名 |
| `Index` | 课程序号 |
| `Start` / `End` | 该节课 `HH:mm`（已叠加偏移） |
| `RemainMinutes` | 距下课分钟数 |
| `UntilMinutes` | 距上课分钟数 |
| `HolidayName` | 假期名（`holiday` 时有值） |
| `Now` | 宿主视角当前时间（含偏移） |

#### `DutySnapshot` —— 值日与出勤

```csharp
public sealed record DutySnapshot(string TodayName, int ShouldCount, int ActualCount);
```

`TodayName` 为空串表示今天没排值日。

#### `HomeworkEntry` —— 一条作业

```csharp
public sealed record HomeworkEntry(string Subject, string Content, string Date);
```

#### `HostWorkArea` —— 主屏工作区（DIP）

```csharp
public sealed record HostWorkArea(double X, double Y, double Width, double Height, double Scale);
```

`Scale` 与 `host.DpiScale` 一致；做自适应布局时配合使用。

### 5.3 事件参数

```csharp
public sealed class HostTickEventArgs : EventArgs
{
    public HostTickEventArgs(HostSnapshot snapshot);
    public HostSnapshot Snapshot { get; }
}

public sealed class HostSettingsEventArgs : EventArgs
{
    public HostSettingsEventArgs(IReadOnlyList<string> keys);
    public IReadOnlyList<string> Keys { get; }   // 本次被改动的设置键
}
```

---

## 6. HostStatus 与 BadgeTone

### 6.1 HostStatus（课表状态常量）

```csharp
public static class HostStatus
{
    public const string Upcoming = "upcoming";   // 即将上课
    public const string Ongoing  = "ongoing";    // 正在上课
    public const string Holiday  = "holiday";    // 假期
    public const string None     = "none";       // 今天没课
    public const string Done     = "done";       // 今天已放学
}
```

与 `HostSnapshot.Status` 逐字对应，做 switch 时用常量不要写字面量。

### 6.2 BadgeTone（语义色调）

```csharp
public enum BadgeTone
{
    Default = 0,   // 中性（次要文字色）
    Accent  = 1,   // 强调蓝
    Success = 2,   // 成功/进行中（绿）
    Warning = 3,   // 提醒（黄）
    Danger  = 4,   // 警示（红）
}
```

**插件不要自己传颜色值**：色调是语义，宿主按当前主题（浅色/深色）取色，保证两套主题下都协调。

---

## 7. 宿主实现要点（影响可观察行为）

这些不属于契约程序集，但决定了插件能看到的行为，写插件时必须知道。

### 7.1 显示位常量与竞争规则

| 显示位 | 内部槽位 | 类型 | 竞争键 |
| --- | --- | --- | --- |
| 主岛文案 | `island.text` | 单值 | `Priority` 降序 → 注册顺序升序 |
| 主岛加长段 | `island.extra` | 单值 | 同上 |
| 主岛徽标 | `island.badge` | 单值 | 同上 |
| 副岛内容 | `subIsland.content` | 单值 | 同上 |
| 下拉面板 | `panel` | 多值 | `Priority` 降序 → 注册顺序升序，全部显示 |
| 主窗区块 | `board.section` | 多值 | 同上 |
| 主窗徽标 | `board.badge` | 单值 | 同单值规则 |
| 主窗菜单 | `board.action` | 多值 | `Order` 升序 → 注册顺序 |
| 托盘菜单 | `tray.action` | 多值 | `Order` 升序 → 注册顺序 |

- 单值位求值：按优先级逐个问，**回调返回 null 视为「本周期没内容」，继续问下一个**（高优先级插件临时没东西时自然落到次优先级，不会空着显示位）；
- 多值位求值：全部调用，任一返回空则本周期不显示它，不影响别的。

### 7.2 异常隔离

- 所有插件回调（显示位委托、事件处理函数）都在宿主的 try 内；
- 抛异常的注册项按「本次没有内容」处理，并往插件日志记一条 `插件回调异常（类型）：...`；
- 连续抛错不会拖垮宿主，但会污染日志——自己接住。

### 7.3 加载与隔离

- 每插件一个可卸载 `AssemblyLoadContext`（名字 `plugin:<目录名>`）；
- 依赖解析：先插件目录内同名 dll，再默认上下文（框架/宿主程序集不重复加载）；
- **契约程序集永远返回 null 交给默认上下文**——这就是「输出目录不能带契约 dll」的技术原因；
- 加载失败：撤注册、记错误、`context.Unload()`；设置中心列出该插件与错误原因。

### 7.4 日志格式

- 插件页运行日志：`HH:mm:ss  <来源(16字节右对齐)>  <消息>`，容量 400 条；
- `app.log` 同步一份，前缀 `[plugin:<来源>]`；宿主自身事件用来源 `host`，注册表异常用 `registry`。

---

## 8. plugin.json 模式

### 8.1 字段定义

| 字段 | 必填 | 类型 | 默认 | 校验规则 |
| --- | --- | --- | --- | --- |
| `id` | ✅ | string | — | `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`；同时作为安装目录名 |
| `name` | | string | = `id` | 任意文本 |
| `version` | | string | `1.0.0` | 任意文本；宿主不比较、不阻止降级 |
| `description` | | string | `""` | 任意文本 |
| `author` | | string | `""` | 任意文本 |
| `entry` | ✅ | string | — | 文件名（禁 `/` `\` `..`），必须以 `.dll` 结尾，文件必须存在 |
| `type` | ✅ | string | — | 完整类型名；宿主先按它找，找不到则找程序集里第一个公开非抽象 `IPlugin` |
| `apiVersion` | | int | `1` | ≤ `HostApiVersion`(1)，否则拒绝加载 |
| `homepage` | | string | — | 任意文本（宿主不打开，仅展示） |

### 8.2 示例

```json
{
  "id": "my-plugin",
  "name": "我的插件",
  "version": "1.0.0",
  "description": "演示主岛徽标与下拉面板。",
  "author": "你的名字",
  "entry": "MyPlugin.dll",
  "type": "MyPlugin.MyPlugin",
  "apiVersion": 1,
  "homepage": "https://example.com/my-plugin"
}
```

### 8.3 解析宽容度

大小写不敏感、允许 `//` 与 `/* */` 注释、允许尾随逗号。

---

## 9. 线程与异常契约

### 9.1 线程归属汇总

| API | 线程 | 备注 |
| --- | --- | --- |
| `Initialize` / `Shutdown` | UI | 各一次 |
| 全部 `RegisterXxx` 传入的委托 | UI | 每秒一次 |
| `Tick` / `SettingsChanged` / `IslandClicked` | UI | 事件处理函数 |
| `BoardActionSpec.Invoke` / `TrayActionSpec.Invoke` | UI | 用户点击时，可干重活 |
| `Log` / `Warn` / `Error` | 任意 | 内部加锁 |
| 其余属性与方法 | 预期 UI | 虽内部有兜底，仍建议在 UI 线程调用 |

### 9.2 异常行为汇总

| 场景 | 宿主行为 |
| --- | --- |
| `Initialize` 抛异常 | 加载失败：撤注册、记错误、设置中心展示 |
| `Shutdown` 抛异常 | 接住并记日志，继续卸载流程 |
| 显示位回调抛异常 | 本周期按「无内容」处理，记日志，继续问下一个（单值位） |
| 事件处理函数抛异常 | 接住并记日志，不影响其它插件的同一事件 |
| `Invalidate` 内部失败 | 记警告，不抛给调用方 |
| `ReadJson` 遇损坏文件 | 返回 `default` + 警告 |
| `WriteJson`/`WriteState` 落盘失败 | 警告（`FlushState` 失败）或静默（`WriteJson` 失败） |

---

## 10. 版本兼容规则

1. `plugin.json` 的 `apiVersion` 是「我需要的最低契约大版本」；大于宿主支持值 → **拒绝加载**并提示用户升级宿主或换旧版插件；
2. 宿主当前 `HostApiVersion = 1`、`PluginManager.ApiVersion = 1.0`；
3. 大版本内宿主只增成员不改语义；数据记录字段只增不减；
4. 插件侧建议在 `Initialize` 里比对 `host.ApiVersion`，遇到更新的宿主可按保守模式运行（少注册几个显示位）；
5. 显示位的钳制范围、竞争规则、null 语义属于契约的一部分——与本文档描述不一致即是契约破坏，应升大版本。

---

## 11. 附录：成员速查索引

### IPlugin（2）

| 成员 | 一句话 |
| --- | --- |
| `void Initialize(IPluginHost host)` | 加载成功后 UI 线程调用一次，做注册 |
| `void Shutdown()` | 禁用/重载/退出前调用，别抛异常 |

### IPluginHost（48）

| # | 成员 | 一句话 |
| --- | --- | --- |
| 1 | `Version ApiVersion` | 契约版本（1.0） |
| 2 | `string HostVersion` | 宿主版本号 |
| 3 | `string PluginId` | 本插件 id |
| 4 | `string DataDirectory` | `plugins/<id>/data/` |
| 5 | `string PluginDirectory` | `plugins/<id>/` |
| 6 | `void Log(string)` | 普通日志 |
| 7 | `void Warn(string)` | 警告日志 |
| 8 | `void Error(string)` | 错误日志 |
| 9 | `string RegisterIslandText(IslandTextSpec)` | 主岛文案（单值） |
| 10 | `string RegisterIslandExtra(IslandExtraSpec)` | 主岛加宽（单值） |
| 11 | `string RegisterIslandBadge(IslandBadgeSpec)` | 主岛徽标（单值） |
| 12 | `string RegisterSubIsland(SubIslandSpec)` | 副岛内容（单值） |
| 13 | `string RegisterPanel(PanelSpec)` | 下拉面板（多值） |
| 14 | `string RegisterBoardSection(BoardSectionSpec)` | 主窗区块（多值） |
| 15 | `string RegisterBoardBadge(BoardBadgeSpec)` | 主窗徽标（单值） |
| 16 | `string RegisterBoardAction(BoardActionSpec)` | 主窗右键菜单（多值） |
| 17 | `string RegisterTrayAction(TrayActionSpec)` | 托盘菜单（多值） |
| 18 | `void Unregister(string)` | 按注册 id 注销 |
| 19 | `void Invalidate()` | 催宿主立刻重算 |
| 20 | `DateTime Now` | 应用内现在（含偏移） |
| 21 | `HostSnapshot Snapshot` | 课表状态快照 |
| 22 | `IReadOnlyList<CourseEntry> CoursesOfDay(DateTime)` | 某天全部课程 |
| 23 | `CourseEntry? CurrentCourse` | 正在上的课 |
| 24 | `CourseEntry? NextCourse` | 下一节课 |
| 25 | `DutySnapshot Duty` | 值日出勤 |
| 26 | `IReadOnlyList<HomeworkEntry> Homework` | 全部作业 |
| 27 | `string? GetSetting(string)` | 读设置原始值 |
| 28 | `int GetSettingInt(string, int)` | 读整数设置 |
| 29 | `bool GetSettingBool(string, bool)` | 读布尔设置 |
| 30 | `HostWorkArea WorkArea` | 工作区（DIP） |
| 31 | `double DpiScale` | DPI 缩放 |
| 32 | `bool IsMainWindowVisible` | 主窗可见性 |
| 33 | `bool IsIslandVisible` | 灵动岛可见性 |
| 34 | `string? ReadState(string)` | 读键值状态 |
| 35 | `void WriteState(string, string?)` | 写键值状态 |
| 36 | `void FlushState()` | 状态落盘 |
| 37 | `T? ReadJson<T>(string)` | 读私有 JSON |
| 38 | `void WriteJson<T>(string, T)` | 写私有 JSON |
| 39 | `void Toast(string, string)` | 托盘气泡 |
| 40 | `void OpenSettings(string?)` | 打开设置（可定位页） |
| 41 | `void OpenUrl(string)` | 开链接（限 http/https） |
| 42 | `void OpenFolder(string)` | 开目录（限程序目录内） |
| 43 | `void ShowMainWindow(bool)` | 显隐主窗 |
| 44 | `void ShowIsland(bool)` | 显隐灵动岛 |
| 45 | `void SetSubIslandExpanded(bool)` | 展开/收起副岛 |
| 46 | `event EventHandler<HostTickEventArgs>? Tick` | 每秒 tick |
| 47 | `event EventHandler<HostSettingsEventArgs>? SettingsChanged` | 设置改动 |
| 48 | `event EventHandler? IslandClicked` | 灵动岛被点击 |

### 常量与枚举

| 成员 | 值 |
| --- | --- |
| `HostStatus.Upcoming` | `"upcoming"` |
| `HostStatus.Ongoing` | `"ongoing"` |
| `HostStatus.Holiday` | `"holiday"` |
| `HostStatus.None` | `"none"` |
| `HostStatus.Done` | `"done"` |
| `BadgeTone` | `Default(0)` / `Accent(1)` / `Success(2)` / `Warning(3)` / `Danger(4)` |

---

*本文档与 `ClassDailyLand.Plugin.Abstractions` 源码同源生成口径；源码变更时以代码为准，并应同步更新本文档。*
