using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Input;
using XinDong.Models;
using XinDong.Services;

namespace XinDong.ViewModels;

/// <summary>榜单指标元数据（key + 中文标签 + 是否金额）。</summary>
public sealed class RankMetric
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public bool IsMoney { get; init; }
}

/// <summary>排行页 ViewModel：拉取日/周榜 + 我的排名，离线展示缓存。</summary>
public sealed class RankViewModel : INotifyPropertyChanged
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private ApiService? _api;
    private readonly DeviceIdService _device;
    private readonly Func<UserConfig> _getConfig;
    private readonly string _cachePath;

    public static readonly RankMetric[] AllMetrics =
    [
        new() { Key = "normal_earned", Label = "今日已赚", IsMoney = true },
        new() { Key = "net_earned", Label = "净赚", IsMoney = true },
        new() { Key = "slacking_earned", Label = "摸鱼赚", IsMoney = true },
        new() { Key = "worked_seconds", Label = "有效工时" },
        new() { Key = "total_seconds", Label = "总工时" },
        new() { Key = "ontime_days", Label = "按时下班" },
        new() { Key = "full_attendance", Label = "全勤" },
        new() { Key = "overtime_loss", Label = "加班亏损", IsMoney = true },
        new() { Key = "emergency_call_loss", Label = "夺命Call亏损", IsMoney = true },
        new() { Key = "rounding_loss", Label = "抹零", IsMoney = true },
        new() { Key = "depreciation_pct", Label = "身价贬值" },
        new() { Key = "slacking_seconds", Label = "摸鱼时长" },
        new() { Key = "slacking_count", Label = "摸鱼次数" },
        new() { Key = "single_slack_max", Label = "单次最长摸鱼" },
        new() { Key = "emergency_call_count", Label = "夺命Call次数" },
        new() { Key = "poorest", Label = "穷鬼榜", IsMoney = true }
    ];

    private string _scope = "daily";
    private RankMetric _selectedMetric = AllMetrics[0];
    private string _statusText = "未加入排行";
    private bool _isLoading;
    private string _myRankText = "";
    private string _tierText = "";
    private string _tierScoreText = "";
    private string _prevTierText = "";
    private string _tierDistributionText = "";
    private string _challengeDescription = "";

    public RankViewModel(ApiService? api, DeviceIdService device, string baseDirectory, Func<UserConfig> getConfig)
    {
        _api = api;
        _device = device;
        _getConfig = getConfig;
        _cachePath = Path.Combine(baseDirectory, "xindong_rank_cache.json");
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync());
        SetScopeCommand = new RelayCommand(p => Scope = (string?)p ?? "daily");
    }

    public ObservableCollection<LeaderboardEntry> Entries { get; } = new();
    public ObservableCollection<RankMetric> Metrics { get; } = new(AllMetrics);

    public RankMetric SelectedMetric
    {
        get => _selectedMetric;
        set { _selectedMetric = value; OnPropertyChanged(); _ = RefreshAsync(); }
    }

    public string Scope
    {
        get => _scope;
        set { _scope = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsScopeDaily)); OnPropertyChanged(nameof(IsScopeWeekly)); _ = RefreshAsync(); }
    }

    public bool IsScopeDaily => _scope == "daily";
    public bool IsScopeWeekly => _scope == "weekly";

    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public string MyRankText
    {
        get => _myRankText;
        set { _myRankText = value; OnPropertyChanged(); }
    }

    public string TierText
    {
        get => _tierText;
        set { _tierText = value; OnPropertyChanged(); }
    }

    public string TierScoreText
    {
        get => _tierScoreText;
        set { _tierScoreText = value; OnPropertyChanged(); }
    }

    public string PrevTierText
    {
        get => _prevTierText;
        set { _prevTierText = value; OnPropertyChanged(); }
    }

    public string TierDistributionText
    {
        get => _tierDistributionText;
        set { _tierDistributionText = value; OnPropertyChanged(); }
    }

    public string ChallengeDescription
    {
        get => _challengeDescription;
        set { _challengeDescription = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasChallenge)); }
    }

    public bool HasChallenge => !string.IsNullOrEmpty(_challengeDescription);

    public ObservableCollection<ChallengeAchievedEntry> ChallengeAchieved { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand SetScopeCommand { get; }

    /// <summary>热切换加入排行时，App 层注入 ApiService 并刷新榜单。</summary>
    public void AttachApi(ApiService api)
    {
        _api = api;
        _ = RefreshAsync();
    }

    /// <summary>退出排行时，App 层调用以清空 api 引用和所有云端展示。</summary>
    public void DetachApi()
    {
        _api = null;
        Entries.Clear();
        ChallengeAchieved.Clear();
        MyRankText = "";
        TierText = "";
        TierScoreText = "";
        PrevTierText = "";
        TierDistributionText = "";
        ChallengeDescription = "";
        StatusText = "未加入排行（在设置页开启）";
    }

    public async Task RefreshAsync()
    {
        var config = _getConfig();
        if (_api is null || !config.JoinLeaderboard)
        {
            StatusText = "未加入排行（在设置页开启）";
            Entries.Clear();
            MyRankText = "";
            TierText = "";
            TierScoreText = "";
            PrevTierText = "";
            TierDistributionText = "";
            ChallengeDescription = "";
            ChallengeAchieved.Clear();
            return;
        }

        var identity = _device.LoadOrCreate();
        if (string.IsNullOrEmpty(identity.Token))
        {
            StatusText = "尚未注册，请稍候…";
            return;
        }

        IsLoading = true;
        StatusText = "加载中…";
        var date = DateTime.Now.ToString("yyyy-MM-dd");
        var resp = await _api!.GetLeaderboardAsync(identity, _scope, _selectedMetric.Key, date);
        IsLoading = false;

        if (resp is null)
        {
            LoadCache();
            StatusText = "离线 · 显示缓存";
            return;
        }

        Entries.Clear();
        foreach (var e in resp.Entries) Entries.Add(e);
        SaveCache(resp);
        StatusText = $"{resp.MetricLabel} · 共 {resp.Total} 人 · {DateTime.Now:HH:mm}";

        var mine = await _api!.GetMyPositionAsync(identity, _scope, date);
        if (mine is not null && mine.TryGetValue(_selectedMetric.Key, out var pos) && pos is not null)
            MyRankText = pos.Rank is int r ? $"我：第 {r} / {pos.Total} 名" : "我：未上榜";
        else
            MyRankText = "";

        await RefreshTierAndChallengeAsync(identity);
    }

    /// <summary>拉取段位 + 全服分布 + 今日挑战（与榜单同页展示）。</summary>
    private async Task RefreshTierAndChallengeAsync(DeviceIdentity identity)
    {
        var tier = await _api!.GetMyTierAsync(identity);
        TierText = tier?.Tier is string t && !string.IsNullOrEmpty(t) ? t : "未定级";
        TierScoreText = tier is not null && tier.Score != 0 ? $"段位分 {tier.Score:F1}" : "";
        PrevTierText = tier?.PrevTier is string p && !string.IsNullOrEmpty(p) ? $"上周：{p}" : "";

        var dist = await _api!.GetTierDistributionAsync(identity);
        if (dist is not null && dist.Count > 0)
            TierDistributionText = string.Join(" · ", dist.Select(kv => $"{kv.Key} {kv.Value}"));
        else
            TierDistributionText = "";

        var ch = await _api!.GetChallengeTodayAsync(identity);
        ChallengeAchieved.Clear();
        if (ch?.Challenge is not null && !string.IsNullOrEmpty(ch.Challenge.Description))
        {
            ChallengeDescription = ch.Challenge.Description;
            foreach (var a in ch.Achieved) ChallengeAchieved.Add(a);
        }
        else
        {
            ChallengeDescription = "";
        }
    }

    private void SaveCache(LeaderboardResponse resp)
    {
        try { File.WriteAllText(_cachePath, JsonSerializer.Serialize(resp, JsonOpts)); }
        catch { }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var resp = JsonSerializer.Deserialize<LeaderboardResponse>(File.ReadAllText(_cachePath), JsonOpts);
            if (resp is null) return;
            Entries.Clear();
            foreach (var e in resp.Entries) Entries.Add(e);
        }
        catch { }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}