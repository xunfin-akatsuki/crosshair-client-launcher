using System.Net;
using CCL.Core.Hashing;
using CCL.Core.Net;

namespace CCL.Core.Tests;

public class MirrorRouterTests
{
    private static MirrorRouter Bmclapi(bool enabled = true) =>
        new(MirrorRouter.CreateBmclapiRules(), enabled);

    [Fact]
    public void OfficialComesFirstThenMirror()
    {
        var router = Bmclapi();
        var targets = router.Resolve(new Uri("https://launchermeta.mojang.com/mc/game/version_manifest.json"));

        Assert.Equal(2, targets.Count);
        Assert.False(targets[0].IsMirror);
        Assert.True(targets[1].IsMirror);
        Assert.Equal("https://bmclapi2.bangbang93.com/mc/game/version_manifest.json", targets[1].Uri.ToString());
    }

    [Fact]
    public void LibraryHostGetsMavenPrefix()
    {
        var router = Bmclapi();
        var targets = router.Resolve(new Uri("https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar"));

        Assert.Equal(2, targets.Count);
        Assert.Equal("https://bmclapi2.bangbang93.com/maven/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar",
            targets[1].Uri.ToString());
    }

    [Fact]
    public void AssetHostGetsAssetsPrefix()
    {
        var router = Bmclapi();
        var targets = router.Resolve(new Uri("https://resources.download.minecraft.net/ab/abcdef"));

        Assert.Equal("https://bmclapi2.bangbang93.com/assets/ab/abcdef", targets[1].Uri.ToString());
    }

    [Fact]
    public void UnknownHostHasNoMirror()
    {
        var router = Bmclapi();
        var targets = router.Resolve(new Uri("https://example.invalid/file.jar"));

        Assert.Single(targets);
        Assert.False(targets[0].IsMirror);
    }

    [Fact]
    public void DisabledMirrorsYieldOfficialOnly()
    {
        var router = Bmclapi(enabled: false);
        var targets = router.Resolve(new Uri("https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar"));

        Assert.Single(targets);
    }

    [Fact]
    public void ThreeConsecutiveFailuresPutSourceInCooldown()
    {
        var router = Bmclapi();
        var url = new Uri("https://launchermeta.mojang.com/mc/game/version_manifest.json");

        router.ReportFailure("BMCLAPI");
        router.ReportFailure("BMCLAPI");
        Assert.Equal(2, router.Resolve(url).Count);

        router.ReportFailure("BMCLAPI");
        Assert.True(router.IsCoolingDown("BMCLAPI"));
        Assert.Single(router.Resolve(url));
    }

    [Fact]
    public void SuccessResetsFailureCount()
    {
        var router = Bmclapi();
        router.ReportFailure("BMCLAPI");
        router.ReportFailure("BMCLAPI");
        router.ReportSuccess("BMCLAPI");
        router.ReportFailure("BMCLAPI");

        Assert.False(router.IsCoolingDown("BMCLAPI"));
    }
}

public class RetryPolicyTests
{
    [Theory]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(429, true)]
    [InlineData(408, true)]
    [InlineData(404, false)]
    [InlineData(403, false)]
    [InlineData(400, false)]
    public void RetryableStatusMatchesExpectation(int code, bool expected) =>
        Assert.Equal(expected, RetryPolicy.IsRetryableStatus((HttpStatusCode)code));

    [Fact]
    public void DelayGrowsWithAttemptsAndStaysCapped()
    {
        var random = new Random(42);
        var first = RetryPolicy.ComputeDelay(1, random: random);
        var third = RetryPolicy.ComputeDelay(3, random: random);
        var later = RetryPolicy.ComputeDelay(20, random: random);

        Assert.True(third > first);
        Assert.True(later <= TimeSpan.FromMilliseconds(8250));
    }

    [Fact]
    public void RetryAfterHintIsRespectedButCapped()
    {
        var delay = RetryPolicy.ComputeDelay(1, TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(5), delay);

        var capped = RetryPolicy.ComputeDelay(1, TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromSeconds(30), capped);
    }
}

