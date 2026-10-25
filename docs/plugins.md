# 插件开发指南（完整版）

插件是**一个 .NET 程序集 + 一份 `plugin.json`**，装进 `plugins/<id>/` 就生效。
宿主启动时反射加载它，然后把「灵动岛 / 副岛 / 下拉面板 / 班级日常主窗 / 托盘」里的
若干**显示位**交给它填内容。

插件跑在自己的 `AssemblyLoadContext` 里，所以：

- 禁用 / 重载插件**不需要重启应用**；
- 插件之间的依赖版本互不干扰；
- 插件炸了不会带走宿主（异常一律被接住并记日志）。

---

## 目录

- [0. 30 秒速览](#0-30-秒速览)
- [1. 环境准备](#1-环境准备)
- [2. 第一个插件（10 分钟跑起来）](#2-第一个插件10-分钟跑起来)
- [3. plugin.json 参考](#3-pluginjson-参考)
- [4. 生命周期](#4-生命周期)
- [5. 九个显示位详解](#5-九个显示位详解)
- [6. 数据模型](#6-数据模型)
- [7. API 全表（48 个成员）](#7-api-全表48-个成员)
- [8. 实战：写一个「下节课倒计时 + 天气」插件](#8-实战写一个下节课倒计时--天气插件)
- [9. 打包与分发](#9-打包与分发)
- [10. 调试手册](#10-调试手册)
- [11. 性能预算与反模式](#11-性能预算与反模式)
- [12. 安全与边界](#12-安全与边界)
- [13. 版本兼容](#13-版本兼容)
- [14. 错误速查](#14-错误速查)
- [15. 提交前检查清单](#15-提交前检查清单)
- [16. FAQ](#16-faq)

---

## 0. 30 秒速览

**插件能做什么**

| 能 | 不能 |
| --- | --- |
| 往九个显示位写文字 / 徽标 / 进度 | 往宿主界面里插任意 UI 控件 |
| 读课表、作业、值日、可见性、工作区 | 改宿主的设置（只能读） |
| 自己存数据（键值 + JSON 文件） | 直接写别人的数据文件 |
| 弹托盘气泡、开设置页、开链接 | 弹自己的窗口（没有需要的场景） |
| 注册右键菜单项 / 托盘菜单项 | 改宿主的事件行为 |

**三条硬约束**（违反任何一条，插件都会被禁用或被用户骂）

1. **回调里不许做重活**。注册给宿主的 `Func<>` 每个都在 UI 线程每秒跑一次，
   里面**不能有 IO、网络、数据库、锁等待、大数组计算**。要拉数据自己起线程，回调只读缓存。
2. **不要带自己的契约 DLL**。`ClassDailyLand.Plugin.Abstractions.dll` 只能由宿主提供，
   插件目录里放一份就再也加载不起来（详见 [1.3](#13-为什么必须-privatefalse)）。
3. **时间只认 `host.Now`**。它已经叠加了用户设置的"时间偏移"；
   用 `DateTime.Now` 在用户调过时间之后就会和界面显示对不上。

---

## 1. 环境准备

### 1.1 需要什么

| 项 | 要求 |
| --- | --- |
| .NET SDK | 8.0 或更高（仓库用 10 编译） |
| 目标框架 | `net10.0`（**不要** `-windows`，插件不该依赖 WPF） |
| 引用的程序集 | **只有** `ClassDailyLand.Plugin.Abstractions.dll` |
| 编辑器 | 任意；Visual Studio / Rider / VS Code + C# 扩展都行 |

> 为什么不能依赖 WPF：插件跑在 UI 线程的回调里，如果它自己引用 WPF 并创建控件，
> 会绕开宿主的视觉语言（深浅主题、圆角、字号）自己长出一套界面。
> 契约只提供**数据**，渲染由宿主负责 —— 这是插件能跟主题一起变色的原因。

### 1.2 工程模板（可复制）

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>MyPlugin</RootNamespace>
    <AssemblyName>MyPlugin</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>

  <ItemGroup>
    <!-- ★ 关键：Private=false + ExcludeAssets=runtime -->
    <ProjectReference Include="..\..\ClassDailyLand.Plugin.Abstractions\ClassDailyLand.Plugin.Abstractions.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>

  <ItemGroup>
    <!-- plugin.json 跟着输出走，装的时候整个输出目录丢进去就行 -->
    <None Update="plugin.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>

</Project>
```

如果契约是从 NuGet 或单独 dll 拿到的，把 `ProjectReference` 换成：

```xml
<ItemGroup>
  <Reference Include="ClassDailyLand.Plugin.Abstractions">
    <HintPath>..\lib\ClassDailyLand.Plugin.Abstractions.dll</HintPath>
    <Private>false</Private>
  </Reference>
</ItemGroup>
```

### 1.3 为什么必须 `Private=false`

宿主用 `AssemblyLoadContext` 加载你的插件。加载器里有一条规则：

> 任何名为 `ClassDailyLand.Plugin.Abstractions` 的程序集，**一律从默认上下文（宿主自己那份）解析**，
> 不从插件目录加载。

这是故意的。如果插件目录里也有一份契约 DLL，就会出现**两个同名但不同实例的 `IPlugin` 类型**：

```
宿主里的 IPlugin  ≠  插件里的 IPlugin
```

后果：

- `typeof(IPlugin).IsAssignableFrom(插件类型)` **恒为 false**；
- 宿主找不到"实现了 IPlugin 的类"，于是 `Initialize` **永远不会被调用**；
- 设置页显示「加载失败：没有找到 IPlugin 实现」，但你代码明明写了。

所以：**插件目录里不要出现 `ClassDailyLand.Plugin.Abstractions.dll`**。
上面模板里的 `Private=false` + `ExcludeAssets=runtime` 就是干这个的。

### 1.4 目录结构

开发目录随便放，装到宿主里之后长这样：

```
plugins/
  hello/                  ← 目录名 = plugin.json 里的 id
    plugin.json           ← 清单
    HelloPlugin.dll       ← 入口程序集
    MyPlugin.deps.json    ← 有依赖时会有
    data/                 ← 宿主创建的私有数据目录（插件不用自己建）
      state.json          ← WriteState 落的键值
      你的名字.json       ← WriteJson 落的文件
```

---

## 2. 第一个插件（10 分钟跑起来）

### 2.1 建工程

```bash
mkdir MyPlugin && cd MyPlugin
dotnet new classlib -f net10.0
```

删掉模板生成的 `Class1.cs`，换成下面的 `plugin.json` + `MyPlugin.cs`，
并把 [1.2](#12-工程模板可复制) 的 csproj 覆盖过去。

### 2.2 plugin.json

```json
{
  "id": "hello",
  "name": "示例插件",
  "version": "1.0.0",
  "description": "演示主岛文案、徽标与主窗区块。",
  "author": "你的名字",
  "entry": "MyPlugin.dll",
  "type": "MyPlugin.MyPlugin",
  "apiVersion": 1
}
```

> `id` 是目录名，`entry` 是**文件名**（不能带路径），`type` 是**完整类型名**。
> 三个只要错一个，插件就加载不起来 —— 这是最常见的失败点。

### 2.3 代码

```csharp
using ClassDailyLand.Plugin;

namespace MyPlugin;

public sealed class MyPlugin : IPlugin
{
    IPluginHost? _host;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        host.Log($"hello 已加载，数据目录：{host.DataDirectory}");

        // 主岛文案后缀：非空时拼成「正在上课 · 数学 · 你好」
        host.RegisterIslandText(new IslandTextSpec(() => "你好"));

        // 主岛右侧徽标
        host.RegisterIslandBadge(new IslandBadgeSpec(
            () => new IslandBadge("HI", BadgeTone.Accent)));

        // 主窗追加区块
        host.RegisterBoardSection(new BoardSectionSpec(() =>
            new BoardSection("我的区块", new[] { "第一行", "第二行" })));
    }

    public void Shutdown() => _host = null;
}
```

### 2.4 编译并安装

```bash
dotnet build -c Release
```

然后在应用里：**设置 → 插件 → 从文件夹安装… → 选 `bin/Release/net10.0`**。

不想走界面也行，直接把整个输出目录拷进 `plugins/hello/`，再点「重新扫描插件目录」。

### 2.5 应该看到什么

| 位置 | 期望 |
| --- | --- |
| 灵动岛（顶部胶囊） | 文案末尾多了 `· 你好`，右端多了蓝色 `HI` 徽标 |
| 班级日常主窗 | 作业列表下面多了「我的区块」+ 两行正文 |
| 设置 → 插件 | 出现一行「示例插件 v1.0.0 已加载」，运行日志里有 `hello 已加载…` |

### 2.6 逐行解释

| 代码 | 为什么要这么写 |
| --- | --- |
| `Initialize(IPluginHost host)` | 唯一的入口。**只在 UI 线程调用一次**，注册显示位都写在这里 |
| `host.Log(...)` | 写进插件页的「运行日志」，同时落 `app.log` 的 `[plugin:hello]` 行 |
| `new IslandTextSpec(() => "你好")` | 传的是**委托**不是字符串。宿主每秒调一次，所以返回值可以随时变 |
| `BadgeTone.Accent` | 色调由宿主按主题取色，插件不传颜色 —— 深浅色两套主题才都好看 |
| `Shutdown()` | 禁用/重载/卸载/退出前调用。**不要在这里等异步任务**，宿主不会等你 |

---

## 3. plugin.json 参考

### 3.1 完整字段表

| 字段 | 类型 | 必填 | 默认 | 约束 |
| --- | --- | --- | --- | --- |
| `id` | string | **是** | — | 1~64 字符，只允许字母数字与 `.` `_` `-`；**就是安装目录名**，重复会被当成覆盖安装 |
| `name` | string | 否 | `id` | 显示名，出现在插件页与错误信息里 |
| `version` | string | 否 | `1.0.0` | 纯显示用，宿主不做比较，也不阻止降级 |
| `description` | string | 否 | 空 | 插件页里的一行说明 |
| `author` | string | 否 | 空 | 作者 |
| `entry` | string | **是** | — | 入口程序集**文件名**（如 `MyPlugin.dll`）。带路径或不存在会加载失败 |
| `type` | string | **是** | — | 实现 `IPlugin` 的完整类型名（`命名空间.类名`）。找不到时会退化为"程序集里第一个 IPlugin 实现"，但仍建议写对 |
| `apiVersion` | int | 否 | `1` | 需要的契约大版本。**大于宿主支持版本会被拒绝加载**（不降级、不猜测） |
| `homepage` | string | 否 | 空 | 主页链接，显示在插件行里（不自动打开） |

> 清单解析是**严格**的：未知字段会被忽略（前向兼容），但已知字段类型不对会直接判失败，
> 并在插件页标红说明原因。

### 3.2 全字段示例

```json
{
  "id": "countdown.weather",
  "name": "下节课倒计时 + 天气",
  "version": "1.2.3",
  "description": "主岛显示下节课还有多久，副岛显示当前天气与温度。",
  "author": "你的名字",
  "entry": "CountdownWeather.dll",
  "type": "CountdownWeather.Plugin",
  "apiVersion": 1,
  "homepage": "https://example.com/my-plugin"
}
```

### 3.3 id 的命名与冲突

- 推荐用 `作者.功能` 或 `功能.子功能` 的点分层级：`foo.weather`、`foo.todo`。
- 只用小写字母、数字、点、下划线、短横线；**不要中文、不要空格**。
- id 决定安装目录：装 `foo.weather` 就会写进 `plugins/foo.weather/`。
- 同一个 id 再装一次 = **覆盖安装**（默认拒绝，勾"覆盖"后旧目录先改名备份，失败会回滚）。

### 3.4 apiVersion 语义

| 宿主的 `ApiVersion` | 插件写 `apiVersion: 1` | 插件写 `apiVersion: 2` |
| --- | --- | --- |
| `1.x` | ✅ 正常加载 | ❌ 拒绝："插件需要更新的宿主" |

宿主只在**破坏性变更**时提升主版本（改接口签名、删显示位、改 Spec 字段语义）。
加新成员不提升主版本，所以老插件在新宿主上永远能跑。

### 3.5 常见写法错误

| 写错 | 实际后果 |
| --- | --- |
| `"entry": "bin/MyPlugin.dll"` | 拒绝加载（entry 不许带路径） |
| `"entry": "MyPlugin"` | 找不到文件（要带 `.dll`） |
| `"type": "MyPlugin"` | 类型找不到 → 退化为"第一个 IPlugin"，若程序集里不止一个实现就会加载错 |
| `"apiVersion": "1"` | 类型不符（要数字，不要字符串） |
| `"id": "我的插件"` | 校验失败（只允许 ASCII） |
| JSON 带 BOM 或注释 | 解析失败（标准 JSON，不支持注释） |

---

## 4. 生命周期

### 4.1 加载时序

```
扫描 plugins/*/plugin.json
   │
   ├─ 清单校验失败 ────────────────→ 只出现在设置页（标红 + 原因），不进内存
   │
   ├─ 读 plugins.json 的启用状态
   │     └─ 被禁用 ───────────────→ 只列出，不加载
   │
   ├─ 建 collectible AssemblyLoadContext
   ├─ Assembly.LoadFrom(<插件目录>/<entry>)
   ├─ Activator.CreateInstance(<type>)        ← 需要 public 无参构造
   ├─ Initialize(host)                        ← 注册显示位都写在这里
   │
   …… 运行期：宿主每秒在 UI 线程回调你的 Func，并转发 Tick 事件 ……
   │
   ├─ Shutdown()                              ← 禁用 / 重载 / 卸载 / 退出前
   ├─ 撤销这个插件注册的全部显示位
   ├─ FlushState()                            ← 键值状态落盘
   └─ ALC.Unload()
```

### 4.2 三种"卸载"路径

| 操作 | `Shutdown` | 目录 | 数据 | 重新启用 |
| --- | --- | --- | --- | --- |
| **禁用** | 调 | 保留 | 保留 | 重新走一遍 `Initialize` |
| **重载** | 调 | 保留 | 保留 | 立刻重新 `Initialize`（改完 dll 用它） |
| **卸载** | 调 | **删掉** | **删掉** | 要重新安装 |

> 重载的意义：你改完代码重新编译，点一下「重载」就能看到新行为，**不用重启应用**。
> 如果 dll 被占用（上次加载没完全释放），先「禁用」再「启用」。

### 4.3 线程归属

| 回调 / 成员 | 线程 | 说明 |
| --- | --- | --- |
| `Initialize` / `Shutdown` | UI | 都只有一次，且同步 |
| 所有 `Register*Spec(Func<…>)` 里的 Func | **UI** | 每秒调用一次，必须快 |
| `Tick` 事件 | **UI** | 与宿主的刷新同一个 tick |
| `SettingsChanged` / `IslandClicked` | **UI** | |
| `Toast` / `OpenSettings` / `OpenUrl` / `OpenFolder` | 任意线程安全 | 宿主动作都做了线程切换 |
| `ReadState` / `WriteState` / `ReadJson` / `WriteJson` | 任意（宿主内部加锁） | 但别在 UI 回调里做 |
| 你自己的后台线程 | 你负责 | 结果只写字段，让 UI 回调去读 |

### 4.4 数据保存在哪里

```
plugins/<id>/data/
  state.json           ← ReadState / WriteState 的键值（FlushState 落盘）
  <name>.json          ← WriteJson("name", obj) 落的文件（原子写）
```

这是**插件私有**目录：卸载插件会连它一起删。要跨卸载保留的东西自己往 `%APPDATA%` 放。

### 4.5 重载会保留什么

| 保留 | 不保留 |
| --- | --- |
| 目录里的所有文件 | 内存里的字段（新实例从零开始） |
| `data/state.json` 与 JSON 文件 | 你注册的显示位 id（要重新注册） |
| 启用状态 | 事件订阅（要重新订阅） |
| 播放器/网络连接 | 后台线程（旧线程随旧实例一起被丢弃，**记得在 `Shutdown` 里停掉**） |

---

## 5. 九个显示位详解

### 5.1 主岛文案补充（`RegisterIslandText`）

```csharp
host.RegisterIslandText(new IslandTextSpec(Func<string?> Text, int Priority = 0));
```

| 项 | 说明 |
| --- | --- |
| `Text` | 每秒在 UI 线程调用。返回 `null`/空白 = 本秒不追加 |
| 拼接方式 | `核心文案 + " · " + 你的文案`，例：`正在上课 · 数学 · 距下课 12 分` |
| 长度 | 建议 ≤ 12 个汉字；太长会触发跑马灯（滚动显示） |
| `Priority` | 单值位竞争权重，大者胜 |

```csharp
host.RegisterIslandText(new IslandTextSpec(() =>
{
    var s = host.Snapshot;
    return s.Status == HostStatus.Ongoing ? $"距下课 {s.RemainMinutes} 分" : null;
}, Priority: 10));
```

> **坑**：文案每秒都可能换。换得越勤，岛上的"上移淡出"动画越闹。
> 只在真正变化时返回不同字符串 —— 宿主会比对，同一字符串不会重播动画。

### 5.2 主岛加长段（`RegisterIslandExtra`）

```csharp
host.RegisterIslandExtra(new IslandExtraSpec(int Width, int Priority = 0));
```

给胶囊**额外预留 N 像素**。什么时候需要它：

- 你要在岛上显示比默认宽度更长的一段自定义文字（徽标位会自动算宽度，不用它）；
- 你给别的插件/外部程序留了位置。

| 项 | 说明 |
| --- | --- |
| `Width` | DIP。典型 40~200，别超过 300（屏幕窄时胶囊会贴边） |
| 与徽标一起用 | 徽标宽度由宿主**自动叠加**，不需要你在 `Width` 里算它 |

> **坑**：`Width` 太小 + 文案太长 → 文字被挤压或跑马灯。宁可多留 20px。

### 5.3 主岛徽标（`RegisterIslandBadge`）

```csharp
host.RegisterIslandBadge(new IslandBadgeSpec(Func<IslandBadge?> Badge, int Priority = 0));

public sealed record IslandBadge(string Text, BadgeTone Tone = BadgeTone.Default);
```

| 项 | 说明 |
| --- | --- |
| `Text` | 建议 1~4 个字符（`DEMO`、`WiFi`、`23°`）。宽度宿主用 `FormattedText` 量，不会压字 |
| `Tone` | `Default` / `Accent` / `Success` / `Warning` / `Danger` —— 宿主按主题取色 |
| 返回 null | 本秒不显示徽标（胶囊自动缩回去） |

> **坑**：灾害预警接管胶囊时（红色倒计时条），徽标会被强制隐藏；
> 媒体片段显示时也会让位。这两种情况是设计如此，不是 bug。

### 5.4 副岛（`RegisterSubIsland`）

```csharp
host.RegisterSubIsland(new SubIslandSpec(Func<SubIslandInfo?> Content, int Priority = 0));

public sealed record SubIslandInfo(string Title, string Value, double Progress = 0);
```

副岛平时收成 40×40 的小圆，点击展开成长条。

| 字段 | 说明 |
| --- | --- |
| `Title` | 短标签（1~3 字），如 `天气`、`作业` |
| `Value` | 主要数值，如 `晴 23°`、`3 项` |
| `Progress` | 0~1，画成圆环里的进度点；不需要就给 0 |

> **坑**：`Title` 与 `Value` 都为空时，宿主认为"这一秒没内容"，副岛会回退到内置的当前课程。
> 这是有意的回退，不是加载失败。

### 5.5 下拉面板（`RegisterPanel`）

```csharp
host.RegisterPanel(new PanelSpec(
    string Title,
    double Width,
    Func<IReadOnlyList<string>?> Lines,
    double MaxHeightRatio = 1.0 / 3.0,
    int Priority = 0));
```

点主岛弹出的面板区。多个插件面板会与内置「今日课程」**并排**排开。

| 字段 | 说明 |
| --- | --- |
| `Title` | 面板标题（当前只用于日志与无障碍名称） |
| `Width` | 典型 200~320 DIP；总宽超过屏幕 80% 时，**最先丢掉插件面板**（从右往左） |
| `Lines` | 逐行文本。空行 `""` 可以用来做分段 |
| `MaxHeightRatio` | 最大高度占工作区高度的比例，默认 1/3，会被夹到 0.1~0.6 |
| 返回 null | 这一秒不显示这个面板 |

```csharp
host.RegisterPanel(new PanelSpec("作业", 220, () =>
{
    var items = host.Homework;
    if (items.Count == 0) return new[] { "没有作业" };

    var lines = new List<string> { $"共 {items.Count} 项", "" };
    foreach (var h in items.Take(8))
        lines.Add($"· {h.Subject}  {h.Content}");
    if (items.Count > 8) lines.Add($"…还有 {items.Count - 8} 项");
    return lines;
}));
```

> 面板是**纯文字**的（与作业列表同一套排版）。想要表格/图片请拆成多行文本表达。

### 5.6 主窗区块（`RegisterBoardSection`）

```csharp
host.RegisterBoardSection(new BoardSectionSpec(Func<BoardSection?> Section, int Priority = 0));

public sealed record BoardSection(string Title, IReadOnlyList<string> Lines);
```

追加在「班级日常」主窗的作业列表下面，等比作业那套排版（标题加粗 + 缩进正文）。

```csharp
host.RegisterBoardSection(new BoardSectionSpec(() =>
{
    var duty = host.Duty;
    return new BoardSection("值日提醒", new[]
    {
        $"今天是 {duty.TodayName}",
        $"应到 {duty.ShouldCount} · 实到 {duty.ActualCount}",
    });
}));
```

> **坑**：这个回调也是每秒调用。宿主按"内容签名"去重，内容没变不会重建可视树 ——
> 但如果你每秒都返回**新构造但内容相同**的对象，签名一样，所以没问题；
> 真正会让作业列表滚动条被拉回顶部的，是内容签名**一直变**（比如每行都带秒）。

### 5.7 主窗徽标（`RegisterBoardBadge`）

```csharp
host.RegisterBoardBadge(new BoardBadgeSpec(Func<BoardBadge?> Badge, int Priority = 0));

public sealed record BoardBadge(string Text, BadgeTone Tone = BadgeTone.Default);
```

画在「班级日常」标题行右侧的小胶囊，如 `Wi-Fi 正常`、`值日已排`。

> 主窗标题行用 `WrapPanel` 排版：窗口窄时徽标会**自动换到下一行**，不会把标题挤没。

### 5.8 主窗右键菜单（`RegisterBoardAction`）

```csharp
host.RegisterBoardAction(new BoardActionSpec(string Text, Action Invoke, int Order = 50));
```

排在宿主自带项（随机点名 / 设置 / 退出）**之前**。`Order` 越小越靠上，宿主默认项从 100 起。

```csharp
host.RegisterBoardAction(new BoardActionSpec("刷新天气", () =>
{
    _needRefresh = true;                    // 只翻个标志，别在菜单回调里直接发网络请求
    host.Toast("天气", "正在刷新…");
}, Order: 20));
```

> **坑**：`Invoke` 在 UI 线程执行，做重活会让菜单卡住。要拉数据就翻标志，让后台线程去干。

### 5.9 托盘菜单（`RegisterTrayAction`）

```csharp
host.RegisterTrayAction(new TrayActionSpec(string Text, Action Invoke, int Order = 50));
```

排在「设置」之后、「退出」之前（不干扰用户找退出）。

```csharp
host.RegisterTrayAction(new TrayActionSpec("打开插件设置", () => host.OpenSettings("plugins"), Order: 10));
```

### 5.10 竞争规则

**单值位**（岛文案 / 岛加长段 / 岛徽标 / 副岛 / 主窗徽标）

1. 按 `Priority` 从大到小逐个问；
2. 平级时**先注册者**优先；
3. **返回 null/空白视为"这一秒没内容"，继续问下一个候选**；
4. 全都没有内容 → 回退到内置内容（副岛回退到当前课程）。

第 3 条很重要：它意味着高优先级插件**临时没数据时不会把显示位空着**，会自动落到次高优先级。

**多值位**（面板 / 主窗区块 / 主窗菜单 / 托盘菜单）

- 全部显示，不竞争；
- 面板按注册顺序从左到右；
- 菜单按 `Order` **升序**（越小越靠上），同 `Order` 按注册顺序。

**异常隔离**

任何回调抛异常：

- 宿主接住，写进插件日志（`[plugin:<id>]` 行）；
- 该显示位这一秒按"没内容"处理（单值位会继续问下一个候选）；
- **不会**禁用插件、不会崩宿主。

### 5.11 岛宽的算法（想知道为什么被挤压就看这里）

```
胶囊总宽 = 基础宽度（240）
         + 插件加长段（IslandExtraSpec.Width，单值位取赢家）
         + 徽标实测宽度 + 10（有徽标时）
         + 媒体片段宽度 + 8（有正在播放的媒体时）
```

三个"额外"部分互不覆盖，是**相加**的。所以：

- 同时注册加长段 + 徽标，实际宽度是两者之和；
- 灾害预警接管时（红条态）额外部分**全部归零**，胶囊固定 600；
- 状态是 mini / hidden / alert 时，徽标与媒体片段自动隐藏。

---

## 6. 数据模型

```csharp
// 当前课表状态快照（每秒刷新）
public sealed record HostSnapshot(
    string Status,          // "upcoming" / "ongoing" / "holiday" / "none" / "done"
    string StatusText,      // 给用户看的整句，如「正在上课 · 数学」
    string Course,          // 课名
    int Index,              // 第几节（1 起）
    string Start,           // "08:00"（已叠加时间偏移）
    string End,             // "08:45"
    int RemainMinutes,      // 距下课还有多少分钟（下课时为 0）
    int UntilMinutes,       // 距下节课还有多少分钟（上课时为 0）
    string? HolidayName,    // 假期名
    DateTime Now);          // 与 host.Now 一致

// 一节课
public sealed record CourseEntry(int Index, string Course, string Start, string End, bool IsOngoing);

// 值日与出勤
public sealed record DutySnapshot(string TodayName, int ShouldCount, int ActualCount);

// 一条作业
public sealed record HomeworkEntry(string Subject, string Content, string Date);

// 主屏工作区（DIP）
public sealed record HostWorkArea(double X, double Y, double Width, double Height, double Scale);

// 状态常量（别自己写字面量）
public static class HostStatus
{
    public const string Upcoming = "upcoming";
    public const string Ongoing  = "ongoing";
    public const string Holiday  = "holiday";
    public const string None     = "none";
    public const string Done     = "done";
}

// 徽标色调
public enum BadgeTone { Default, Accent, Success, Warning, Danger }
```

---

## 7. API 全表（48 个成员）

### 一、元信息与日志（8）

| 成员 | 签名 | 备注 |
| --- | --- | --- |
| `ApiVersion` | `Version` | 契约版本，如 `1.0` |
| `HostVersion` | `string` | 宿主版本，如 `1.0.0` |
| `PluginId` | `string` | 清单里的 id |
| `DataDirectory` | `string` | `plugins/<id>/data/`，**已创建** |
| `PluginDirectory` | `string` | `plugins/<id>/` |
| `Log` | `void Log(string)` | 进插件页「运行日志」+ `app.log` |
| `Warn` | `void Warn(string)` | 黄色 |
| `Error` | `void Error(string)` | 红色 |

### 二、显示位注册（11）

| 成员 | 返回 | 备注 |
| --- | --- | --- |
| `RegisterIslandText(IslandTextSpec)` | 注册 id | 单值 |
| `RegisterIslandExtra(IslandExtraSpec)` | 注册 id | 单值 |
| `RegisterIslandBadge(IslandBadgeSpec)` | 注册 id | 单值 |
| `RegisterSubIsland(SubIslandSpec)` | 注册 id | 单值 |
| `RegisterPanel(PanelSpec)` | 注册 id | **可多个** |
| `RegisterBoardSection(BoardSectionSpec)` | 注册 id | **可多个** |
| `RegisterBoardBadge(BoardBadgeSpec)` | 注册 id | 单值 |
| `RegisterBoardAction(BoardActionSpec)` | 注册 id | **可多个** |
| `RegisterTrayAction(TrayActionSpec)` | 注册 id | **可多个** |
| `Unregister(string id)` | — | 传不存在的 id 不报错 |
| `Invalidate()` | — | 别等下一秒，现在就重算 |

### 三、只读数据（14）

| 成员 | 签名 | 备注 |
| --- | --- | --- |
| `Now` | `DateTime` | **已叠加时间偏移**，时间判断只认它 |
| `Snapshot` | `HostSnapshot` | 每秒更新 |
| `CoursesOfDay(DateTime day)` | `IReadOnlyList<CourseEntry>` | 已叠加偏移 |
| `CurrentCourse` | `CourseEntry?` | 没课为 null |
| `NextCourse` | `CourseEntry?` | 没下节为 null |
| `Duty` | `DutySnapshot` | |
| `Homework` | `IReadOnlyList<HomeworkEntry>` | 按录入顺序 |
| `GetSetting(string)` | `string?` | 原始值，不存在为 null |
| `GetSettingInt(string, int)` | `int` | 类型不符返回 fallback |
| `GetSettingBool(string, bool)` | `bool` | 同上 |
| `WorkArea` | `HostWorkArea` | 主屏工作区（DIP） |
| `DpiScale` | `double` | 1.0 = 100% |
| `IsMainWindowVisible` | `bool` | |
| `IsIslandVisible` | `bool` | |

### 四、插件私有存储（5）

| 成员 | 签名 | 备注 |
| --- | --- | --- |
| `ReadState(string key)` | `string?` | 来自 `data/state.json` |
| `WriteState(string, string?)` | — | 内存即时，落盘看 Flush |
| `FlushState()` | — | 立刻落盘（宿主卸载前也会调一次） |
| `ReadJson<T>(string name)` | `T?` | 不存在或损坏返回 default |
| `WriteJson<T>(string, T)` | — | 原子写 `data/<name>.json` |

### 五、宿主动作（7）

| 成员 | 签名 | 备注 |
| --- | --- | --- |
| `Toast` | `void Toast(string title, string message)` | 托盘气泡，线程安全 |
| `OpenSettings` | `void OpenSettings(string? pageKey = null)` | 页键见下 |
| `OpenUrl` | `void OpenUrl(string url)` | **只放行 http/https**，其它协议被拒并记日志 |
| `OpenFolder` | `void OpenFolder(string path)` | **只允许程序目录与插件目录内部** |
| `ShowMainWindow` | `void ShowMainWindow(bool show)` | |
| `ShowIsland` | `void ShowIsland(bool show)` | |
| `SetSubIslandExpanded` | `void SetSubIslandExpanded(bool expanded)` | |

可用的 `pageKey`：

```
home / general / island / media / disaster / interaction /
appearance / schedule / homework / plugins / advanced / about
```

### 六、事件（3）

| 事件 | 参数 | 时机 |
| --- | --- | --- |
| `Tick` | `HostTickEventArgs.Snapshot` | 每秒一次（UI 线程） |
| `SettingsChanged` | `HostSettingsEventArgs.Keys` | 宿主设置被改动 |
| `IslandClicked` | — | 用户点了灵动岛 |

```csharp
host.Tick += (_, e) =>
{
    // e.Snapshot 与 host.Snapshot 一致；这里能拿到"这一 tick 的"快照
    if (e.Snapshot.Status == HostStatus.Ongoing) _lastSeen = e.Snapshot.Course;
};

host.SettingsChanged += (_, e) =>
{
    if (e.Keys.Any(k => k.StartsWith("theme"))) ReloadColors();
};

host.IslandClicked += (_, _) => _needRefresh = true;
```

---

## 8. 实战：写一个「下节课倒计时 + 天气」插件

目标：主岛文案显示下节课还有多久，副岛显示天气与温度，下拉面板显示未来 3 小时。

### 8.1 痛点与设计

| 需求 | 做法 | 为什么 |
| --- | --- | --- |
| 天气数据 | **后台线程**每 10 分钟拉一次 Open-Meteo，结果写字段 | 回调里绝不能发网络请求 |
| 主岛文案 | 回调只读 `host.Snapshot` + 缓存的温度 | 全都是内存读，微秒级 |
| 失败降级 | 拉不到就把 `_weather` 置空，文案自动少一段 | 返回 null 是合法的"这秒不显示" |
| 城市 | 从宿主设置读 `disaster_province` / `disaster_city` 的经纬度 | 复用用户已经在灾害预警里选过的位置 |

### 8.2 文件

```
MyPlugin/
  plugin.json
  MyPlugin.csproj
  Plugin.cs           ← IPlugin 实现 + 显示位注册
  WeatherClient.cs    ← 后台拉数据（不碰 UI）
```

### 8.3 后台拉数据（`WeatherClient.cs`）

```csharp
using System.Globalization;
using System.Text.Json;

namespace CountdownWeather;

/// <summary>
/// 后台拉天气。只写字段，不碰任何 UI —— 宿主会在 UI 线程读这些字段。
/// </summary>
sealed class WeatherClient
{
    readonly System.Net.Http.HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    readonly Action<string> _log;

    public WeatherClient(Action<string> log) => _log = log;

    // ↓ 这三个字段是"跨线程只写一次、UI 线程只读"，用 volatile 保证可见性
    volatile string? _text;             // "晴 23°"
    volatile string? _error;
    volatile string[] _hours = Array.Empty<string>();

    public string? Text => _text;
    public string? Error => _error;
    public IReadOnlyList<string> Hours => _hours;

    /// <summary>拉一次；失败只记日志并保留旧值（别把界面清空）。</summary>
    public async Task RefreshAsync(double lat, double lon, CancellationToken ct)
    {
        try
        {
            // Open-Meteo 免费、免 key；current 只取温度和天气码
            var url = string.Format(CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}" +
                "&current=temperature_2m,weather_code&hourly=temperature_2m&forecast_days=1&timezone=auto",
                lat, lon);

            using var res = await _http.GetAsync(url, ct).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();

            await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var cur = doc.RootElement.GetProperty("current");
            var temp = cur.GetProperty("temperature_2m").GetDouble();
            var code = cur.GetProperty("weather_code").GetInt32();

            _text = $"{Icon(code)} {Math.Round(temp)}°";
            _error = null;

            // 未来 3 小时
            var hours = doc.RootElement.GetProperty("hourly");
            var temps = hours.GetProperty("temperature_2m");
            var times = hours.GetProperty("time");
            var lines = new List<string>();
            for (var i = 1; i <= 3 && i < temps.GetArrayLength(); i++)
            {
                var t = times[i].GetString() ?? "";
                var hh = t.Length >= 13 ? t.Substring(11, 5) : t;
                lines.Add($"{hh}   {Math.Round(temps[i].GetDouble())}°");
            }
            _hours = lines.ToArray();
        }
        catch (OperationCanceledException) { /* 关窗/卸载，忽略 */ }
        catch (Exception ex)
        {
            _error = ex.Message;
            _log($"天气拉取失败：{ex.Message}");
        }
    }

    static string Icon(int code) => code switch
    {
        0 => "☀", 1 or 2 => "⛅", 3 => "☁",
        45 or 48 => "🌫",
        >= 51 and <= 67 or >= 80 and <= 82 => "🌧",
        >= 71 and <= 77 or >= 85 and <= 86 => "🌨",
        >= 95 => "⛈",
        _ => "☁",
    };
}
```

### 8.4 主类（`Plugin.cs`）

```csharp
using ClassDailyLand.Plugin;

namespace CountdownWeather;

public sealed class Plugin : IPlugin
{
    IPluginHost? _host;
    WeatherClient? _weather;
    CancellationTokenSource? _cts;

    // ——— 这些字段由后台线程写、UI 线程读，都是简单值/不可变引用 ———
    string? _weatherText;
    string[] _weatherHours = Array.Empty<string>();

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _cts = new CancellationTokenSource();
        _weather = new WeatherClient(host.Warn);

        // 位置复用灾害预警里已经选好的经纬度（用户不用再填一遍）
        var (lat, lon) = ReadLocation(host);

        _weatherText = host.ReadState("weather_text");
        _ = LoopAsync(host, lat, lon, _cts.Token);

        // ① 主岛文案：下节课还有多久（纯内存计算）
        host.RegisterIslandText(new IslandTextSpec(() =>
        {
            var s = host.Snapshot;
            var tail = s.Status switch
            {
                HostStatus.Ongoing  => $"距下课 {s.RemainMinutes} 分",
                HostStatus.Upcoming => $"距上课 {s.UntilMinutes} 分",
                _ => null,
            };
            return tail is null ? _weatherText : tail + " · " + _weatherText;
        }, Priority: 5));

        // ② 副岛：天气
        host.RegisterSubIsland(new SubIslandSpec(() =>
            _weatherText is null ? null : new SubIslandInfo("天气", _weatherText)));

        // ③ 下拉面板：未来 3 小时
        host.RegisterPanel(new PanelSpec("天气", 200, () =>
            _weatherHours.Length == 0 ? null : _weatherHours));

        // ④ 主窗徽标：只在有错误时提示
        host.RegisterBoardBadge(new BoardBadgeSpec(() =>
            _weather?.Error is null ? null : new BoardBadge("天气离线", BadgeTone.Warning)));

        // ⑤ 右键菜单：手动刷新
        host.RegisterBoardAction(new BoardActionSpec("刷新天气", () =>
        {
            host.Log("手动刷新天气");
            _ = RefreshOnceAsync(host, lat, lon, _cts.Token);
        }, Order: 20));

        // 位置被用户改了 → 重新取坐标
        host.SettingsChanged += (_, e) =>
        {
            if (!e.Keys.Any(k => k.StartsWith("disaster_"))) return;
            var (newLat, newLon) = ReadLocation(host);
            _ = RefreshOnceAsync(host, newLat, newLon, _cts.Token);
        };
    }

    /// <summary>后台循环：每 10 分钟拉一次（渲染回调里绝不做这件事）。</summary>
    async Task LoopAsync(IPluginHost host, double lat, double lon, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshOnceAsync(host, lat, lon, ct).ConfigureAwait(false);
            try { await Task.Delay(TimeSpan.FromMinutes(10), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    async Task RefreshOnceAsync(IPluginHost host, double lat, double lon, CancellationToken ct)
    {
        if (_weather is null) return;
        await _weather.RefreshAsync(lat, lon, ct).ConfigureAwait(false);

        // 后台线程 → 只搬字段。要落盘用宿主的存储（它自己加锁）
        _weatherText = _weather.Text;
        _weatherHours = _weather.Hours.ToArray();
        if (_weatherText is not null) host.WriteState("weather_text", _weatherText);

        host.Invalidate();          // 立刻重算显示位，别等下一秒
    }

    static (double Lat, double Lon) ReadLocation(IPluginHost host)
    {
        // 灾害预警页里选的经纬度（字符串）；为空就退到成都
        var lat = double.TryParse(host.GetSetting("disaster_lat"), out var a) ? a : 30.66;
        var lon = double.TryParse(host.GetSetting("disaster_lon"), out var b) ? b : 104.06;
        return (lat, lon);
    }

    public void Shutdown()
    {
        _cts?.Cancel();             // ★ 必须停后台线程，否则旧实例会一直发请求
        _cts?.Dispose();
        _cts = null;
        _host = null;
    }
}
```

### 8.5 这个例子里值得抄的四点

1. **后台线程 + 字段**：网络只在线程里跑，`Func<>` 里只读字段，微秒级返回。
2. **失败不毁界面**：拉不到就把 `Error` 往徽标上一放，其它显示位继续用旧值。
3. **`Shutdown` 里 `Cancel`**：重载插件时旧实例的后台循环必须停，否则会越积越多。
4. **`Invalidate()`**：数据到了立刻刷，不用等宿主下一秒的 tick。

---

## 9. 打包与分发

### 9.1 `.cblplugin` 是什么

**就是一个 zip**，扩展名不同而已。结构：

```
hello-1.0.0.cblplugin   (= zip)
  plugin.json           ← 必须在压缩包根目录
  HelloPlugin.dll
```

也允许**整包只套一层文件夹**（从 GitHub 下 zip 解压再打包时常见）：

```
hello-1.0.0.zip
  HelloPlugin-1.0.0/
    plugin.json         ← 宿主会找到"唯一子目录"里的清单
    HelloPlugin.dll
```

> 找不到 `plugin.json`（既不在根也不在唯一子目录）会提示「包里找不到 plugin.json」。

### 9.2 打包命令

PowerShell：

```powershell
# 在编译输出目录里打包
cd bin\Release\net10.0
Compress-Archive -Path * -DestinationPath ..\..\..\hello-1.0.0.zip -Force
Rename-Item ..\..\..\hello-1.0.0.zip hello-1.0.0.cblplugin
```

bash / Git Bash：

```bash
cd bin/Release/net10.0
zip -r ../../../hello-1.0.0.cblplugin . -x '*.pdb'
```

### 9.3 版本号怎么定

- `plugin.json` 的 `version` 只是显示用，宿主**不做**比较、不阻止降级。
- 但用户会靠它判断要不要更新，建议**语义化版本**：`1.2.3`。
- 升级 = 用同 `id` 的新包覆盖安装（插件页会问一次，确认后旧目录先备份、失败回滚）。

### 9.4 不要打包的东西

| 别打包 | 原因 |
| --- | --- |
| `ClassDailyLand.Plugin.Abstractions.dll` | 会和宿主那份冲突，插件直接加载失败（见 [1.3](#13-为什么必须-privatefalse)） |
| `*.pdb` | 体积大，除非你在排查崩溃 |
| `data/` 目录 | 那是用户数据，覆盖安装会覆盖掉用户状态 |
| 你自己的 `settings.json` / 缓存 | 同上 |
| 调试用的 `*.log` | 没必要 |

### 9.5 升级与覆盖安装

1. 用户点「安装插件包…」选新包；
2. 宿主发现同 id 已存在 → 提示「已装过 X，要覆盖吗」；
3. 确认后：旧目录改名为 `.bak-<时间戳>` → 解压新包 → 成功则删备份；失败则**回滚**旧目录；
4. `data/` 目录**保留**（所以状态、缓存不会丢）。

---

## 10. 调试手册

### 10.1 日志在哪

| 位置 | 内容 |
| --- | --- |
| 设置 → 插件 → 运行日志 | 最近的插件日志（环形缓冲，最新的在下面） |
| `settings\app.log` | 全量日志，插件行带 `[plugin:<id>]` 前缀 |
| `%LOCALAPPDATA%\ClassDailyLand\startup-crash.log` | 启动崩溃兜底 |

日志是排查的第一入口。加载失败的原因（清单校验、类型找不到、`apiVersion` 不匹配、
`Initialize` 抛异常）都会写在这里，不要靠猜。

### 10.2 改完代码怎么生效

```
改代码 → dotnet build → 设置 → 插件 → 点「重载」
```

不用重启应用。如果重载报"文件被占用"，先点「禁用」再「启用」
（旧 ALC 还没完全释放时 dll 仍被锁）。

### 10.3 附加调试器

1. 应用正常启动（插件已装好）；
2. Visual Studio → 调试 → 附加到进程 → 选 `Class Daily Land 2.exe`；
3. 在你的插件代码里下断点；
4. 触发方式：改设置、点主岛、等一秒 tick、点插件页的「重载」。

> `Initialize` 只在加载时跑一次，所以调试它要**先下断点再点「重载」**。

### 10.4 造数据

不想等到真的下节课，用「设置 → 课表与提醒 → 状态测试」：可以试算任意时间点的岛状态并预览胶囊。
灾害预警页还有「模拟一次地震预警」，可以看红条接管的效果。

### 10.5 一个常见误判

> "我改了文案，但岛上没变"

先确认三件事：

1. 回调真的返回了**不同**的字符串（相同字符串宿主不重播动画，也不改文案）；
2. 这秒你的显示位**没被更高优先级**的插件/内置内容盖掉；
3. `Priority` 是不是被别人的更高值压住了（单值位只有一个赢家）。

---

## 11. 性能预算与反模式

### 11.1 预算

| 场景 | 预算 | 说明 |
| --- | --- | --- |
| 单个 `Func<>` 回调 | **< 0.5 ms** | 每次 tick 要把九个显示位全问一遍，超了就是整条界面掉帧 |
| `Tick` 事件处理 | < 0.5 ms | 与宿主刷新同一个 tick |
| `SettingsChanged` | < 5 ms | 不频繁，但别在这里重建整个状态 |
| `Initialize` | < 200 ms | 启动时会阻塞，重活放后台线程 |
| `Shutdown` | < 200 ms | 不能等异步任务，超时会拖慢卸载 |

### 11.2 反模式对照

**❌ 在回调里发请求**

```csharp
host.RegisterIslandText(new IslandTextSpec(() =>
{
    var json = new HttpClient().GetStringAsync(url).Result;   // 卡死界面
    return Parse(json);
}));
```

**✅ 后台线程 + 字段**

```csharp
_ = Task.Run(async () => { _text = await FetchAsync(); });     // 后台
host.RegisterIslandText(new IslandTextSpec(() => _text));      // 微秒级
```

---

**❌ 每秒重建大对象**

```csharp
host.RegisterPanel(new PanelSpec("作业", 220, () =>
{
    var lines = new List<string>();
    foreach (var h in host.Homework)
        lines.Add($"{h.Subject} {h.Content} {JsonSerializer.Serialize(h)}");  // 序列化 × 每秒
    return lines;
}));
```

**✅ 缓存不变的部分**

```csharp
List<string>? _cachedLines;
int _cachedCount = -1;

host.RegisterPanel(new PanelSpec("作业", 220, () =>
{
    var items = host.Homework;
    if (items.Count == _cachedCount && _cachedLines is not null) return _cachedLines;
    _cachedCount = items.Count;
    _cachedLines = items.Select(h => $"{h.Subject} {h.Content}").ToList();
    return _cachedLines;
}));
```

---

**❌ 在回调里写文件**

```csharp
host.RegisterBoardBadge(new BoardBadgeSpec(() =>
{
    File.WriteAllText(path, "…");        // 磁盘 IO，每帧都来一次
    return new BoardBadge("OK");
}));
```

**✅ 变化时写，且用宿主的存储**

```csharp
if (stateChanged) host.WriteState("last", value);   // 只在真变化时写
```

---

**❌ 用 `DateTime.Now`**

```csharp
if (DateTime.Now.Hour >= 22) return "该睡觉了";      // 用户调过时间偏移就对不上
```

**✅ 用 `host.Now`**

```csharp
if (host.Now.Hour >= 22) return "该睡觉了";
```

### 11.3 自查表

- [ ] 所有 `Func<>` 里没有 `await`、没有 `.Result`、没有 `HttpClient`
- [ ] 所有 `Func<>` 里没有 `File.*`、没有 `Directory.*`
- [ ] 所有 `Func<>` 里没有 `lock`、没有 `Thread.Sleep`
- [ ] 大数据集合有缓存，不每秒重建
- [ ] 后台线程在 `Shutdown` 里被 `Cancel`
- [ ] 时间判断全用 `host.Now`

---

## 12. 安全与边界

### 12.1 插件能做什么

插件是**同进程**的 .NET 代码，理论上能做任何事（读写文件、发网络请求、P/Invoke）。
宿主只提供便利 API，**不做沙箱**。

### 12.2 宿主替你挡了什么

| 动作 | 防护 |
| --- | --- |
| `OpenUrl` | 只放行 `http` / `https`；其它协议（`file:`、`javascript:`、自定义协议）被拒并记日志 |
| `OpenFolder` | 只允许程序目录与插件目录内部，`..` 穿越会被规范化后拒绝 |
| 安装包解压 | 逐条校验落点防 `../` 穿越；限 2000 条目、64MB 总量（防解压炸弹） |
| 覆盖安装 | 旧目录先改名备份，失败回滚 |
| 显示位回调 | 异常一律接住，不会拖垮宿主 |
| 单个插件崩 | 只影响它自己的显示位，其它插件与内置功能照常 |

### 12.3 不要做的事

| 别做 | 原因 |
| --- | --- |
| 弹自己的 `Window` / `MessageBox` | 会脱离宿主的视觉语言（主题、圆角、Dpi）；用 `Toast` 或写进显示位 |
| 修改宿主的数据文件 | 违反原子写约定，可能把用户数据写坏 |
| 在 `Shutdown` 里 `await` | 宿主不会等，进程可能提前退出 |
| 假设自己是唯一的插件 | 单值位有竞争，多值位会并排 |
| 把坐标/尺寸写死 | 用户可能 150% 缩放，用 `host.WorkArea` 与 `host.DpiScale` |

---

## 13. 版本兼容

### 13.1 宿主的承诺

| 变更类型 | 是否提升 `apiVersion` |
| --- | --- |
| 新增显示位 / 新增 `IPluginHost` 成员 | 否（老插件照跑） |
| 新增 Spec 的可选参数（带默认值） | 否 |
| 改 Spec 字段语义 / 删成员 / 改接口签名 | **是** |

所以插件只要写 `apiVersion: 1`，在 `1.x` 的宿主上**永远能跑**。

### 13.2 插件侧的做法

```csharp
if (host.ApiVersion.Major > 1)
    host.Warn($"宿主契约版本 {host.ApiVersion} 比插件预期的新，可能有不兼容");
```

需要新能力时：

```csharp
// 别用 try/catch 硬扛，先看版本
if (host.ApiVersion >= new Version(1, 1))
{
    // 用 1.1 才有显示位
}
```

### 13.3 当前版本

| 项 | 值 |
| --- | --- |
| 契约 `ApiVersion` | `1.0` |
| 显示位 | 9 个注册点 |
| `IPluginHost` 成员 | 48 |

---

## 14. 错误速查

| 现象 | 原因 | 修法 |
| --- | --- | --- |
| 插件页显示「找不到 plugin.json」 | 目录里没有清单，或包结构不对 | 清单必须在插件目录根（或唯一子目录） |
| 「entry 文件不存在」 | `entry` 写错文件名，或 dll 没跟着打包 | 和输出目录里的实际文件名对齐，带 `.dll` |
| 「找不到类型 X」 | `type` 写错，或类不是 `public` | 写完整名（`命名空间.类名`），类加 `public` |
| 「无法实例化」 | 没有 public 无参构造函数 | 加无参构造，或去掉带参构造 |
| 「插件需要更新的宿主」 | `apiVersion` 大于宿主支持版本 | 降 `apiVersion` 或换新宿主 |
| **`Initialize` 从没被调用，日志也没有** | 插件目录里放了 `ClassDailyLand.Plugin.Abstractions.dll` | 删掉它；csproj 加 `Private=false` + `ExcludeAssets=runtime` |
| 加载成功但显示位没内容 | 回调一直返回 null/空白 | 打日志确认回调被调用；检查数据来源 |
| 显示位被别人盖掉 | 单值位竞争，别人 `Priority` 更高 | 提高自己的 `Priority`，或改注册到多值位 |
| 改完 dll 没生效 | 还在跑旧 ALC | 点「重载」；报占用就先「禁用」再「启用」 |
| 重载后日志打两遍 | 旧实例没 `Shutdown`，事件没退订 | `Shutdown` 里退订事件、`Cancel` 后台线程 |
| 下拉面板不显示 | 总宽超过屏幕 80%，插件面板被丢弃 | 把 `Width` 调小（200~260 更稳） |
| 副岛不显示 | 回调 null，或设置里 `show_sub_island` 关了 | 检查返回值与设置 |
| 主岛徽标压到文字 | 同时注册了 `IslandExtra` 但 `Width` 太小 | 调大 `IslandExtra.Width` |
| 主岛文字被截断/跑马灯 | 文案太长 | 压到 12 个汉字以内 |
| 时间判断错位 | 用了 `DateTime.Now` | 改用 `host.Now` |
| 界面每隔一秒抖一下 | 回调里做了重活（IO/网络/大对象） | 见 [11.2](#112-反模式对照) |
| 作业列表滚动条每秒跳回顶部 | 主窗区块内容每秒都在变（比如带秒数） | 内容不变就别变字符串 |
| 卸载插件后目录还在 | 卸载时文件被占用（日志/句柄） | 关掉应用再删，或先「禁用」再「卸载」 |
| `Toast` 没弹出来 | 系统通知被关 | 检查 Windows 通知设置（宿主用托盘气泡） |
| 插件列表里有一条红色的失败项 | 清单校验/加载/`Initialize` 抛异常 | 看「运行日志」里的具体原因 |

---

## 15. 提交前检查清单

**清单**

- [ ] `id` 只用小写字母数字与 `.` `_` `-`，与目录名一致
- [ ] `entry` 是文件名，与实际 dll 一致
- [ ] `type` 是 `命名空间.类名`，类是 `public` 且有无参构造
- [ ] `apiVersion: 1`（数字不是字符串）
- [ ] JSON 无注释、无 BOM

**工程**

- [ ] `TargetFramework` 是 `net10.0`（不带 `-windows`）
- [ ] 契约引用是 `Private=false` + `ExcludeAssets=runtime`
- [ ] 输出目录里**没有** `ClassDailyLand.Plugin.Abstractions.dll`

**代码**

- [ ] 所有回调里没有 IO / 网络 / 锁 / 大对象
- [ ] 后台线程在 `Shutdown` 里被取消
- [ ] 事件在 `Shutdown` 里退订
- [ ] 时间判断用 `host.Now`
- [ ] 失败路径有降级（返回 null 而不是抛异常）
- [ ] 文案长度在预算内（岛 ≤ 12 字、徽标 ≤ 4 字）

**体验**

- [ ] 深色/浅色两套主题下都好看（只用 `BadgeTone`，不自己传颜色）
- [ ] 150% DPI 下不挤压（用 `host.WorkArea` / `host.DpiScale` 而不是写死像素）
- [ ] 卸载后没有残留副作用（后台线程、临时文件）

---

## 16. FAQ

**Q：插件能改宿主的设置吗？**
不能。`GetSetting*` 是只读的，宿主没暴露写接口 —— 设置是用户的，插件只能建议（用 `Toast` 提示去设置页）。

**Q：插件能往岛上放图片吗？**
不能。显示位只接受文本、徽标（文本 + 色调）、进度值。图片请转成字符（`♪`、`☀`、`☔`）。

**Q：为什么我的面板宽 400，但显示时被挤掉了？**
所有面板加上「今日课程」的总宽不能超过屏幕 80%，超了会**从右往左**丢插件面板。把 `Width` 压到 200~260。

**Q：`Priority` 什么时候有用？**
只在**单值位**（岛文案 / 加长段 / 徽标 / 副岛 / 主窗徽标）上。多值位不竞争，只看 `Order`。

**Q：能同时注册多个 `RegisterPanel` 吗？**
可以，会并排显示（但总宽限制同上）。岛文案、徽标这些单值位注册多次只有赢家生效。

**Q：怎么让插件只在有内容时出现？**
回调返回 `null` 就行。这是**推荐**写法：没数据的秒数不要占位置。

**Q：`Invalidate()` 会不会太频繁伤性能？**
它只是让宿主提前重算一次显示位。别在每秒 tick 里调它（那时候本来就会算），在"数据到了"的时候调就好。

**Q：插件崩了会把应用带崩吗？**
回调里的异常不会（宿主接住并记日志）。但如果你在自己起的后台线程里抛未处理异常，.NET 默认会杀进程 —— 所以后台线程里一定要 `try/catch`。

**Q：能引用第三方 NuGet 包吗？**
可以，但包必须随插件一起放在插件目录（`Private=true` 对**这些包**）。只有契约 DLL 必须 `Private=false`。

**Q：能调试 `.cblplugin` 安装流程吗？**
可以：装完看插件页的日志。安装失败会写明原因（找不到清单 / 清单校验失败 / 解码失败）。

**Q：怎么做一个"只在特定课出现"的插件？**
在回调里判断 `host.Snapshot.Course` 或 `host.CurrentCourse`，不匹配就返回 `null`：

```csharp
host.RegisterIslandText(new IslandTextSpec(() =>
    host.Snapshot.Course == "数学" ? "记得带三角板" : null));
```

---

## 附：可直接抄的样板

`samples/HelloPlugin` 是一个能编译、能跑、把显示位用了一大半的示例：
主岛文案 + 徽标、副岛、下拉面板、班级日常区块 + 徽标 + 右键菜单 + 托盘菜单，
外加私有存储与设置变更事件。

```bash
dotnet build samples/HelloPlugin/HelloPlugin.csproj
# 设置 → 插件 → 从文件夹安装… → samples/HelloPlugin/bin/Debug/net10.0
```
