using CCL.Core.Localization;
namespace CCL.Core.Minecraft.Instances;

/// <summary>
/// 实例目录的增删。删除只针对 versions 下的单个版本目录，
/// libraries 与 assets 是跨实例共享的，不会被牵连。
/// </summary>
public static class InstanceManager
{
    /// <summary>删除指定实例目录。目录不存在视为已删除（幂等）。</summary>
    public static InstanceDeleteResult Delete(InstanceInfo instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (!Directory.Exists(instance.Directory))
            return new InstanceDeleteResult(instance.Id, true, null);

        try
        {
            Directory.Delete(instance.Directory, recursive: true);
            return new InstanceDeleteResult(instance.Id, true, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new InstanceDeleteResult(instance.Id, true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new InstanceDeleteResult(instance.Id, false, ex.Message);
        }
    }

    public static long GetSizeBytes(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        try
        {
            return Directory
                .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(path =>
                {
                    try
                    {
                        return new FileInfo(path).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return 0L;
                    }
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

public sealed record InstanceDeleteResult(string InstanceId, bool Succeeded, string? Error)
{
    public string Describe() => Succeeded
        ? CoreText.Format("Account.DeleteOk", InstanceId)
        : CoreText.Format("Account.DeleteFailed", InstanceId, Error ?? string.Empty);
}
