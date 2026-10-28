# 薪动后台 XinDong-Server 需求文档 v1

> 为「薪动 XinDong」客户端提供数据上报、榜单、PK、段位、挑战赛、好友体系的后台服务。
> 技术栈：Python 3.11+ / FastAPI / SQLite（单文件） / Flet（管理面板）。
> 部署形态：单机或单服务器，零外部依赖（除 Python 包），固定东八区 `Asia/Shanghai`。

---

## 1. 总体目标

1. **存储**：接收并持久化每个客户端上报的用户数据，包含 `xindong_config.json`（配置）与 `xindong_ledger.json`（账本）的全量内容，每次上报保留全量历史快照。
2. **榜单**：为每个用户产出**日榜**与**周榜**，每类榜单含**正向榜**与**反向榜**，全榜公开但金额脱敏。
3. **PK 玩**：提供 1v1 实时对账、每日挑战赛、老板白嫖榜、段位赛、匿名 PK 等趣味玩法。
4. **管理面板**：Flet 桌面端管理面板，查看榜单 / 用户 / 数据 / 段位 / 挑战。

---

## 2. 用户身份与鉴权

### 2.1 身份标识
- **匿名设备 ID 自动生成**：客户端首次启动本地生成一个设备唯一 ID（UUID v4 + 机器指纹哈希），无注册无密码。
- 后台按设备 ID 区分用户。用户可后期绑定昵称（花名），昵称唯一性以"先占先得 + 可弃用释放"为准。
- 同一设备 ID 多设备登录：**合并策略**——按字段 `LastUpdated` 取最新值合并，不踢人、不拒绝。

### 2.2 Token 鉴权
- 客户端首次上报时无需 token，后台校验设备 ID 格式后**下发 token**（随机 32 字节十六进制），并记录签发时间、来源 IP。
- 后续所有写接口必须携带 `Authorization: Bearer <token>`。
- token 可吊销（管理面板 / 注销接口）。
- 限流：按 token + IP 双维度令牌桶，默认 60 次/分钟，心跳通道单独放宽至 120 次/分钟。

---

## 3. 数据模型

### 3.1 上报载荷
客户端上报一个 JSON 包，结构对齐客户端现有两个文件：

```jsonc
{
  "device_id": "uuid",
  "client_version": "1.0.0",
  "client_tz_offset": 480,           // 客户端本地相对 UTC 分钟偏移，仅记录
  "reported_at": "2026-10-28T21:41:19+08:00",
  "config": { /* xindong_config.json 全量 */ },
  "ledger": { /* xindong_ledger.json 全量 */ }
}
```

### 3.2 持久化分层
| 表 | 用途 |
|---|---|
| `users` | device_id、昵称、token、签发时间、首报时间、最近活跃、段位、综合分等元数据 |
| `configs` | 每用户最新 config（覆盖更新，带 `updated_at`） |
| `ledger_records` | 每用户每日账本记录，按 `(device_id, date)` upsert，字段对齐客户端 `Records[]` |
| `snapshots` | 每次上报全量快照原始 JSON，带 `reported_at` |
| `friends` | 好友关系（双向确认） |
| `invitations` | 一次性邀请码 |
| `pk_sessions` | PK 对局 |
| `challenges` | 每日挑战定义与达成记录 |
| `ranks` | 段位与周结算快照 |
| `webhooks` | 外部订阅配置与投递日志 |

### 3.3 历史快照策略
- **最近 30 天**：每次上报全量快照全留。
- **30 天以前**：每日仅保留当日最后一次上报快照。
- 清理任务每日 00:30（东八区）执行。
- 注销时所有快照连同主数据一并物理删除。

### 3.4 多设备合并
- 收到上报时按字段级 `LastUpdated` 取较新值合并到 `configs` / `ledger_records`。
- `snapshots` 始终追加新行，不做合并。

---

## 4. 上报协议（事件 + 心跳混合）

### 4.1 事件驱动上报
客户端在以下事件触发上报：
- 启动 / 退出
- 下班确认 / 误触撤回
- 摸鱼开始 / 摸鱼结束
- 夺命 Call 开始 / 结束
- 配置变更
- 日切封账

