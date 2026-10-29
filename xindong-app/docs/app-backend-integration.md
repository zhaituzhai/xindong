# 薪动 App 接入后台改造方案（草案 v1）

> 范围：登录（设备注册）+ 数据上报 + 拉取榜单
> 后续扩展（好友/PK/挑战/段位）另立文档，本方案预留接口位但不实现。
> 后端：`xindong-server`（FastAPI + SQLite），默认 `http://<host>:8770`，文档 `/docs`。

---

## 一、现状与目标

### 现状
- app 是纯本地单机：`StorageService` 读写 `xindong_config.json` / `xindong_ledger.json`，每 30 秒落盘。
- 唯一网络调用是 `HolidayService` → `timor.tech`（第三方节假日 API）。
- 无 `HttpClient`、无账号体系、无任何自建后端通信。
- 数据模型见 `Models/AppModels.cs`：`UserConfig`（配置）+ `Ledger{Version, Records:List<DailyRecord>}`（账本）。

### 目标
1. **登录**：首次启动自动注册设备，拿到 token；可绑定昵称用于榜单展示。
2. **上报**：把 `UserConfig` + `Ledger` 周期性/事件驱动上报到 server，server 拆解入库参与榜单计算。
3. **拉取榜单**：新增「排行」入口，展示日/周榜与我的排名。
4. **不破坏单机体验**：后台不可达时静默降级为纯本地，绿色便携哲学不变。

---

## 二、后端接口清单（已实现，本方案对接）

| 用途 | 方法 | 路径 | 认证 | 说明 |
|---|---|---|---|---|
| 注册/上报 | POST | `/api/v1/report` | 首次无 token，后续 Bearer | 全量快照，返回 `{device_id, token, nickname}` |
| 心跳 | POST | `/api/v1/heartbeat` | Bearer | 轻量保活，更新 last_active/online |
| 绑定昵称 | POST | `/api/v1/bind/nickname` | Bearer | body `{nickname}`，榜单展示用 |
| 注销 | POST | `/api/v1/logout` | Bearer | 清除 server 端全部数据 |
| 日榜 | GET | `/api/v1/leaderboard/daily?metric=&date=&limit=` | 可选 Bearer | metric 见下方清单 |
| 周榜 | GET | `/api/v1/leaderboard/weekly?metric=&date=&limit=` | 可选 Bearer | 周一~周日 |
| 我的排名 | GET | `/api/v1/leaderboard/me?scope=&date=` | Bearer | 返回所有指标我的位次 |
| 段位(预留) | GET | `/api/v1/tier/me` | Bearer | 本方案仅展示，不实现段位逻辑 |

**榜单 metric 取值**（`rank.py:METRICS`）：
`normal_earned`(今日已赚) / `net_earned`(净赚) / `slacking_earned`(摸鱼赚) / `worked_seconds`(有效工时) / `total_seconds`(总工时) / `ontime_days`(按时下班) / `full_attendance`(全勤) / `overtime_loss`(加班亏损) / `emergency_call_loss`(夺命Call亏损) / `rounding_loss`(抹零) / `depreciation_pct`(身价贬值) / `slacking_seconds`(摸鱼时长) / `slacking_count`(摸鱼次数) / `single_slack_max`(单次最长摸鱼) / `emergency_call_count`(夺命Call次数) / `poorest`(穷鬼榜)

**认证机制**：无账号密码。`device_id`（app 生成的设备指纹）+ `token`（server 颁发的 64 位 hex）。首次 `POST /report` 带 `device_id` 无 token → server 建用户发 token；后续 `Authorization: Bearer <token>`。

---

## 三、登录流程（实现细化）

### 3.1 设备指纹算法（零 NuGet，仅 BCL）

**指纹来源选择**（按稳定性排序，只取最稳定的两项，避免漂移）：

