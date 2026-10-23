namespace CCL.Core.Minecraft.Versioning;

/// <summary>
/// Maven 坐标与相对路径互转。支持 group:artifact:version[:classifier][@ext] 形式，
/// 缺省扩展名为 jar。
/// </summary>
public sealed record MavenCoordinate(string Group, string Artifact, string Version, string? Classifier, string Extension)
{
    public static MavenCoordinate Parse(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var extension = "jar";
        var at = name.LastIndexOf('@');
        if (at > 0)
        {
            extension = name[(at + 1)..];
            name = name[..at];
        }

        var parts = name.Split(':');
        if (parts.Length < 3)
            throw new FormatException($"不是合法的 Maven 坐标：{name}");

        return new MavenCoordinate(
            parts[0],
            parts[1],
            parts[2],
            parts.Length >= 4 && parts[3].Length > 0 ? parts[3] : null,
            extension);
    }

    /// <summary>相对于 libraries 根目录的路径。</summary>
    public string ToRelativePath()
    {
        var file = Classifier is null
            ? $"{Artifact}-{Version}.{Extension}"
            : $"{Artifact}-{Version}-{Classifier}.{Extension}";
        return Path.Combine(Group.Replace('.', '/'), Artifact, Version, file).Replace('\\', '/');
    }

    public override string ToString() =>
        Classifier is null
            ? $"{Group}:{Artifact}:{Version}"
            : $"{Group}:{Artifact}:{Version}:{Classifier}";
}
