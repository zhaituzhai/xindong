using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using XinDong.Models;

namespace XinDong.Services;

/// <summary>
/// 上报调度：事件驱动全量 /report + 每 2 分钟 /heartbeat + 离线待发队列补发。
/// 本地永远是 source of truth，上报失败静默入队，不阻断任何本地功能。
/// </summary>
public sealed class ReportScheduler : IDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(2);
    private const int MaxPending = 20;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ApiService _api;
    private readonly DeviceIdService _device;
    private readonly Func<UserConfig> _getConfig;
    private readonly Func<Ledger> _getLedger;
    private readonly string _pendingPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Timer _heartbeatTimer;

    public ReportScheduler(ApiService api, DeviceIdService device, string baseDirectory,
        Func<UserConfig> getConfig, Func<Ledger> getLedger)
    {
        _api = api;
        _device = device;
        _getConfig = getConfig;
        _getLedger = getLedger;
        _pendingPath = Path.Combine(baseDirectory, "xindong_pending_reports.json");
        _heartbeatTimer = new Timer(_ => _ = HeartbeatTick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>启动定时心跳。</summary>
    public void Start() => _heartbeatTimer.Change(HeartbeatInterval, HeartbeatInterval);

    /// <summary>停止心跳。</summary>
    public void Stop() => _heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);

    /// <summary>事件触发：立即全量上报，失败入待发队列。</summary>
    public async Task ReportNowAsync()
    {
        var identity = _device.LoadOrCreate();
        if (string.IsNullOrEmpty(identity.Token)) return;
        await _lock.WaitAsync();
        try
        {
            if (await _api.ReportAsync(identity, _getConfig(), _getLedger()))
                await DrainPendingAsync(identity);
            else
                EnqueuePending();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>退出前补发待发队列。</summary>
    public async Task FlushAsync()
    {
        Stop();
        var identity = _device.LoadOrCreate();
        if (string.IsNullOrEmpty(identity.Token)) return;
        await _lock.WaitAsync();
        try
        {
            await DrainPendingAsync(identity);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task HeartbeatTick()
    {
        try
        {
            var identity = _device.LoadOrCreate();
            if (string.IsNullOrEmpty(identity.Token)) return;
            await _api.HeartbeatAsync(identity, _getConfig(), _getLedger());
        }
        catch
        {
            // 心跳失败忽略
        }
    }

    private void EnqueuePending()
    {
        try
        {
            var list = LoadPending();
            list.Add(DateTime.Now.ToString("o"));
            if (list.Count > MaxPending) list.RemoveRange(0, list.Count - MaxPending);
            File.WriteAllText(_pendingPath, JsonSerializer.Serialize(list, JsonOpts));
        }
        catch
        {
            // 队列写失败忽略
        }
    }

    private List<string> LoadPending()
    {
        try
        {
            return File.Exists(_pendingPath)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_pendingPath)) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
    }

    private async Task DrainPendingAsync(DeviceIdentity identity)
    {
        if (LoadPending().Count == 0) return;
        if (await _api.ReportAsync(identity, _getConfig(), _getLedger()))
        {
            try { File.Delete(_pendingPath); } catch { }
        }
    }

    public void Dispose()
    {
        Stop();
        _lock.Dispose();
    }
}