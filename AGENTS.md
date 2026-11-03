# AGENTS.md

背景：
Class Daily Land 插件市场后端：Python 3 + FastAPI + SQLModel + PostgreSQL，uvicorn 单机部署。
插件包、hash.json、list.json 全存 GitHub（LCHXXXX-1/Class-Daily-Land 的 plugins 分支），stdlib urllib 实现，不引 requests。
调用方是已发布的 C# 桌面客户端，API 兼容性直接决定老用户能不能用。
单机 2核2G，无 Redis、无 Celery、无 Docker。

规则：
- 禁编译/测试/跑构建/说"已验证"
- 最小 diff（旧行→新行），不贴完整函数、不复述背景；同函数多处改动合并一个 hunk
- 不新增第三方依赖；stdlib 能做的（urllib、hashlib、zipfile）一律用 stdlib
- 改签名/语义须列影响面（谁调用、是否破坏已发布客户端）
- 影响方向的问题必须问我，不猜着改（字段语义、是否兼容旧客户端、是否新增表、方案取舍）；能自己查的不问
- 不确定说不确定，不猜

红线（改前必须在影响面里单说）：
- API 字段只增不改；客户端已发布，删字段改语义=老用户崩
- GitHub `_get_tree` 失败返回 None 让调用方拒绝，不能返回 {}
- approve 多步（GitHub 上传 / list.json / DB commit / 删本地）不是事务，失败后状态要能收拾
- 合作关系锚定 `package_name`，不绑 `record.id`
- bcrypt 72 字节硬限制，新密码入口自己检查字节长度
- JWT 无撤销，改 auth 不能假设改完就生效
- 迁移用 inspect + ALTER，不用 `ADD COLUMN IF NOT EXISTS`（PG 不支持）
- 服务器地址 / root 密码 / GitHub Token / JWT_SECRET / 数据库口令不进仓库

输出：
表格 / 最小 diff / ≤3 条验证项
末尾必须基于本阶段实际改动，给出需要我人工核对的验证项（不预写、由你生成）