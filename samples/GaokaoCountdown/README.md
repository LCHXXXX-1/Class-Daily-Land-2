# 高考倒计时（lch.gaokao-countdown）

插件市场的官方示例插件：在**灵动岛下拉面板**里显示距离高考还有多少天，届别可自己在设置里选。

它同时是「打包 → 上架 → 下载 → 校验 → 安装 → 出内容 → 改设置」这条链路的验收样本。

## 显示效果

点主岛展开下拉面板，「今日课程」旁边会多出一块（以 2029 届、2026-11-01 为例）：

```
2029 届高考
还有 948 天
约 22752 小时

11月1日 · 周日
约 135 周 3 天
```

打开「也在主岛显示」后，灵动岛文案位会多一句 `高考 948 天`。

## 设置项（插件自己向宿主声明）

插件在 `Initialize` 里调用 `host.RegisterSettings(...)` 声明一页设置，
宿主就会在「设置 → 插件 → 已安装」这个插件那一行亮出**设置**按钮：

| 设置项 | 类型 | 说明 |
| --- | --- | --- |
| 届别 | 下拉 | 2029 届 = 2029 年 6 月 7 日高考；默认「下一个 6 月 7 日」所在年份 |
| 也在主岛显示 | 开关 | 开启后灵动岛文案位多一句「高考 N 天」 |

值存在本插件自己的 `plugins/<id>/data/state.json`（键：`grade`、`show_on_island`），
宿主保存后会触发 `SettingChanged`，插件立刻按新值重算，不用重载。

## 自己编译

需要 .NET 10 SDK。**目标框架必须带 windows**（契约工程引了 Windows 专用 API）：

```bash
dotnet build GaokaoCountdown.csproj -c Release
```

产物目录里应当只有：`plugin.json` + `GaokaoCountdown.dll`（+ deps.json）。
如果看到 `ClassDailyLand2.Plugin.Abstractions.dll`，说明 `<Private>false</Private>`
没生效 —— 那份一定不能随包分发。

## 打包与上架

```bash
node tools/market.mjs pack "<输出目录>"
node tools/market.mjs add "market/packages/lch.gaokao-countdown/lch.gaokao-countdown-<版本>.cblplugin" \
  --id lch.gaokao-countdown --summary "在灵动岛下拉面板显示距离高考还有多少天" --category reminder
node tools/market.mjs build
```

完整规则见本分支 `docs/publish.md`。
