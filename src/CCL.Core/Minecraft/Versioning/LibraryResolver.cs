namespace CCL.Core.Minecraft.Versioning;

public sealed record ResolvedLibrary(string RelativePath, Artifact Artifact, bool ExtractNatives,
    IReadOnlyList<string> Exclude)
{
    public string FileName => Path.GetFileName(RelativePath);
}

/// <summary>
/// 把版本 JSON 里的 libraries 解析成实际需要落盘的文件列表。
/// 需要同时兼容两代 natives 表达：1.18 及更早用 natives 映射加 classifiers，解压到 natives 目录；
/// 1.19 起 natives 变成带 classifier 的独立库条目，jar 内部按平台子目录存放，由 LWJGL 直接从
/// classpath 读取，因此不应解压——是否解压只看库有没有显式声明 extract。
/// </summary>
public static class LibraryResolver
{
    public static IReadOnlyList<ResolvedLibrary> Resolve(IEnumerable<Library> libraries, RuleContext context)
    {
        var result = new List<ResolvedLibrary>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in libraries)
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, context)) continue;

            if (library.Natives is { Count: > 0 } &&
                library.Natives.TryGetValue(context.OsName, out var classifierKey))
            {
                var key = classifierKey.Replace("${arch}", context.OsArch);
                if (library.Downloads?.Classifiers is { } classifiers &&
                    classifiers.TryGetValue(key, out var nativeArtifact) &&
                    nativeArtifact.Url is { Length: > 0 })
                {
                    var path = nativeArtifact.Path ?? BuildPath(library.Name, key);
                    if (seen.Add(path))
                        result.Add(new ResolvedLibrary(path, nativeArtifact, true,
                            library.Extract?.Exclude ?? []));
                    continue;
                }
            }

            var artifact = library.Downloads?.Artifact;
            if (artifact?.Url is not { Length: > 0 }) continue;

            var relativePath = artifact.Path ?? BuildPath(library.Name, null);
            if (!seen.Add(relativePath)) continue;

            result.Add(new ResolvedLibrary(relativePath, artifact, library.Extract is not null,
                library.Extract?.Exclude ?? []));
        }

        return result;
    }

    private static string BuildPath(string name, string? classifierOverride)
    {
        var coordinate = MavenCoordinate.Parse(name);
        return classifierOverride is null
            ? coordinate.ToRelativePath()
            : (coordinate with { Classifier = classifierOverride }).ToRelativePath();
    }
}
