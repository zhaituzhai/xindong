# 薪动后台 XinDong-Server

> 薪动 (XinDong) 客户端的轻量化后台：数据上报、日榜/周榜（正向+反向）、PK、段位赛、每日挑战、好友、webhook、Flet 管理面板。
>
> 技术栈：Python 3.11+ / FastAPI / SQLite / APScheduler / Flet。零外部服务依赖。

需求文档见 [REQUIREMENTS.md](REQUIREMENTS.md)。

## 一、安装

```bat
cd xindong-server
pip install -r requirements.txt
```

## 二、启动后台

```bat
python -m app.main
```

默认监听 `0.0.0.0:8770`，Swagger 文档 `http://127.0.0.1:8770/docs`。

## 三、灌入模拟数据

```bat
python scripts/seed.py --users 50 --days 7
```

## 四、启动管理面板

```bat
python panel/app.py
```

## 五、配置

编辑 `config.yaml`：端口、admin_token、限流、段位权重与百分位、挑战模板、webhook 重试策略。

## 六、主要 API

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/api/v1/report` | 上报（首次下发 token） |
| POST | `/api/v1/heartbeat` | 心跳 |
| POST | `/api/v1/bind/nickname` | 绑定昵称 |
| POST | `/api/v1/logout` | 注销删数据 |
| GET | `/api/v1/leaderboard/daily?metric=` | 日榜 |
| GET | `/api/v1/leaderboard/weekly?metric=` | 周榜 |
| GET | `/api/v1/leaderboard/me?scope=` | 自己排名 |
| POST | `/api/v1/pk/match` | 发起 PK |
| GET | `/api/v1/pk/{id}` | PK 实时差值 |
| POST | `/api/v1/pk/{id}/end` | 结算 |
| GET | `/api/v1/pk/white_collar` | 老板白嫖榜 |
| GET | `/api/v1/challenge/today` | 今日挑战 |
| GET | `/api/v1/tier/me` | 我的段位 |
| POST | `/api/v1/friend/invite` | 生成邀请码 |
| POST | `/api/v1/friend/accept` | 接受邀请 |
| GET | `/api/v1/export/ledger` | 导出账本 CSV |
| POST | `/api/v1/webhook/register` | 注册 webhook |

正向指标：`normal_earned net_earned slacking_earned worked_seconds total_seconds ontime_days full_attendance`
反向指标：`overtime_loss emergency_call_loss rounding_loss depreciation_pct slacking_seconds slacking_count single_slack_max emergency_call_count poorest`

## 七、定时任务

- 00:05 生成每日挑战
- 00:30 清理 30 天前快照（保留每日末次）
- 周一 00:10 段位结算
- 每 5 分钟标记离线