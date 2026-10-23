namespace CCL.Core.Net;

/// <summary>
/// 一条镜像改写规则。仅替换主机名是不够的：BMCLAPI 的 maven 与 assets 需要额外路径前缀，
/// 直接换 host 会得到 404，所以前缀是规则的一部分。
/// </summary>
public sealed record MirrorRule(string SourceName, string OfficialHost, string MirrorBase, string PathPrefix = "")
{
    public bool Matches(Uri official) =>
        official.Host.Equals(OfficialHost, StringComparison.OrdinalIgnoreCase);

    public Uri Rewrite(Uri official)
    {
        var path = PathPrefix.TrimEnd('/') + official.AbsolutePath;
        var builder = new UriBuilder(MirrorBase) { Path = path, Query = official.Query.TrimStart('?') };
        return builder.Uri;
    }
}