事件上报携带事件类型与当前全量 `config` + `ledger`。

### 4.2 心跳上报
- **60 秒一次**，携带当前全量 `config` + `ledger`，事件类型 `heartbeat`。
- 心跳通道单独限流 120 次/分钟。
- 心跳同时刷新 `users.last_active`；超过 5 分钟无心跳视为离线。

### 4.3 接口
- `POST /api/v1/report`：上报（首次无 token，响应下发 token；后续需 token）。
- `POST /api/v1/heartbeat`：心跳（需 token）。
- `POST /api/v1/bind/nickname`：绑定/修改昵称。
- `POST /api/v1/logout`：注销并删除全部数据（需 token，二次确认）。

---

## 5. 榜单体系

### 5.1 时间界
- **日榜**：服务器自然日 00:00 切（东八区）。
- **周榜**：周一 00:00 ~ 周日 24:00（东八区，ISO 周一为首日）。

### 5.2 正向榜指标
| 指标 | 取值 | 排序 |
|---|---|---|
| 今日已赚 | `NormalEarned` | 降序 |
| 净赚 | `NormalEarned - 生存成本 - OvertimeLoss - RoundingLoss - EmergencyCallLoss` | 降序 |
| 摸鱼赚 | `SlackingEarned` | 降序 |
| 有效工时 | `WorkedSeconds` | 降序 |
| 总工时 | `WorkedSeconds + OvertimeSeconds` | 降序 |
| 按时下班天数 | 周期内 `IsOffDuty && OvertimeSeconds==0 && AutoFilled==false` 的天数 | 降序 |
| 全勤天数 | 周期内非休息日且有有效工时的天数 | 降序 |

### 5.3 反向榜指标
| 指标 | 取值 | 排序 |
|---|---|---|
| 义务加班亏损 | `OvertimeLoss` | 降序（亏越多越靠前） |
| 夺命 Call 亏损 | `EmergencyCallLoss` | 降序 |
| 抹零损失 | `RoundingLoss` | 降序 |
| 身价贬值百分比 | `(标准时薪 - 实际时薪) / 标准时薪` | 降序 |
| 摸鱼时长 | `SlackingSeconds` | 降序 |
| 摸鱼次数 | `SlackingCount` | 降序 |
| 单次最长摸鱼 | `max(SlackingStats.duration)` | 降序 |
| 夺命 Call 次数 | `EmergencyCallCount` | 降序 |
| 净赚倒排（穷鬼榜） | `净赚` | **升序**（最穷最靠前） |

### 5.4 可见性与脱敏
- **全榜公开**：所有用户可查看全部榜单。
- **金额脱敏**：榜单只展示 `昵称 + 排名 + 与第一名的差值 + 与上一名的差值`，**不展示绝对金额**。
- 工时、次数、百分比等非金额指标可展示绝对值。
- 查看自己时额外返回自己的绝对值与全服百分位。

### 5.5 接口
- `GET /api/v1/leaderboard/daily?metric=normal_earned&limit=50`
- `GET /api/v1/leaderboard/weekly?metric=overtime_loss&limit=50`
- `GET /api/v1/leaderboard/me?scope=daily|weekly`：返回自己在所有指标上的排名与邻居。

---

## 6. PK 环

### 6.1 1v1 实时对账
- 匹配模式三选一：`random`（全服在线随机）/ `tier`（同段位）/ `friend`（指定好友）。
- 建立对局后双方实时比 `今日已赚 / 净赚 / 摸鱼赚`，后台基于心跳推送差值（SSE 或轮询）。
- 任一方下班 / 退出 / 主动结束 → 结算，生成战报（胜负 + 三项差值 + 用时）。
- **匿名 PK 模式**：只显示"你比对手多赚 ¥X"，不暴露对方绝对金额与昵称（昵称用"神秘对手"替代）。

### 6.2 每日挑战赛
- **规则引擎随机生成**：每日 00:05 生成全服统一挑战，参数随机化，不重复（30 天内不重样）。
- 挑战模板池（参数随机）：
  - 摸鱼满 X 分钟（X ∈ [20, 60]）
  - 净赚超日薪 X%（X ∈ [70, 95]）
  - 零加班下班
  - 摸鱼次数达 X 次（X ∈ [3, 8]）
  - 单次摸鱼超 X 分钟（X ∈ [10, 25]）
  - 净赚超过全服中位数 X%（X ∈ [10, 30]）
