# 路线图（分阶段交付）

## 阶段 1 —— MVP 骨架（已完成）
- 单实例锁（Mutex + 命名事件唤醒旧实例）
- 主窗（贴右、自动滚动作业、右键菜单）
- 灵动岛：Hidden/Mini/Compact/Countdown/Alert + 蓝色进度条（流光 + 彗星光晕）
  + 文字跑马灯（32px/s、回起点前停 0.9s、首尾 40px）+ 文案 140ms 竖向切换
- 全屏 / Office（WPS/Word/Excel/PPT）前台检测
- ScheduleEngine（假期 / 调休 / 周末 / 偏移 / 提前 / 跨午夜 / 临时调课）
- 数据层：AtomicJson 原子写 + 六个 JSON 文件 + 逐键类型校验
- 设置中心 6 页（通用 / 外观 / 灵动岛 / 课表与提醒 / 作业 / 关于），改即存 + 广播
- 托盘（WinForms NotifyIcon）
- 单元测试 27 个，全部通过

## 阶段 1.5 —— 设置中心 XAML 化（已完成，对位 ui/ 的 WebView 设计）
- 设计令牌搬到 XAML：`Themes\Tokens.Base/Light/Dark.xaml`（浅深两套，变量名沿用 WinUI 3 资源键，
  rgba 已换算成 #AARRGGBB）；`ThemeService` 换字典实现整窗换色，并同步自绘界面的 `Palette`
- 32 个描边图标由 `ui/lib/icons.js` 的 SVG 逐条转成 `Geometry`（实心小圆点靠 1.5 描边填实）
- 控件库：Button（普通/强调/微妙/危险/标题栏 46×34，关闭悬停 #c42b1c）、FluentToggle 40×20、
  Slider（4px 轨道 + 20px 外圆 + 12px 内点，悬停长大）、NumberBox（▲▼ + 单位后缀 + 夹取）、
  ComboBox（浮出 10px 圆角 + 勾选位）、SelectorBar、TextBox（焦点内下划线，不跳 1px）、
  细滚动条 12px、SettingRow（62 高 / 18,14 内边距 / 行间分隔线）、LinkRow（36px 彩色磁贴）、
  Card（12px 圆角 + 顶部内高光 + 静置阴影）、SettingGroup、InfoBar、Badge
- 外壳：48px 自绘标题栏（WindowChrome 交给系统拖动与缩放）+ 264px 侧栏（品牌 / 搜索过滤 / 药丸导航）
  + 内容区 max 980 + 页面淡入（350ms OutQuint + 上移 10px）
- 8 个页面全部照 web 版字段与文案重做（新增 高级 与 插件 两页）；补 3 个设置键
  `anim_fade_panel` / `anim_fade_text` / `settings_mica`
- 6 个二级浮层改为**窗口内浮层**（不再各开一个窗）：课表管理 / 周末作息 / 假期与调休 /
  值日生名单 / 临时调课 / 状态测试；作业编辑器一份实现、两种用法（内嵌即时生效 / 独立带取消保存）
- 键名沿用本项目既有 snake_case，差异集中在 `SettingsViewModel` 顶部注释那张映射表里
- 删除旧的代码绘制版：`Windows\SettingsWindow.cs(.Pages.cs)`、`Windows\Editors\*`、`Controls\BasicControls.cs`
- 单元测试 32 个（新增设置中心模型不变量：PageKeys 只引用已知键、外观页键全覆盖、
  ResetAll 单次广播、未知键跨重启保留）
- 依赖：只加了 `CommunityToolkit.Mvvm`（INPC / 命令样板，不含任何控件）

## 阶段 2 —— 面板与编辑器（已完成）
- PanelWindow（`Windows\PanelWindow.cs`）：点击主岛展开「今日课程」面板（高度 0→目标 +
  上移 6px，OutCubic 320ms；多面板 70ms 交错），3 秒无操作自动收起，再点主岛立即收起；
  内容超高时滚轮滚动 + 底部渐变遮罩；视觉 = 原版 #1c1c1e / 圆角 8 / 13px #e6e6e6
- 主岛：点击 Compact/Countdown 均可展开面板；面板文案带时间偏移换算与当前节「▸」标记
- 作业编辑器（行内 学科/日期/内容 + 上移/下移/删除，导出 CSV（UTF-8 BOM）/JSON、导入 JSON）
  —— 阶段 1.5 起改为一份实现、内嵌与独立浮层两种用法
- 值日生名单编辑器（名单增删改序 + 应到/实到，保存即轮换生效）→ 现为 `DutyDialog` 浮层
- 课表管理：多课表（新建 / 复制 / 重命名 / 启用 / 删除）+ 节次时间表 + 课程格
  —— 课程格只放周一~周五（周末课程存在 `weekend`，走「周末作息」，与 web 版同一取舍）
- 假期与调休（区间列表 + 补课日按周几）；周末作息（上午分界点 + 下午节次/课程）→ 现为浮层
- 临时调课（选日期 + 逐节覆盖，预填原课表，保存/清除即生效）→ 现为浮层
- 主窗右键菜单按原版补全：上一位/下一位值日生、出勤人数、编辑作业、课表管理、
  临时调课、假期与调休、周末作息、随机点名（F1 = 退出）
