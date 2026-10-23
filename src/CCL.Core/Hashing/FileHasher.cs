using System.Security.Cryptography;

namespace CCL.Core.Hashing;

public static class FileHasher
{
    public static async Task<string> ComputeAsync(string path, HashAlgorithmKind kind, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 128 * 1024, useAsync: true);
        return await ComputeAsync(stream, kind, token).ConfigureAwait(false);
    }

    public static async Task<string> ComputeAsync(Stream stream, HashAlgorithmKind kind,
        CancellationToken token = default)
    {
        using var hash = IncrementalHash.CreateHash(ToAlgorithm(kind));
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            hash.AppendData(buffer, 0, read);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static bool Matches(string? expected, string actual) =>
        !string.IsNullOrWhiteSpace(expected) &&
        string.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase);

    private static HashAlgorithmName ToAlgorithm(HashAlgorithmKind kind) => kind switch
    {
        HashAlgorithmKind.Sha1 => HashAlgorithmName.SHA1,
        HashAlgorithmKind.Sha256 => HashAlgorithmName.SHA256,
        HashAlgorithmKind.Sha512 => HashAlgorithmName.SHA512,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的哈希类型")
    };
}