- 全服达成排行按达成时间先后排。
- 达成时触发 webhook `challenge.achieved`。

### 6.3 老板白嫖榜
- 把 `OvertimeLoss + EmergencyCallLoss` 包装为"被老板白嫖金额"，亏损越多排名越靠前。
- 反向荣誉感：榜首获"白嫖之王"称号（每周一结算）。
- 该榜为反向榜，遵循 5.4 脱敏规则。

### 6.4 段位赛
- **综合分** = `周净赚 × 0.6 + 全勤天数 × 日薪 × 0.4`（日薪取该用户配置折算），避免只看金额卷死低收入者。
- 段位：青铜 / 白银 / 黄金 / 铂金 / 钻石 / 王者（6 段）。
- 段位阈值按全服综合分百分位动态划分：青铜 < 25%，白银 25-50%，黄金 50-70%，铂金 70-85%，钻石 85-95%，王者 ≥ 95%。
- **每周一 00:10 结算**升降级，生成 `ranks` 快照。
- 升降级触发 webhook `tier.up` / `tier.down`。

### 6.5 接口
- `POST /api/v1/pk/match`：发起匹配 `{mode, friend_id?}`。
- `GET /api/v1/pk/{session_id}`：对局详情 / 实时差值（SSE）。
- `POST /api/v1/pk/{session_id}/end`：结束并结算。
- `GET /api/v1/challenge/today`：今日挑战与达成榜。
- `GET /api/v1/tier/me`：我的段位与全服分布。

---

## 7. 好友体系

- **一次性邀请码**：用户生成 6 位邀请码（字母数字，10 分钟有效，一次性）。
- 对方输入码即建立双向好友关系，不需暴露设备 ID。
- 好友列表用于 `friend` 模式 PK 与好友榜单筛选。
- 接口：
  - `POST /api/v1/friend/invite`：生成邀请码。
  - `POST /api/v1/friend/accept`：`{code}` 接受邀请。
  - `GET /api/v1/friend/list`：好友列表。
  - `DELETE /api/v1/friend/{device_id}`：解除好友。

---

## 8. 管理面板（Flet）

桌面端管理面板，功能页：
1. **概览**：在线用户数、今日上报数、活跃段位分布。
2. **榜单**：日榜/周榜切换，正向/反向切换，指标下拉。
3. **用户**：用户列表、详情（配置 / 账本 / 快照时间线 / 段位 / 好友）。
4. **PK**：进行中对局列表、历史战报。
5. **挑战**：今日挑战、达成榜、历史挑战。
6. **段位**：全服段位分布、本周升降级变动。
7. **Webhook**：订阅配置、投递日志。
8. **系统**：限流配置、token 吊销、数据清理。

面板通过调用后台 `admin` 接口（带 admin token）实现，本身不直连数据库。

---

## 9. 导出 / Webhook / 注销

### 9.1 CSV 导出
- `GET /api/v1/export/ledger?device_id=&from=&to=`：导出指定用户区间账本 CSV。
- `GET /api/v1/export/leaderboard?scope=daily|weekly&metric=&date=`：导出榜单 CSV。
- 管理面板可触发全服导出。

### 9.2 Webhook
- 用户/管理员注册 webhook URL + 订阅事件类型。
- 事件：`challenge.achieved` / `tier.up` / `tier.down` / `pk.settled` / `leaderboard.crowned`（周榜首）。
- 投递带 HMAC 签名，失败重试 3 次（指数退避），投递日志入 `webhooks` 表。

### 9.3 注销
- `POST /api/v1/logout`（需 token + 二次确认 `confirm: true`）：物理删除该用户所有数据（users / configs / ledger_records / snapshots / friends / pk 历史 / 段位 / 挑战达成）。
- 删除后 token 立即吊销。

---

## 10. 非功能需求

