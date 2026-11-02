# 插件市场上架指南

本分支（`plugins`）既是插件开发文档的所在地，也是**插件市场的真源**。市场不依赖任何服务器：
客户端直接读这一分支上的 `index.json` 与包文件。

```
plugins/
  README.md                          插件开发与 API 规范（原有文档）
  index.json                         ★ 工具生成，禁止手改
  schema/index.schema.json           index.json 的格式定义
  entries/<id>.json                  一个插件一条，上架时只改这里
  已完成插件/<id>/<id>-<ver>.cblplugin 安装包（整包，含入口 dll）
  icons/<id>.png                     可选，列表图标（缺省用名称首字色块）
```

## 一、上架 / 发新版本（三分钟）

在源码仓库（`master` 分支）根目录执行：

```bash
# 1) 编译插件（Release）
dotnet build samples/GaokaoCountdown/GaokaoCountdown.csproj -c Release

# 2) 打包：自动剔除 pdb/源码/data/，并检查 dll 是否平铺、是否漏了入口程序集
node tools/market.mjs pack "samples/GaokaoCountdown/bin/Release/net10.0-windows10.0.19041.0"

# 3) 写入 entries（sha256 与体积由工具算，别手写）
node tools/market.mjs add "已完成插件/<id>/<id>-<ver>.cblplugin" \
  --id <id> --name "显示名" --summary "一句话（≤60 字）" \
  --category reminder --trust official --tags "标签1,标签2" \
  --repo "https://github.com/LCHXXXX-1/Class-Daily-Land-2/tree/master/samples/<工程>" \
  --license MIT --changelog "第一条|第二条"

# 4) 生成索引（本地先校验一遍）
node tools/market.mjs build

# 5) 把 `market/` 的内容提交到本分支，两个远端都要推
git checkout plugins          # 或在一个 plugins 分支的工作树里
cp -r ../Class\ Daily\ Land\ 2/market/* .
git add -A && git commit -m "market: <id> v<ver>"
git push origin plugins        # Gitee
git push github plugins        # GitHub（备用源，必须同步）
```

> 两个远端必须同时推：客户端主源读 Gitee，备用源读 GitHub。只推一边，另一边的用户
> 会拿到旧索引或 404。

## 二、上架校验清单（CI 与 `market.mjs verify` 都会跑）

- [ ] `plugin.json` 在包根，`entry` 指向的 dll 必须在包内
- [ ] **所有 dll 平铺在包根**（宿主只在插件目录一级找依赖，`runtimes/` 里的加载不到）
- [ ] 包内**不含** `ClassDailyLand2.Plugin.Abstractions.dll`（契约由宿主提供）
- [ ] 包内不含 `*.pdb`、`data/`
- [ ] `entry` 只能写文件名，不能带路径；`id` 匹配 `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`
- [ ] 包内 `plugin.json` 的 `id`/`version` 与 `entries/<id>.json` 完全一致
- [ ] `api_version` ≤ 2（v2 = 用了「插件设置页」RegisterSettings；不用就写 1）；`min_app_version` / `target_sdk_version` 能解析成版本号
- [ ] 插件工程 `TargetFramework` 是 `net10.0-windows*`（契约引了 Windows 专用 API）

## 三、规则

| 事项 | 规则 |
| --- | --- |
| 版本 | 严格 semver，**只升不降**。要回退就发更高的补丁号，降级会被客户端拦 |
| 一个 id 一条 | 索引只列最新版本，历史包可以留在 `已完成插件/` 但不进 `index.json` |
| 下架 | 在 entry 里加 `"revoked": true` 并重新 `build`；**不要直接删条目**（已装用户需要看到原因） |
| 单包体积 | 建议 ≤ 5 MB。超了改走 Releases，并把 entry 的 `package` 换成绝对 URL |
| 改名 | 不要复用别人的 id；改 id 等于换插件，老用户的启用状态与数据不会跟过去 |

## 四、客户端怎么读

| 用途 | 地址 |
| --- | --- |
| 索引（主） | `https://gitee.com/lchxxxx/class-daily-land-2/raw/plugins/index.json` |
| 索引（备） | `https://raw.githubusercontent.com/LCHXXXX-1/Class-Daily-Land-2/plugins/index.json` |
| 包（主） | `https://gitee.com/lchxxxx/class-daily-land-2/raw/plugins/` + `path` |
| 包（备） | `https://raw.githubusercontent.com/LCHXXXX-1/Class-Daily-Land-2/plugins/` + `path` |

`path` 是相对路径，所以主备源共用同一条记录；下载失败时客户端自动换另一个源重试一次。
两个源都要加 `?t=<unix>` 破 CDN 缓存，否则发布后几分钟内可能仍然拿到旧文件。