| 来源 | 取法 | 稳定性 | 是否采用 |
|---|---|---|---|
| MachineGuid | 注册表 `HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid` | Windows 装机时生成，重装系统才变，极稳定 | ✅ 主 |
| CPU Identifier | 注册表 `HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\Identifier` | CPU 不换就不变 | ✅ 主 |
| 网卡 MAC | `NetworkInterface.GetAllNetworkInterfaces()` | 可禁用/可换网卡/虚拟网卡乱序，**会漂移** | ❌ 不采用 |
| 主板序列号 | WMI `Win32_BaseBoard.SerialNumber` | 需 `System.Management` 包，违反零 NuGet | ❌ 不采用 |

**算法**：`device_id = SHA256(MachineGuid + "|" + CpuIdentifier)` 取前 16 字节 hex（32 字符小写）。两项都取不到（极罕见，至少 MachineGuid 几乎总有）→ `Guid.NewGuid().ToString("N")` 兜底，`FingerprintSource="Guid"`。

**为什么不用 MAC**：MAC 会因禁用网卡、插拔 USB 网卡、虚拟网卡增删而变，导致 device_id 漂移→同一台机器被 server 当成新用户。MachineGuid+Cpu 已足够区分不同机器且不漂移。

### 3.2 `DeviceIdentity` 模型 + `xindong_device.json` 结构

```jsonc
{
  "DeviceId": "a1b2c3...32字符",
  "Token": null,                    // 注册前 null，注册后 64 位 hex
  "Nickname": null,                 // 未绑花名 null
  "RegisteredAt": null,             // ISO 8601，注册前 null
  "ServerBaseUrl": "http://localhost:8770",
  "FingerprintSource": "MachineGuid+Cpu",  // 或 "Guid"
  "CreatedAt": "2026-10-29T14:30:00+08:00"
}
```

文件落 exe 同级（与 `xindong_config.json` 同目录），原子写入（先 `.tmp` 再 `File.Replace`，复刻 `StorageService` 既有模式）。

### 3.3 `DeviceIdService` 完整实现

```csharp
using Microsoft.Win32;            // BCL，注册表读取
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XinDong.Services;

public sealed class DeviceIdentity
{
    public string DeviceId { get; set; } = "";
    public string? Token { get; set; }
    public string? Nickname { get; set; }
    public string? RegisteredAt { get; set; }
    public string ServerBaseUrl { get; set; } = "http://localhost:8770";
    public string FingerprintSource { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}

public sealed class DeviceIdService
{
    private readonly string _path;
    private DeviceIdentity? _cache;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public DeviceIdService(string baseDirectory)
        => _path = Path.Combine(baseDirectory, "xindong_device.json");

    public DeviceIdentity LoadOrCreate()
    {
        if (_cache is not null) return _cache;
        _cache = TryLoad() ?? Create();
        return _cache;
    }

    public void Save(DeviceIdentity identity)
    {
        _cache = identity;
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(identity, JsonOpts), Encoding.UTF8);
        if (File.Exists(_path)) File.Replace(tmp, _path, null);
        else File.Move(tmp, _path);
    }

    private DeviceIdentity? TryLoad()
    {
        if (!File.Exists(_path)) return null;
        try { using var fs = File.OpenRead(_path); return JsonSerializer.Deserialize<DeviceIdentity>(fs, JsonOpts); }
        catch { return null; }   // 损坏则重建
    }

    private DeviceIdentity Create()
    {
        var (id, src) = GenerateFingerprint();
        var identity = new DeviceIdentity
        {
            DeviceId = id,
            FingerprintSource = src,
            CreatedAt = DateTime.Now.ToString("o")
        };
        Save(identity);
        return identity;
    }

    private static (string id, string source) GenerateFingerprint()
    {
        var parts = new List<string>(2);
        var src = new List<string>(2);

        var mg = ReadRegistry(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid");
        if (!string.IsNullOrEmpty(mg)) { parts.Add(mg); src.Add("MachineGuid"); }

        var cpu = ReadRegistry(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "Identifier");
        if (!string.IsNullOrEmpty(cpu)) { parts.Add(cpu); src.Add("Cpu"); }

        if (parts.Count == 0)
            return (Guid.NewGuid().ToString("N"), "Guid");   // 兜底，极罕见

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)));
        return (Convert.ToHexString(hash, 0, 16).ToLowerInvariant(), string.Join("+", src));
    }

    private static string? ReadRegistry(string path, string value)
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(path); return k?.GetValue(value)?.ToString(); }
        catch { return null; }   // 权限不足等
    }
}
```