- **时区**：全后台固定 `Asia/Shanghai`，所有时间戳存储 UTC、展示按东八区。
- **存储**：SQLite 单文件 `xindong_server.db`，WAL 模式，定期 vacuum。
- **并发**：FastAPI + uvicorn，单进程多线程（SQLite 写串行化用连接池 + 重试）。
- **限流**：令牌桶，按 token + IP 双维度。
- **日志**：结构化 JSON 日志，按天滚动，保留 30 天。
- **配置**：`config.yaml`（端口、限流、段位阈值、挑战模板、webhook 默认重试策略）。
- **自测数据生成器**：后台内置一个 `seed` 命令，生成 N 个模拟用户 + 模拟上报，用于榜单/PK 自测。
- **零外部服务依赖**：不依赖 Redis / MQ / Postgres，全部 SQLite + 内存。

---

## 11. 工程结构（拟）

```
xindong-server/
├─ REQUIREMENTS.md            # 本文档
├─ README.md                  # 启动说明
├─ requirements.txt           # fastapi, uvicorn, pydantic, flet, pyyaml, apscheduler
├─ config.yaml                # 服务配置
├─ xindong_server.db          # SQLite（运行时生成）
├─ app/
│  ├─ main.py                 # FastAPI 入口
│  ├─ config.py               # 配置加载
│  ├─ db.py                   # SQLite 连接 / 初始化
│  ├─ models.py               # Pydantic 模型（对齐客户端 config/ledger）
│  ├─ auth.py                 # token / 限流
│  ├─ api/
│  │  ├─ report.py            # 上报 / 心跳
│  │  ├─ leaderboard.py       # 榜单
│  │  ├─ pk.py                # PK / 段位 / 挑战
│  │  ├─ friend.py            # 好友
│  │  ├─ export.py            # CSV 导出
│  │  ├─ webhook.py           # webhook 注册/投递
│  │  └─ admin.py             # 管理面板后端
│  ├─ services/
│  │  ├─ snapshot.py          # 快照存储与清理
│  │  ├─ rank.py              # 榜单计算
│  │  ├─ tier.py              # 段位结算
│  │  ├─ challenge.py         # 挑战规则引擎
│  │  ├─ pk_match.py          # PK 匹配与结算
│  │  └─ webhook_dispatch.py
│  └─ jobs/
│     └─ scheduler.py         # apscheduler：快照清理 / 段位结算 / 挑战生成
├─ panel/
│  └─ app.py                  # Flet 管理面板
└─ scripts/
   └─ seed.py                 # 模拟数据生成器
```

---

## 12. 验收标准

1. 启动后台 + 跑 `seed --users=50 --days=7`，榜单各指标非空且排名正确。
2. 模拟两客户端上报，1v1 PK 能结算并产出战报。
3. 段位周一结算后 `ranks` 表有快照，百分位划分符合 6.4。
4. 每日挑战 00:05 自动生成，达成后 webhook 投递成功。
5. 注销后该用户所有表无残留。
6. 30 天后快照清理任务只保留每日末次。
7. Flet 面板能查看所有上述数据。
---

## 13. 管理面板重构设计（v2）

### 13.1 布局
- **左右分离**：左侧 `NavigationRail`（原生支持二级 `NavigationRailDestination`），右侧内容区 `Container`。
- 窗口宽 1200×800，左侧导航固定 240px，右侧自适应滚动。
- 顶部标题栏显示当前一级/二级菜单路径与后台地址。

