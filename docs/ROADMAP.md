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

## 阶段 4.6 —— 作业提交时间（1.7.30 起，1.7.117 定型为「提交时段」）

- 数据结构：`homework.json` 每条新增 `due` / `due_end` / `due_name` / `due_period` /
  `due_kind` / `due_label` / `due_fired`，老文件缺键读出即为空串，行为与之前完全一致
  （`Core/Models/HomeworkModels.cs`）
- 「编辑作业」的「提交日期」文本框 → **下拉框**：今天**全部**节次按顺序列出（含没排课名的
  晚自习），选中即用**该节下课**当提交时间、**下节课上课**当截止；重名去重三条规则：
  ① 课名唯一「第3节 数学 · 10:40 下课」② 课名重复「10:40 数学 · 第3节 下课」
  ③ 没排课名且节次重名「21:30 晚自习 · 第9节 下课」（`HomeworkDue.PeriodOptions`）
- 另外三项：「自定义文本…」（自由填一个提交时间）、「补交」、「不交」；
  补交/自定义先弹后果警告，下拉框右侧实时显示「这条作业会排在哪」
- **提交时段** `[due, due_end)`（1.7.117 定型）：时段 = 那节课下课 → 下节课上课（最后一节到 23:59）
  - 时段内 → 整个**科目板块**顶到作业区最前，条目行尾标「10:40 自习(1)」
  - 没设 / 还没到 → 正常（homework.json 原顺序）
  - 补交 → 正常之后、已过之前
  - 时段已过（下节课都上课了）→ 沉到最底下（「已过提交时间」）
  - **排位单位是整个科目板块**：同科目只要有一条在时段内就整块置顶，全过完一起沉底；
    纯计算、不写盘，`homework.json` 顺序一点没动（`Core/HomeworkDue.cs`）
- 到点提醒（`Core/HomeworkDueWatcher.cs` + `AppController.CheckHomeworkDue`）：每秒扫一次，
  进入时段那一刻写 `due_fired` 并弹一条系统通知（`homework_notify_enabled` 可关）；
  程序打盹超过 2 分钟宽容窗就当错过、不再补弹；凌晨跨天自动沉底
- 设置页「作业」页是**卡片编辑器**：一科一色、卡片可拖动排序（鼠标 + 触屏）、
  每条自带提交时间下拉框；与独立「编辑作业」窗口共用 `HomeworkDueOptions` 一份实现
- 历史快照恢复 / CSV·JSON 导入导出都带上提交时间字段
- 测试 +40 余例（时段排位 / 板块单位 / 到点触发 / 宽容窗 / 跨天 / 老数据兼容 / 时间解析）
- 后续补强（同日）：
  - **同科目多条分不清**：作业区每一行行尾补淡色小字，一眼分清哪条是哪条（`HomeworkDue.BoardHint`）
  - 卡片编辑器工具栏加「保存」按钮（点一下立刻写盘 + Toast），旁边标「改动会自动保存」
  - 触屏拖不动卡片：ScrollViewer 的 PanningMode 把触摸当滚动手势吃掉了 ——
    卡片标题接管 `PreviewTouchDown`/`Move`/`Up` + `TouchDevice.Capture`，与鼠标共用一套拖动逻辑
  - 提示框统一入场动画 `Views/UiMotion.cs`：**从窗口底边加速升上来**（位移 = 内容自身高度，
    `AccelerationRatio 0.65` 加速 / `DecelerationRatio 0.35` 收住）+ 淡入；
    覆盖 警告 / 自定义时间 / 发现新版本 / 出错啦 / 设置窗内确认浮层；时长跟着设置里的「动画时长」走
  - 触屏长按的菜单重做：**鼠标右键仍是老样子的 ContextMenu**；触屏长按改为贴在「班级日常」
    **左边**向外张开的侧边菜单（Popup `Placement=Left`），每一项从板子边缘**依次伸出来**
    （32ms 间隔 + 240ms 加速滑入 + 淡入，ClipToBounds 挡住未进场的部分）；
    菜单项抽出 `GetMenuEntries()` 与右键菜单共用，点外面照旧由全局鼠标钩子关掉

## 阶段 4.7 —— 实时天气与天气动画（已实现，待实机验证）

