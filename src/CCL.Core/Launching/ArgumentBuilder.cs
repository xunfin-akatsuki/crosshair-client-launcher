using System.Text.Json.Nodes;
using CCL.Core.Layout;
using CCL.Core.Minecraft.Versioning;

namespace CCL.Core.Launching;

/// <summary>参数模板求值时需要的全部占位符来源。</summary>
public sealed class LaunchContext
{
    public required GameAccount Account { get; init; }

    public required VersionMeta Meta { get; init; }

    public required string GameDirectory { get; init; }

    public required string AssetsRoot { get; init; }

    public required string AssetsIndexName { get; init; }

    public required string NativesDirectory { get; init; }

    public required IReadOnlyList<string> Classpath { get; init; }

    public string LauncherName { get; init; } = "CrosshairClientLauncher";

    public string LauncherVersion { get; init; } = "0.1.0";

    public int? ResolutionWidth { get; init; }

    public int? ResolutionHeight { get; init; }

    public bool DemoUser { get; init; }

    public IReadOnlyDictionary<string, bool> Features => new Dictionary<string, bool>
    {
        ["is_demo_user"] = DemoUser,
        ["has_custom_resolution"] = ResolutionWidth is > 0 && ResolutionHeight is > 0,
        ["has_quick_plays_support"] = false,
        ["is_quick_play_singleplayer"] = false,
        ["is_quick_play_multiplayer"] = false,
        ["is_quick_play_realms"] = false
    };

    public RuleContext ToRuleContext() => new(RuleContext.GetOsName(), RuleContext.GetOsArch(), Features);
}

public static class ArgumentBuilder
{
    /// <summary>JVM 参数。老版本没有 arguments 段，退回到固定的 -cp 形式。</summary>
    public static IReadOnlyList<string> BuildJvm(LaunchContext context)
    {
        var values = Collect(context.Meta.Arguments, "jvm", context.ToRuleContext());
        if (values.Count == 0)
            values = ["-Djava.library.path=${natives_directory}", "-cp", "${classpath}"];

        return Substitute(values, BuildPlaceholders(context));
    }

    /// <summary>游戏参数。1.13 之前是 minecraftArguments 单串，按空格切分。</summary>
    public static IReadOnlyList<string> BuildGame(LaunchContext context)
    {
        var values = Collect(context.Meta.Arguments, "game", context.ToRuleContext());
        if (values.Count == 0 && context.Meta.MinecraftArguments is { Length: > 0 } legacy)
            values = [.. legacy.Split(' ', StringSplitOptions.RemoveEmptyEntries)];

        return Substitute(values, BuildPlaceholders(context));
    }

    /// <summary>
    /// 从 arguments 的某个段里取出所有命中的条目。元素可以是字符串，
    /// 也可以是带 rules 与 value 的对象，value 允许是数组。
    /// </summary>
    public static List<string> Collect(JsonObject? arguments, string section, RuleContext rules)
    {
        var result = new List<string>();
        if (arguments?[section] is not JsonArray array) return result;

        foreach (var node in array)
        {
            switch (node)
            {
                case JsonValue value when value.TryGetValue<string>(out var text):
                    result.Add(text);
                    break;
                case JsonObject obj:
                {
                    var ruleList = ParseRules(obj["rules"] as JsonArray);
                    if (!RuleEvaluator.IsAllowed(ruleList, rules)) continue;
                    switch (obj["value"])
                    {
                        case JsonValue single when single.TryGetValue<string>(out var one):
                            result.Add(one);
                            break;
                        case JsonArray many:
                            result.AddRange(many
                                .Select(v => v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null)
                                .Where(s => s is not null)!);
                            break;
                    }

                    break;
                }
            }
        }

        return result;
    }

    public static IReadOnlyList<string> Substitute(IReadOnlyList<string> values,
        IReadOnlyDictionary<string, string> placeholders)
    {
        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            var replaced = value;
            foreach (var (key, replacement) in placeholders)
                replaced = replaced.Replace("${" + key + "}", replacement, StringComparison.Ordinal);
            result.Add(replaced);
        }

        return result;
    }

    public static Dictionary<string, string> BuildPlaceholders(LaunchContext context)
    {
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = context.Account.PlayerName,
            ["auth_uuid"] = context.Account.UuidCompact,
            ["auth_access_token"] = context.Account.AccessToken,
            ["auth_session"] = context.Account.AccessToken,
            ["user_type"] = context.Account.UserType,
            ["clientid"] = string.Empty,
            ["auth_xuid"] = string.Empty,
            ["version_name"] = context.Meta.Id,
            ["version_type"] = context.Meta.Type ?? "release",
            ["game_directory"] = context.GameDirectory,
            ["assets_root"] = context.AssetsRoot,
            ["assets_index_name"] = context.AssetsIndexName,
            ["natives_directory"] = context.NativesDirectory,
            ["classpath"] = string.Join(Path.PathSeparator, context.Classpath),
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["launcher_name"] = context.LauncherName,
            ["launcher_version"] = context.LauncherVersion,
            ["library_directory"] = Path.Combine(context.GameDirectory, "libraries"),
            ["primary_jar"] = Path.Combine(context.GameDirectory, "versions", context.Meta.Id,
                $"{context.Meta.Id}.jar")
        };

        if (context.ResolutionWidth is { } width) placeholders["resolution_width"] = width.ToString();
        if (context.ResolutionHeight is { } height) placeholders["resolution_height"] = height.ToString();
        return placeholders;
    }

    private static List<Rule> ParseRules(JsonArray? array)
    {
        var rules = new List<Rule>();
        if (array is null) return rules;

        foreach (var node in array.OfType<JsonObject>())
        {
            var osNode = node["os"] as JsonObject;
            var features = new Dictionary<string, bool>();
            if (node["features"] is JsonObject featureNode)
            {
                foreach (var (key, value) in featureNode)
                    if (value is JsonValue jv && jv.TryGetValue<bool>(out var flag))
                        features[key] = flag;
            }

            rules.Add(new Rule
            {
                Action = node["action"]?.GetValue<string>() ?? "allow",
                Os = osNode is null
                    ? null
                    : new OsRule
                    {
                        Name = osNode["name"]?.GetValue<string>(),
                        Arch = osNode["arch"]?.GetValue<string>()
                    },
                Features = features.Count > 0 ? features : null
            });
        }

        return rules;
    }
}