### 13.2 一级菜单与二级菜单结构
| 一级菜单 | 二级菜单 | 说明 |
|---|---|---|
| 概览 | （无） | 单击直接显示系统概览，不展开二级 |
| 数据管理 | 用户管理 | 分页+按昵称/ID/段位/在线筛选+查看详情+吊销token+注销 |
| 数据管理 | 账本记录 | 按用户/日期范围筛选分页查看 |
| 数据管理 | 配置管理 | 按用户筛选分页查看各用户 config |
| 数据管理 | 快照管理 | 按用户/时间范围查看历史快照时间线 |
| 数据管理 | 好友关系 | 查看所有好友关系+解除 |
| 数据管理 | 邀请码 | 查看邀请码使用情况 |
| 数据管理 | Webhook管理 | 注册/查看/删除/投递日志 |
| 榜单管理 | 日榜·正向 | 页面内下拉选指标(7个)+日期 |
| 榜单管理 | 日榜·反向 | 页面内下拉选指标(9个)+日期 |
| 榜单管理 | 周榜·正向 | 页面内下拉选指标(7个)+日期 |
| 榜单管理 | 周榜·反向 | 页面内下拉选指标(9个)+日期 |
| 段位管理 | 当前分布 | 全服段位分布+我的段位 |
| 段位管理 | 历史结算 | 按周查看 ranks 结算记录 |
| 挑战管理 | 今日挑战 | 今日挑战定义+达成榜 |
| 挑战管理 | 历史挑战 | 按日期范围查看历史挑战 |
| 挑战管理 | 达成榜 | 按挑战日期查看达成排行 |
| PK管理 | 进行中 | 进行中对局列表 |
| PK管理 | 历史对局 | 分页查看历史战报 |
| PK管理 | 白嫖榜 | 老板白嫖榜 |

### 13.3 概览页内容
- 基础计数：在线用户数 / 总用户数 / 今日上报数 / 段位分布
- 近 7 天上报趋势：每日上报量数字列表
- 业务动态：进行中 PK 数 / 今日挑战达成数 / 本周段位结算状态
- 系统状态：数据库大小 / 快照总数 / 最近清理时间 / 定时任务下次执行时间

### 13.4 数据管理模式
- 统一模式：顶部筛选条件栏 + 分页表格（每页 20 条）+ 行操作按钮。
- 只读查看 + 少量管理操作（吊销 token / 注销用户 / 解除好友 / 删除 webhook），不做数据编辑。
- 分页参数：`page`（从1起）、`page_size`（默认20）。响应含 `total`、`page`、`page_size`、`items`。

### 13.5 需补充的后端 Admin API
| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/v1/admin/overview` | 扩展：增加近7天趋势、进行中PK数、今日挑战达成数、db大小、快照数、定时任务状态 |
| GET | `/api/v1/admin/users` | 增加分页+筛选参数 `page/page_size/nickname/tier/online` |
| GET | `/api/v1/admin/ledger` | 新增：`device_id/from/to/page/page_size` |
| GET | `/api/v1/admin/configs` | 新增：`device_id/page/page_size` |
| GET | `/api/v1/admin/snapshots` | 新增：`device_id/from/to/page/page_size` |
| GET | `/api/v1/admin/friends` | 新增：`device_id/page/page_size` |
| DELETE | `/api/v1/admin/friends/{a}/{b}` | 新增：解除好友 |
| GET | `/api/v1/admin/invitations` | 新增：`page/page_size` |
| GET | `/api/v1/admin/webhooks` | 增加分页 |
| DELETE | `/api/v1/admin/webhooks/{id}` | 新增：删除 webhook |
| GET | `/api/v1/admin/webhooks/{id}/logs` | 新增：投递日志 |
| GET | `/api/v1/admin/pk/list` | 新增：`status/page/page_size` 历史对局 |
| GET | `/api/v1/admin/ranks` | 新增：`week/page/page_size` 段位结算记录 |
| GET | `/api/v1/admin/challenges` | 新增：`from/to/page/page_size` 历史挑战 |
| GET | `/api/v1/admin/challenges/{day}/achieved` | 新增：指定日达成榜 |
| GET | `/api/v1/admin/system/status` | 新增：db大小/快照数/最近清理/定时任务下次执行 |

### 13.6 面板工程结构
```
panel/
├─ app.py              # 入口，构建左右布局与路由
├─ api.py              # 封装后台 admin API 调用
├─ widgets.py          # 通用组件：分页表格、筛选栏、空状态
└─ pages/
   ├─ overview.py
   ├─ data_users.py
   ├─ data_ledger.py
   ├─ data_configs.py
   ├─ data_snapshots.py
   ├─ data_friends.py
   ├─ data_invitations.py
   ├─ data_webhooks.py
   ├─ leaderboard.py   # 通用榜单页，按 scope+方向渲染指标下拉
   ├─ tier.py
   ├─ challenge.py
   └─ pk.py
```