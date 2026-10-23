namespace CCL.Core.Net;

public sealed record MirrorTarget(Uri Uri, string SourceName, bool IsMirror);

/// <summary>
/// 把官方 URL 解析成候选源列表（官方优先，镜像在后），并记录各源的连续失败次数。
/// 连续失败达到阈值后该源进入冷却期，冷却期间不再参与候选，避免每次请求都先撞一次墙。
/// </summary>
public sealed class MirrorRouter
{
    private const int FailureThreshold = 3;
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    private readonly List<MirrorRule> _rules;
    private readonly Dictionary<string, SourceState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public MirrorRouter(IEnumerable<MirrorRule>? rules = null, bool mirrorsEnabled = true)
    {
        _rules = rules?.ToList() ?? [];
        MirrorsEnabled = mirrorsEnabled;
    }

    public bool MirrorsEnabled { get; set; }

    /// <summary>BMCLAPI 的既有映射，路径前缀已按实测结果给出。</summary>
    public static IReadOnlyList<MirrorRule> CreateBmclapiRules() =>
    [
        new("BMCLAPI", "launchermeta.mojang.com", "https://bmclapi2.bangbang93.com"),
        new("BMCLAPI", "piston-meta.mojang.com", "https://bmclapi2.bangbang93.com"),
        new("BMCLAPI", "launcher.mojang.com", "https://bmclapi2.bangbang93.com"),
        new("BMCLAPI", "libraries.minecraft.net", "https://bmclapi2.bangbang93.com", "/maven"),
        new("BMCLAPI", "resources.download.minecraft.net", "https://bmclapi2.bangbang93.com", "/assets")
    ];

    public IReadOnlyList<MirrorTarget> Resolve(Uri official)
    {
        var candidates = new List<MirrorTarget> { new(official, "Mojang", false) };
        if (!MirrorsEnabled) return candidates;

        foreach (var rule in _rules)
        {
            if (!rule.Matches(official)) continue;
            if (IsCoolingDown(rule.SourceName)) continue;
            candidates.Add(new MirrorTarget(rule.Rewrite(official), rule.SourceName, true));
        }

        return candidates;
    }

    public void ReportSuccess(string sourceName)
    {
        lock (_gate)
        {
            if (_states.TryGetValue(sourceName, out var state)) state.ConsecutiveFailures = 0;
        }
    }

    public void ReportFailure(string sourceName)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(sourceName, out var state))
                _states[sourceName] = state = new SourceState();
            state.ConsecutiveFailures++;
            if (state.ConsecutiveFailures >= FailureThreshold) state.CooldownUntil = DateTimeOffset.UtcNow + Cooldown;
        }
    }

    public bool IsCoolingDown(string sourceName)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(sourceName, out var state)) return false;
            if (state.CooldownUntil is not { } until) return false;
            if (until > DateTimeOffset.UtcNow) return true;
            state.CooldownUntil = null;
            state.ConsecutiveFailures = 0;
            return false;
        }
    }

    private sealed class SourceState
    {
        public int ConsecutiveFailures { get; set; }

        public DateTimeOffset? CooldownUntil { get; set; }
    }
}