public class FileFetcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccl-fetch-" + Guid.NewGuid().ToString("N"));

    public FileFetcherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ExistingFileWithMatchingHashSkipsWithoutAnyRequest()
    {
        var content = "hello world"u8.ToArray();
        var target = Path.Combine(_root, "a.txt");
        await File.WriteAllBytesAsync(target, content);
        var hash = await FileHasher.ComputeAsync(target, HashAlgorithmKind.Sha1);

        var transport = new FakeTransport();
        var fetcher = new FileFetcher(transport, new MirrorRouter([]));

        var outcome = await fetcher.FetchAsync(
            new DownloadItem(new Uri("https://example.invalid/a.txt"), target) { Hash = hash }, null);

        Assert.Equal(FetchStatus.Skipped, outcome.Status);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task ExistingFileWithWrongHashIsReplaced()
    {
        var target = Path.Combine(_root, "b.txt");
        await File.WriteAllBytesAsync(target, "stale"u8.ToArray());
        var expected = "fresh"u8.ToArray();
        var expectedHash = await HashBytesAsync(expected);

        var transport = new FakeTransport { Handler = (_, _) => FakeTransport.Bytes(expected) };
        var fetcher = new FileFetcher(transport, new MirrorRouter([]));

        var outcome = await fetcher.FetchAsync(
            new DownloadItem(new Uri("https://example.invalid/b.txt"), target) { Hash = expectedHash }, null);

        Assert.Equal(FetchStatus.Succeeded, outcome.Status);
        Assert.Equal(expected, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task BadContentFromOfficialFallsBackToMirror()
    {
        var good = "correct-content"u8.ToArray();
        var expectedHash = await HashBytesAsync(good);
        var target = Path.Combine(_root, "c.jar");

        var transport = new FakeTransport
        {
            Handler = (uri, _) => uri.Host.Contains("bangbang93", StringComparison.OrdinalIgnoreCase)
                ? FakeTransport.Bytes(good)
                : FakeTransport.Bytes("wrong"u8.ToArray())
        };
        var fetcher = new FileFetcher(transport,
            new MirrorRouter(MirrorRouter.CreateBmclapiRules()));
        var url = new Uri("https://libraries.minecraft.net/c.jar");

        var outcome = await fetcher.FetchAsync(
            new DownloadItem(url, target) { Hash = expectedHash }, null);

        Assert.Equal(FetchStatus.Succeeded, outcome.Status);
        Assert.Equal(good, await File.ReadAllBytesAsync(target));
        Assert.Contains(transport.Requests, r => r.Uri.Host.Equals("libraries.minecraft.net"));
        Assert.Contains(transport.Requests, r => r.Uri.Host.StartsWith("bmclapi2"));
    }

    [Fact]
    public async Task PartialFileIsResumedWithRangeRequest()
    {
        var full = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        var target = Path.Combine(_root, "d.bin");
        var part = target + ".part";
        await File.WriteAllBytesAsync(part, full[..1000]);

        var transport = new FakeTransport { Handler = (_, range) => FakeTransport.Ranged(full, range) };
        var fetcher = new FileFetcher(transport, new MirrorRouter([]));

        var outcome = await fetcher.FetchAsync(new DownloadItem(new Uri("https://example.invalid/d.bin"), target), null);

        Assert.Equal(FetchStatus.Succeeded, outcome.Status);
        Assert.Equal(1000L, transport.Requests[0].Range);
        Assert.Equal(full, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task HttpErrorIsReportedAsFailure()
    {
        var transport = new FakeTransport
        {
            Handler = (_, _) => FakeTransport.Bytes([], System.Net.HttpStatusCode.NotFound)
        };
        var fetcher = new FileFetcher(transport, new MirrorRouter([]));

        var outcome = await fetcher.FetchAsync(
            new DownloadItem(new Uri("https://example.invalid/e.txt"), Path.Combine(_root, "e.txt")), null);

        Assert.Equal(FetchStatus.Failed, outcome.Status);
    }

    private static async Task<string> HashBytesAsync(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        return await FileHasher.ComputeAsync(stream, HashAlgorithmKind.Sha1);
    }
}