- **新增内置集成 `Integrations\Weather\`**（不是插件，与媒体、灾害预警同级，同一个 1s tick 驱动）
  - `WeatherLocation`：IP 定位三源按序试（太平洋电脑网 → ip-api → ipapi.co），结果缓存 24h；
    三个源全挂就退回手选省市区查内嵌 `Resources\cities.json`
  - `WeatherClient`：Open-Meteo `current` 实况（免 key）+ 天气模块自己的 HTTP（10s 超时 / GBK 兜底）
  - `WeatherService`：每秒 tick、按 `weather_refresh_sec` 触发请求，`weather-cache.json` 重启先显示上次；
    失败退避重试 + in-flight 30s 卡死兜底
- **天气动画 `WeatherArt.xaml(.cs)`**：11 种天气（晴 / 多云 / 阴 / 雾 / 毛毛雨 / 雨 / 阵雨 /
  冻雨 / 雪 / 雷阵雨 / 冰雹），晴天分昼夜（太阳 / 月牙）。WPF Path + Storyboard，
  颜色走新增的 `WeatherCloudBrush` / `WeatherSnowBrush`（浅深各一套令牌）；
  隐藏或移出可视树**必须停表**（副岛是常驻 topmost 窗，最容易踩）。
  一个控件四处复用：副岛收起态、副岛展开态、主窗区块、设置页预览
- **副岛**：收起态改放天气动画（圆环 / 进度点让位）；天气内容一进来自动展开，
  `weather_idle_collapse_sec`（默认 300s）无操作收成 40×40 的圆；普通内容仍走 `sub_island_auto_collapse_sec`
- **主窗（作业窗口）**：`BoardSection.Art` —— 插件契约 **v3 纯新增**，保留 2 参主构造 +
  3 参重载 + `init` 属性，老插件二进制不受影响；`HostApiVersion` 2 → 3，
  Abstractions 版本 1.2.0 → 1.3.0；`MainBoardWindow` 的内容签名把 `Art` 也算进去（否则昼夜切换不刷新）
- **位移规则**：`weather_show_on = sub` 且出现**气象预警**时，预警占副岛、天气自动挤到
  主窗插件区块（置顶第一块），预警过去再回来。
  副岛优先级：**地震 > 气象预警 > 插件 > 媒体 > 实时天气 > 内置课程**
- **自动定位 `auto_locate`（默认开）**：实时天气 / 气象预警过滤 / 地震倒计时**三者共用一个开关**；
  IP 给的省名必须能对上城市表才采纳（ipapi.co 回英文，直接拿去过滤中央气象台会一条都匹配不上）；
  状态卡片一直标出坐标来源（IP 定位 / 手选省市查表 / 手动经纬度），关掉则手填经纬度 > 省市区查表
- **设置**：新增「设置 → 天气」页（导航用 `\uE706`），含**全天气动画预览网格**
  （11 种同时循环播、晴天拆日 / 夜两格）；「灾害预警」页位置组加 `auto_locate` 开关，
  开着手填经纬度整行禁用
- 测试 +15 例（WMO 全码表映射 / 昼夜 / 风向风级 / Open-Meteo 解析与缺字段回退 /
  定位回退链 / 动画种类规整 / `BoardSection` 2 参构造兼容 / `BoardArt` 默认值）
- ⚠️ **待实机验证**：IP 三源与 Open-Meteo 的**国内可达性**（离线无法确认；已做成多源 + 回退 + 每源单独记日志）

## 阶段 4.8 —— 系统通知与一批打磨（1.7.117，2026-11-07）

- **系统通知 `Tray\ToastService`**：所有提醒从托盘气泡升级成 Windows Toast ——
  进**通知中心**（横幅错过了能回去补看）、带应用名与图标（发送前在
  `HKCU\Software\Classes\AppUserModelId\<AUMID>` 登记 DisplayName，未打包桌面程序必须先注册，
  否则 `CreateToastNotifier` 直接抛异常）；灾害类才传 `urgent` 标成**重要**（穿透专注助手）；
  环境不支持时自动退回老的托盘气泡。
  Windows 的限制写进注释：**没有任何受支持的开关能让通知"关不掉"**，
  真正要命的警报仍以主岛置顶红条为"跑不掉"的通道
- **作业提交时段定型**（详见 4.6）：`[due, due_end)` 窗口 + 科目板块为单位的四段排位；
  新增 `homework_notify_enabled`（到点通知可关）；`due_end` / `due_name` / `due_period` 入库
- **课表秒级时间**：`TimeUtil.TryParseSeconds/FormatSeconds`（`HH:mm:ss` 全支持，状态测试用得上）；
  胶囊文案最后一分钟显示秒 —— `下节 语文 · 42秒后` / `本节：语文 还剩 42秒`
- **灾害预警加固**（`DisasterService`）：
  - 位置：与天气共用 `auto_locate`（IP 三源），状态卡片一直标出坐标来源
    （IP 定位 / 手选省市查表 / 手动经纬度）—— IP 是市级精度，算地震倒计时会偏，必须可见
  - ICL 详情补拉只做一次（老事件常返回空 `counties`，不记下来每轮都重发）
  - in-flight 30 秒卡死兜底（请求本身 10 秒必然超时，30 秒还挂着只可能是异常从 Task 逃出去）
- 测试 +57 例（天气 WMO 映射/定位回退/Open-Meteo 解析、作业提交时段、秒级时间、灾害）
- 文档补全：README 新增「作业与提交时间」「系统通知」两节与 `LICENSE.md`（MIT 独立成档）；
  ROADMAP 4.6 改写为提交时段模型；plugins.md 的 `Toast` 语义同步（托盘气泡 → 系统通知）

## 阶段 5 —— 可选增强（未做）
- 插件市场进阶：`icons/` 图标、评分与举报入口、包签名、依赖声明
- Mica 主题（DWM API）
- Banner 态紧急警报条（枚举与渲染骨架已预留）
- 上传工具链：把 `entries` 校验搬到 CI，避免人工忘跑 `verify`