### 3.4 `EnsureRegisteredAsync` 完整状态机

四个状态：`NoDevice` / `Unregistered` / `Registered` / `Offline`。

```
EnsureRegisteredAsync(identity, config, ledger):
  ┌─ 有 Token？
  │   是 → ProbeAsync(GET /api/v1/leaderboard/me, Bearer token)
  │        ├─ 200 Ok          → return Registered          (token 有效，结束)
  │        ├─ 401 Unauthorized→ identity.Token=null; Save  → 落到「无 Token」分支
  │        └─ 超时/5xx/网络错 → return Offline             (保留 token，server 暂不可达，不阻断本地)
  │
  └─ 无 Token（首次 或 失效后）
      → POST /api/v1/report（无 Authorization，body=BuildReportPayload(identity,config,ledger)）
         ├─ 200 + {token,nickname} → identity.Token/Nickname/RegisteredAt 落盘 → return Registered
         └─ 超时/5xx/网络错       → return Offline（待发队列会在下次重试，见第四节）
```

**关键设计**：
- **注册与首次上报合一**：`/report` 本就是「建用户 + 发 token + 存数据」三合一（`snapshot.store_report`），所以首次注册直接发完整 payload，不单独搞注册接口。即使 ledger 为空（首次运行未填薪资），server 也接受（`Records` 空数组）。
- **探活用 `/leaderboard/me` 而非专用 ping**：复用既有接口，省一个端点；且能顺带验证 token 对榜单可用。
- **离线不阻断**：任何网络错误返回 `Offline`，app 继续纯本地运行；token 保留，下次启动或下次上报成功即恢复 `Registered`。
- **401 才清 token**：只有明确的「token 无效」才清，网络错误不清（避免 server 短暂不可达导致误清 token 反复注册）。

### 3.5 `ApiService.EnsureRegisteredAsync` 代码骨架

```csharp
public enum RegistrationState { NoDevice, Unregistered, Registered, Offline }
private enum ProbeResult { Ok, InvalidToken, NetworkError }

public async Task<RegistrationState> EnsureRegisteredAsync(
    DeviceIdentity identity, UserConfig config, Ledger ledger, CancellationToken ct)
{
    if (!string.IsNullOrEmpty(identity.Token))
    {
        var probe = await ProbeTokenAsync(identity, ct);
        if (probe == ProbeResult.Ok) return RegistrationState.Registered;
        if (probe == ProbeResult.InvalidToken)
        {
            identity.Token = null;
            _device.Save(identity);
        }
        if (probe == ProbeResult.NetworkError) return RegistrationState.Offline;
    }

    // 无 token：发 /report 注册
    var payload = BuildReportPayload(identity, config, ledger);
    var resp = await PostReportAsync(identity.ServerBaseUrl, payload, token: null, ct);
    if (resp is null) return RegistrationState.Offline;   // 离线，入待发队列

    identity.Token = resp.Token;
    identity.Nickname = resp.Nickname;
    identity.RegisteredAt = DateTime.Now.ToString("o");
    _device.Save(identity);
    return RegistrationState.Registered;
}

private async Task<ProbeResult> ProbeTokenAsync(DeviceIdentity identity, CancellationToken ct)
{
    try
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/leaderboard/me?scope=daily");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Token);
        using var resp = await _http.SendAsync(req, ct);
        return resp.StatusCode == HttpStatusCode.Unauthorized ? ProbeResult.InvalidToken : ProbeResult.Ok;
    }
    catch { return ProbeResult.NetworkError; }   // 超时/连接失败/5xx 统一归网络错
}
```

