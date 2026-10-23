using System.Runtime.InteropServices;

namespace CCL.Core.Hashing;

/// <summary>
/// 内容寻址的对象库。同一哈希只保留一份实体文件，其余位置通过硬链接复用；
/// 跨卷时硬链接会失败，此时退化为复制，调用方不需要关心差别。
/// </summary>
public sealed class HashStore(string rootPath)
{
    public string RootPath { get; } = Path.GetFullPath(rootPath);

    public string GetObjectPath(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        var normalized = hash.Trim().ToLowerInvariant();
        return Path.Combine(RootPath, normalized[..2], normalized);
    }

    public bool Contains(string hash) => File.Exists(GetObjectPath(hash));

    /// <summary>把已有文件登记进对象库，返回对象路径；已存在同哈希时该文件被丢弃。</summary>
    public async Task<string> PutAsync(string sourceFilePath, string hash, CancellationToken token = default)
    {
        var objectPath = GetObjectPath(hash);
        if (File.Exists(objectPath))
        {
            TryDeleteFile(sourceFilePath);
            return objectPath;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
        await using (var source = File.OpenRead(sourceFilePath))
        await using (var target = new FileStream(objectPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         bufferSize: 128 * 1024, useAsync: true))
        {
            await source.CopyToAsync(target, 128 * 1024, token).ConfigureAwait(false);
        }

        TryDeleteFile(sourceFilePath);
        return objectPath;
    }

    /// <summary>
    /// 把对象库里的文件复用为目标文件。返回 false 表示库里没有该哈希。
    /// </summary>
    public bool TryMaterialize(string hash, string targetPath)
    {
        var objectPath = GetObjectPath(hash);
        if (!File.Exists(objectPath)) return false;

        var fullTarget = Path.GetFullPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullTarget)!);
        if (File.Exists(fullTarget)) File.Delete(fullTarget);

        if (!TryCreateHardLink(fullTarget, objectPath)) File.Copy(objectPath, fullTarget, overwrite: false);
        return true;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool TryCreateHardLink(string linkPath, string existingPath)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return CreateHardLinkW(linkPath, existingPath, IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