- 随机点名窗口（大字号 + 10 人冷却不重复 + 可选 JSON 名单文件）
- 设置滑块 200ms 防抖落盘（拖动不再造成几十次原子写）
- 主窗版式对齐原版：无卡片纯文字列（标题 fs+4、今日值日生、应到/实到、作业按学科分组编号）、
  贴屏幕右缘 + 顶部对齐、触摸/拖动滚动、手动滚动后自动滚动让位 4s

## 阶段 3 —— 插件系统（已完成 2026-10-24）
- 契约工程 `ClassDailyLand2.Plugin.Abstractions`（独立、无 WPF 依赖）：
  `IPlugin` / `IPluginHost`（**50 个成员**）/ 9 个显示位 Spec
- 宿主：每插件一个**可卸载** `AssemblyLoadContext`（契约走默认上下文），
  `plugins\<id>\plugin.json` + 反射实例化；禁用/重载**不用重启应用**
- 显示位：主岛（文案补充 / 加长段 / 右侧徽标）、副岛、下拉面板（多值并排）、
  班级日常主窗（区块 / 标题徽标 / 右键菜单项）、托盘菜单项
- 竞争规则：单值位 Priority 大者胜、平级先注册者胜，**回调返回 null 会回退到下一个候选**；
  多值位全部显示，菜单按 Order 升序
- 安装：`.cblplugin` / `.zip` / 文件夹；逐条校验解压落点（防穿越）、限条目数与 64MB、
  覆盖安装先备份失败可回滚；卸载删目录
- 设置中心「插件」页：已安装列表（名称/版本/作者/契约版本/注册数）、启用开关、
  重载、卸载、安装插件包、从文件夹安装、重新扫描、运行日志（同时写 app.log）
- `docs/plugins.md`：清单字段、50 个 API、线程与性能纪律、常见错误对照表
- 示例插件 `samples/HelloPlugin`（可编译，四个显示位都用了一遍）
- 测试 +35 例（清单校验 / 目录扫描 / 启停状态 / 安装安全 / 显示位竞争与异常隔离）

## 阶段 4 —— 插件市场（已完成 2026-11-01）
- 索引与安装包放仓库 `plugins` 分支，零服务器：主源 Gitee raw + 备源 GitHub raw，
  包地址用相对 `path` 拼，主备共用一条记录（详见 `docs/market.md`）
- `tools/market.mjs`：`pack`（打包并剔除 pdb / 源码 / data、校验 dll 平铺与入口）
  / `add`（算 sha256、写 entries）/ `build`（聚合 index.json）/ `verify`
- `Core/Market/`：索引解析（单条坏数据只丢这一条）、双源取数（破 CDN + 每源重试）、
  本地缓存（12 小时）、兼容性判定（契约 / 主程序 / SDK 三道门槛）、下载校验（sha256 + 包内清单）
- 「设置 → 插件」新增「插件市场」段：搜索、状态徽标、安装 / 更新 / 降级、断网显示缓存
- 首个真实条目 `lch.gaokao-countdown`（下拉面板里的高考倒计时），源码同分支 `sources/`
- 测试 +21 例（索引容错 / 状态判定 / 包校验 / 地址拼接）
- 实机反馈后补的四处修复：
  - 覆盖安装留下的 `<id>.bak-*` 被当成插件列出（"加载失败：缺少 plugin.json"）→
    `PluginCatalog.IsLeftover` 识别并跳过，启动时 `SweepLeftovers` 清掉
  - 卸载/升级时旧 dll 句柄未释放导致 `Directory.Delete` 失败 → 卸载后强制回收一次
    （`GC.Collect` + `WaitForPendingFinalizers`），删除带退避重试；仍删不掉则改名 `_trash-*`
     交给下次启动清理（Windows 下"含被占用文件的目录"连 rename 都会被拒，所以顺序很关键）
  - 市场缓存新鲜度 12 小时 → 10 分钟：作者发版后用户最多十分钟就能看到
  - 「已安装」行显示 `可更新 vX` 徽标 + 行内「更新」按钮，顶部另有提示条
- 测试 +9 例（残留目录识别与清理 / 占用时的卸载行为）

## 阶段 4.5 —— 插件自己的设置页（契约 v2，已完成 2026-11-01）
- 契约新增 `SettingsPageSpec` / `SettingField` / `SettingOption` / `SettingFieldKind`
  与宿主成员 `RegisterSettings` / `SettingChanged`（48 → 50 个成员），`HostApiVersion` 1 → 2
  （纯新增，apiVersion=1 的老插件照旧加载）
- 声明式：插件只描述设置项，宿主按 Kind 渲染 TextBox / NumberBox / FluentToggle / ComboBox，
  插件不碰任何 WPF 类型；主题、触屏放大、键盘操作与设置中心其余页面一致
- 值存在插件自己的 `data/state.json`；保存时逐项写盘并触发 `SettingChanged`，插件立即生效
- 「已安装」列表：声明过设置页的插件才出现「设置」按钮，并把插件算的一句话（如
  「2029 届 · 还有 949 天」）显示在元信息行
- 示例插件 `lch.gaokao-countdown` 1.1.x：届别（哪一年高考）可自选 + 「也在主岛显示」开关
- 测试 +6 例（设置页注册表：声明/覆盖/随插件清理/不计入显示位数）

## 阶段 5 —— 可选增强（未做）
- 插件市场进阶：`icons/` 图标、评分与举报入口、包签名、依赖声明
- Mica 主题（DWM API）
- Banner 态紧急警报条（枚举与渲染骨架已预留）
- 上传工具链：把 `entries` 校验搬到 CI，避免人工忘跑 `verify`
