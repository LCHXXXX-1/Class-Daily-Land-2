# 插件市场

插件市场不依赖任何服务器：**索引与安装包都放在仓库的 `plugins` 分支上**，客户端读 raw 地址。
（上架流程与字段规则见该分支的 `docs/publish.md`。）

## 三个角色

| 角色 | 在哪 | 谁维护 |
| --- | --- | --- |
| `entries/<id>.json` | `plugins` 分支 | 作者提交（一插件一文件，PR 友好） |
| `index.json` | `plugins` 分支 | `tools/market.mjs build` 生成，禁止手改 |
| `packages/<id>/*.cblplugin` | `plugins` 分支 | `tools/market.mjs pack` 产出 |

客户端**只请求 `index.json`**（一个文件装全部插件），包地址由索引里的相对 `path` 拼出来，
主源 Gitee / 备源 GitHub 共用同一条记录。

## 客户端结构

```
Core/Market/                       纯逻辑，无 WPF，可单测
  MarketModels.cs                  索引 DTO + 逐条校验（单条坏数据只丢这一条）
  MarketClient.cs                  双源拉取、破 CDN 缓存、每源一次重试
  MarketCache.cs                   settings/market-cache.json，12 小时内直接用缓存
  MarketPolicy.cs                  兼容性判定与「可更新/未安装/不兼容」状态
  MarketDownloader.cs              下载 + sha256 + 包内清单校验（三道）
App/Market/MarketService.cs        编排：拉索引 → 下载 → 交给 PluginManager.Install
Views/Pages/PluginsPage.xaml(.cs)  插件页「已安装 / 插件市场」两段
```

## 为什么这样切

- **安装逻辑只留一份**：市场下载到 `%TEMP%` 校验通过后，交给现有的
  `PluginInstaller.Install(path, overwrite)`，覆盖备份与回滚不重复实现。
- **三道校验**：索引 sha256/size → 包内 `plugin.json` 的 id/version 必须等于索引条目 →
  包内必须有 entry dll 且不带契约程序集。缺任何一道，「市场」就只是「帮你下了个 zip」。
- **失败也要有内容**：断网时显示上次缓存并标注时间；两条源都失败时把**每个源各自的错因**
  带回来（跟更新检查同款处理）。

## 版本与兼容

索引条目带 `api_version` / `min_app_version` / `target_sdk_version` 三个门槛，
不满足时列表里灰显并给出具体原因，而不是等装完加载失败才报错。
本机版本与市场版本比较得出「可更新 / 已最新 / 会降级」；降级要二次确认。

## 验证

- 单测：`tests/ClassDailyLand2.Core.Tests/MarketTests.cs`（索引解析容错、状态判定、包校验）
- 端到端：改代码后跑一遍「拉索引 → 下载 → 校验 → 解压安装」，最省事的做法是
  在 `%TEMP%` 建个控制台工程引用主工程，直接调 `MarketClient` / `MarketDownloader` / `PluginInstaller`。