### 3.6 与 `App.xaml.cs` 启动流程的插入点

在现有 `OnStartup`（`App.xaml.cs:38-182`）中，**在 `StorageService` 加载完 config/ledger 之后、`_vm.Start()` 之前**插入：

```csharp
// App.xaml.cs 现有第 82-92 行（加载 config/ledger）之后
var deviceSvc = new DeviceIdService(_storage.BaseDirectory);
var identity = deviceSvc.LoadOrCreate();
var api = new ApiService(deviceSvc, _storage.BaseDirectory);

if (config.JoinLeaderboard)   // D3 默认关，未加入则跳过整个后台
{
    _ = api.EnsureRegisteredAsync(identity, config, ledger, default);  // fire-and-forget，不阻塞 UI
}
```

- **fire-and-forget**：注册异步进行，不卡 UI；未完成时排行页显示「正在加入排行…」。
- **`JoinLeaderboard=false` 时完全不创建 `ApiService`**：零网络开销，纯单机。
- `ApiService` 实例存到 `App` 字段，供 `ReportScheduler` 和 `RankViewModel` 复用。

### 3.7 昵称绑定

- 设置页「加入排行」开关打开时，若 `identity.Nickname` 为空 → 弹「起个花名」输入框（可跳过）。
- `POST /api/v1/bind/nickname` body `{"nickname":"..."}`：
  - 200 → 落盘 `identity.Nickname`
  - 409 → 提示「花名已被占用，换一个」
  - 400 → 提示「花名无效（1-32 字符）」
- 榜单展示：有 Nickname 显示花名，无则显示 `DeviceId.Substring(0,6)`（server 端 `nickname` 字段可空，`leaderboard.py` 已处理 None）。

### 3.8 边界与身份语义

| 场景 | 行为 | 是否符合预期 |
|---|---|---|
| 正常换机 | MachineGuid+Cpu 变 → 新 device_id | ✅ 新机器=新身份 |
| 重装系统（同机） | MachineGuid 变 → 新 device_id | ⚠️ 同机重装=新身份（可接受，server 端旧数据成孤儿，可 `/logout` 清） |
| 复制便携包到新机 | `xindong_device.json` 随包走 → 同 device_id | ✅ 用户主动复制=想保留身份 |
| 想换新身份 | 删 `xindong_device.json` | ✅ 下次启动重新生成 |
| VM 克隆 | MachineGuid 是 VM 内 Windows 生成，克隆镜像同 GUID → 同身份 | ✅ 克隆=同一镜像，符合直觉 |
| 注册表读不到（权限/非 Win） | GUID 兜底，`FingerprintSource="Guid"` | ✅ 不崩，身份稳定（持久化后不再变） |
| server 重建 DB | 所有 token 失效 → 探活 401 → 自动清 token 重新注册 | ✅ 无感恢复 |
| server 暂时不可达 | 探活/注册返回 Offline → 保留 token，纯本地继续 | ✅ 不阻断，下次恢复 |

---

## 四、数据上报流程

### 4.1 上报内容（对齐 `ReportPayload`，`models.py:6`）
```jsonc
{
  "device_id": "<设备指纹>",
  "client_version": "1.0.0",
  "client_tz_offset": 480,          // 分钟，+08:00 = 480
  "reported_at": "2026-10-29T14:30:00+08:00",
  "config": { /* UserConfig 整对象，PascalCase */ },
  "ledger": { "Version": 1, "Records": [ /* DailyRecord[] */ ] },
  "event_type": "report"            // 或 "heartbeat"
}
```
- `config` 直接序列化 `UserConfig`（枚举已用 `JsonStringEnumConverter` 写字符串，server 已对齐）。
- `ledger` 直接序列化 `Ledger`（`{Version, Records}`，server 读 `ledger["Records"]`，每个 record 按 `Date` 主键 upsert）。
- **字段大小写**：app 用 PascalCase（`NormalEarned`/`WorkedSeconds`...），server 也是 PascalCase 读取，已对齐 ✓。

