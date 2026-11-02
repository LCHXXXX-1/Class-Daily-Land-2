# 插件市场分支（plugins）

这个分支**不是源码**，它就是插件市场的仓库本体：客户端只读这里的 `index.json`。

主源是 Gitee（`raw/plugins/`），备源是 GitHub（`raw.githubusercontent.com/.../plugins/`）。
App 里的「设置 → 插件 → 插件市场」就是从这里拉列表和安装包。

## 怎么投一个插件

1. 把插件打成一个 zip；
2. 把 zip 放进 **`上传的zip/`**；
3. push 到 `plugins` 分支 —— 就完了。

推送后 GitHub Actions 会自动：识别形态 → 编译（如果是源码）→ 打包成 `.cblplugin` → 算 sha256 →
重建 `index.json` → 同时推回 GitHub 与 Gitee。几十秒后 App 里就能看到。

> 处理完的 zip 会被**自动从 `上传的zip/` 删掉**（内容已经进了 `已完成插件/`），
> 不需要你手动清理。同一份文件再推一次会被指纹识别并跳过，不会重复编译。

### zip 里放什么（三种都收）

| 形态 | zip 内容 | 适用 |
|---|---|---|
| **源码** | `.csproj` + `.cs` + `plugin.json`（同一目录） | 推荐，作者不用装 .NET SDK |
| **预编译目录** | `plugin.json` + 平铺的 `.dll` | 本地已编好、不想交源码 |
| **成品包** | 直接一个 `.cblplugin` | 从别处拿到的包，只做登记上架 |

源码型会按 csproj 里的 `..\..\ClassDailyLand2.Plugin.Abstractions\` 自动补上契约工程，
所以**不要**把契约 dll 打进 zip，也不要改那条 `ProjectReference`。

### 元数据：`market.json`（可选但强烈建议）

`plugin.json` 只描述插件本身；列表页要的 `summary / category / tags / changelog` 等放在同目录的
`market.json` 里，CI 打包时会读它，然后把它从产物里剔除：

```json
{
  "author": "你的名字",
  "category": "reminder",
  "trust": "community",
  "license": "MIT",
  "target_sdk_version": "1.2.0",
  "min_app_version": "1.2",
  "tags": ["高考", "倒计时"],
  "changelog": ["第一条改动", "第二条改动"]
}
```

`category` 可选：`timetable / weather / reminder / tools / media / theme / data / other`。
`trust` 填 `official` 只有在 `OFFICIAL_ACTORS` 变量里的人提交才生效，否则自动降级为 `community`。

## 目录

```
上传的zip/                 收件箱：把 zip 丢这里（只认 *.zip）
已完成插件/<id>/           编译产物 .cblplugin，index.json 的 path 就指向这里
entries/<id>.json          一插件一条的上架记录（版本、哈希在 build 时算）
sources/<id>/              源码型投稿的快照，方便审计与复现
failed/<时间戳>/           失败证据：原 zip + reason.txt
index.json                 客户端唯一读取点（工具生成，不要手改）
tools/market.mjs           打包 / 上架 / 生成索引（零依赖）
tools/zip.mjs              手写 zip 读写（固定时间戳，产出可复现）
tools/ci/publish.mjs       CI 编排器（收件箱 → 编译 → 打包 → 索引）
schema/index.schema.json   索引结构
docs/                      投稿与契约文档
```

## 几条硬规则

- **不能降级**：新版本号必须大于已发布版本，否则直接拒绝。
- **同版本不能改内容**：改了内容就升版本号。
- **单包上限 20 MB**、解压总量 128 MB、单个 zip 最多 2000 个条目。
- **`index.json` 不要手改**，它是 `node tools/market.mjs build` 的产物；改完下次推送会被覆盖。
- 每个插件默认保留最近 3 个版本的包，更早的会被清理（需要留档就自己另存）。

## 本地手动跑（不推 CI 也能验）

```bash
node tools/market.mjs build --root .      # 重建索引
node tools/market.mjs verify --root .     # 只校验：哈希、包内清单、路径是否都对得上
node tools/market.mjs list --root .       # 打印当前市场内容
```

## ⚠ 别 force push 这个分支

它被 `push -f` 重置过一次，代价是 `index.json` 消失、所有学生的市场页拉不到索引。
建议在分支保护里禁止 force push。

## 失败排查

推送后看仓库的 **Actions → market-publish**，跑完的 Summary 里有逐包结果表。
失败原因同时留在 `failed/<时间戳>/<zip>.reason.txt`，常见的几类：

| 现象 | 原因 |
|---|---|
| `zip 里既没有 .csproj，也没有 plugin.json` | zip 多套了一层目录，或打成了“文件夹的文件夹” |
| `契约工程已放到 ..\..\` 之后仍报 MSB9008 | csproj 里的 `ProjectReference` 路径被改过 |
| `同版本内容不同` | 改了代码没升 `plugin.json` 的 `version` |
| `版本倒退` | 提交的版本比市场里的旧 |
| `包体积 ... 超过上限` | 把 bin/obj 或 pdb 一起打进去了 |
