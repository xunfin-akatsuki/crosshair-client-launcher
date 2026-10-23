namespace CCL.Core.Minecraft.Versioning;

/// <summary>规则判定所需的运行环境。features 用于 has_custom_resolution 之类的开关。</summary>
public sealed record RuleContext(string OsName, string OsArch, IReadOnlyDictionary<string, bool>? Features = null)
{
    public static RuleContext Current(IReadOnlyDictionary<string, bool>? features = null) =>
        new(GetOsName(), GetOsArch(), features);

    public static string GetOsName() =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "osx" :
        OperatingSystem.IsLinux() ? "linux" : "unknown";

    public static string GetOsArch() => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.Arm => "arm32",
        _ => "unknown"
    };
}

public static class RuleEvaluator
{
    /// <summary>
    /// 与官方实现一致：没有规则视为允许；有规则时按顺序遍历，最后一条命中的规则决定结果。
    /// </summary>
    public static bool IsAllowed(IReadOnlyList<Rule>? rules, RuleContext context)
    {
        if (rules is null || rules.Count == 0) return true;

        var allowed = false;
        foreach (var rule in rules)
        {
            if (!Matches(rule, context)) continue;
            allowed = rule.Action.Equals("allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static bool Matches(Rule rule, RuleContext context)
    {
        if (rule.Os is { } os)
        {
            if (os.Name is { Length: > 0 } name && !name.Equals(context.OsName, StringComparison.OrdinalIgnoreCase))
                return false;
            if (os.Arch is { Length: > 0 } arch && !arch.Equals(context.OsArch, StringComparison.OrdinalIgnoreCase))
                return false;
            // os.version 依赖具体系统版本号，Minecraft 侧只在极少数规则上使用，此处不做匹配
        }

        if (rule.Features is { Count: > 0 } features)
        {
            foreach (var (key, expected) in features)
            {
                var actual = context.Features?.GetValueOrDefault(key) ?? false;
                if (actual != expected) return false;
            }
        }

        return true;
    }
}