### 4.2 触发时机（**待确认** D2）
候选策略（推荐 C 混合）：
- A) **纯定时**：每 N 分钟一次全量 `/report`。简单但浪费。
- B) **纯事件**：下班结算、退出应用、配置变更时上报。实时性差。
- C) **混合（推荐）**：
  - 事件驱动全量 `/report`：下班封账、配置保存、应用退出、夺命 Call 结束。
  - 定时 `/heartbeat`：每 2 分钟一次（仅 config+ledger 引用计数，server 仅更新 online/last_active，不拆解入库）。server 限流 120/min，远够用。
  - 启动后首次 `/report`：补齐离线期间漏报。

### 4.3 离线与重试（`ApiService` 内置）
- `HttpClient` 超时 5 秒，失败静默（不弹窗，不阻断本地功能）。
- 失败的 `/report` 入「待上报队列」（内存 + `xindong_pending_reports.json` 落盘），下次启动或下次成功上报后补发。
- 队列上限 20 条（FIFO），超出丢弃最旧——避免长期离线后补发风暴。
- server 5xx / 网络错误 → 重试；4xx（含 401）→ 不重试，401 清 token 走重新注册。

### 4.4 隐私开关（**待确认** D3）
- `UserConfig` 新增 `JoinLeaderboard`（bool，默认 **false**——尊重单机哲学，用户主动加入）。
- 关闭时：不发起任何 `/report`/`/heartbeat`，不拉榜单，排行入口置灰提示「未加入排行」。
- 开启时：首次引导花名 → 注册 → 开始上报。

---

## 五、拉取榜单

### 5.1 新增「排行」页（主控台第 5 页，`PageRank=4`）
- 入口：左侧导航新增「🏆 排行」图标（`MainWindow.xaml` 导航区）。
- 未加入排行 → 占位引导页（开关 + 花名输入）。
- 已加入 → 顶部 metric 选择胶囊（横向滚动，16 个指标）+ scope 切换（日/周）+ 日期选择 + 榜单列表。

### 5.2 榜单 ViewModel（`RankViewModel`，新增）
- `Metrics`：16 个 `OptionItem`（label 来自 server 返回的 `metric_label`，本地也可内置中文映射兜底）。
- `Scope`：Daily / Weekly。
- `Entries`：`ObservableCollection<RankEntry>`，每项 `{rank, nickname, isMe, diffToTop, diffToPrev, absolute, isMoney}`。
- `MyPosition`：调用 `/api/v1/leaderboard/me` 拿各指标我的位次，顶部卡片展示。
- 刷新：进入页面拉一次 + 每 60 秒自动刷新 + 手动下拉刷新。

### 5.3 缓存
- 榜单结果缓存到 `xindong_rank_cache.json`（带 `fetched_at`），离线时展示缓存 + 角标「离线 · 更新于 xx:xx」。
- 缓存有效期 10 分钟，过期且离线 → 仍展示但标红「数据可能过期」。

### 5.4 金额脱敏
- server 对非本人金钱指标返回 `absolute=null`（仅返回 `diff_to_top`/`diff_to_prev`），避免暴露他人具体收入。
- 本人行展示 `absolute`，他人行只展示排名 + 与榜首/前一名差值。

---

## 六、改造清单

### 6.1 新增文件
| 文件 | 职责 |
|---|---|
| `Services/DeviceIdService.cs` | 设备指纹生成与持久化（`xindong_device.json`） |
| `Services/ApiService.cs` | `HttpClient` 封装：注册、上报、心跳、榜单、昵称；含超时/重试/待发队列 |
| `Services/ReportScheduler.cs` | 上报调度：事件订阅 + 定时心跳 + 启动补发 |
| `Models/ApiModels.cs` | 后端 DTO：`ReportPayload`/`LeaderboardResponse`/`LeaderboardEntry`/`RankEntry`/`DeviceIdentity` |
| `ViewModels/RankViewModel.cs` | 排行页 VM |
| `Views/RankView.xaml(.cs)` | 排行页 UI（或并入 MainWindow 的 PageRank 区） |

### 6.2 修改文件
| 文件 | 改动 |
|---|---|
| `Models/AppModels.cs` | `UserConfig` 增 `JoinLeaderboard`(bool)、`ServerBaseUrl`(string)、`Nickname`(string) |
| `App.xaml.cs` | 启动流程插入 `ApiService.EnsureRegisteredAsync()`；退出前 flush 待发队列；接线 `ReportScheduler` |
| `ViewModels/MainViewModel.cs` | 下班/夺命Call/保存配置事件触发 `ReportScheduler.ReportNow()`；新增 `PageRank` 常量与导航 |
| `Views/MainWindow.xaml` | 导航区加「排行」入口；右侧内容区按 `PageRank` 切换 |
| `Services/StorageService.cs` | `xindong_device.json` / `xindong_pending_reports.json` / `xindong_rank_cache.json` 读写 |

### 6.3 不改动
- `HolidayService`（仍用 timor.tech，不切自建节假日 API）。
- 计薪核心逻辑、状态机、本地账本——上报是「旁路」，本地永远是 source of truth。
- 零 NuGet 约束不变：用 `HttpClient`（BCL）、`System.Text.Json`（BCL）。

---

## 七、关键决策点（已定稿）

| 编号 | 决策 | 结论 |
|---|---|---|
| D1 | 设备指纹生成方式 | **C) 硬件指纹为主 + GUID 兜底**：优先 MAC+CPU+主板序列号哈希，取不到回退随机 GUID 持久化 |
| D2 | 上报触发时机 | **C) 事件 + 定时心跳混合**：下班/存配置/退出/夺命Call 触发全量 `/report`，每 2 分钟 `/heartbeat` |
| D3 | 加入排行默认开关 | **B) 默认关**：尊重单机哲学，设置页主动开启才上报+上榜，首次启动不打扰 |
| D4 | 排行页位置 | **A) 主控台第 5 页**：左侧导航「🏆 排行」，与今日/看板/账本/设置平级 |
| D5 | server base_url 配置 | **A) 硬编码默认 + 设置页可改**：默认 `http://localhost:8770`，自部署用户可在设置页改 |
| D6 | 本轮范围 | **A) 仅登录 + 上报 + 榜单**：段位/好友/PK/挑战留后续迭代，方案预留接口位 |

---

## 八、风险与兼容

1. **隐私顾虑**：上报含薪资/房租等敏感配置。缓解：默认关、文档明示上报内容、server 自部署可改 base_url。
2. **token 失效**：server 重建 DB 后 token 全失效。app 探活 401 → 自动重新注册（无感）。
3. **离线补发风暴**：队列上限 20 + FIFO 丢弃最旧，避免长期离线后补发压垮 server。
4. **字段漂移**：app 演进新增 `DailyRecord` 字段，server 用 `rec.get()` 容错，不会崩；但新指标需 server 同步加 METRICS 才能上榜。
5. **绿色便携**：所有新增 json 落 exe 同级，整个文件夹仍可迁移；`xindong_device.json` 随文件夹走（换机复制 = 同一设备身份，符合便携预期）。
6. **零依赖不变**：全部用 BCL，不引 NuGet。

---

## 九、实施顺序（建议）

1. `DeviceIdService` + `ApiModels` + `ApiService`（注册 + 上报 + 榜单 HTTP 骨架，离线容错）
2. `UserConfig` 增字段 + 设置页「加入排行/花名/server地址」UI
3. `ReportScheduler` 接线事件 + 启动注册
4. `RankViewModel` + 排行页 UI + 缓存
5. 联调（本地起 server）、边界（离线、token 失效、限流）