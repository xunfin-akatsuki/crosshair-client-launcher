using CCL.Core.Hashing;
using CCL.Core.Net;

namespace CCL.Core.Tests;

public class DownloadBatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccl-batch-" + Guid.NewGuid().ToString("N"));

    public DownloadBatchTests() => Directory.CreateDirectory(_root);

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
    public async Task DuplicateTargetsAreFetchedOnce()
    {
        var transport = new FakeTransport { Handler = (_, _) => FakeTransport.Bytes("x"u8.ToArray()) };
        var batch = new DownloadBatch(new FileFetcher(transport, new MirrorRouter([])), maxConcurrency: 4);
        var target = Path.Combine(_root, "dup.txt");

        var result = await batch.RunAsync(
        [
            new DownloadItem(new Uri("https://example.invalid/1"), target),
            new DownloadItem(new Uri("https://example.invalid/2"), target)
        ], null);

        Assert.Equal(1, result.Succeeded);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task ConcurrencyLimitIsRespected()
    {
        var transport = new FakeTransport
        {
            Delay = TimeSpan.FromMilliseconds(30),
            Handler = (_, _) => FakeTransport.Bytes("y"u8.ToArray())
        };
        var batch = new DownloadBatch(new FileFetcher(transport, new MirrorRouter([])), maxConcurrency: 3);

        var items = Enumerable.Range(0, 12)
            .Select(i => new DownloadItem(new Uri("https://example.invalid/" + i), Path.Combine(_root, $"f{i}.txt")))
            .ToList();

        var result = await batch.RunAsync(items, null);

        Assert.Equal(12, result.Succeeded);
        Assert.True(transport.MaxInFlight <= 3, $"实际并发 {transport.MaxInFlight} 超过上限 3");
    }

    [Fact]
    public async Task FailuresAreCollectedWithoutStoppingOthers()
    {
        var transport = new FakeTransport
        {
            Handler = (uri, _) => uri.AbsolutePath.Contains("bad", StringComparison.Ordinal)
                ? FakeTransport.Bytes([], System.Net.HttpStatusCode.NotFound)
                : FakeTransport.Bytes("ok"u8.ToArray())
        };
        var batch = new DownloadBatch(new FileFetcher(transport, new MirrorRouter([])), maxConcurrency: 4);

        var result = await batch.RunAsync(
        [
            new DownloadItem(new Uri("https://example.invalid/bad"), Path.Combine(_root, "bad.txt")),
            new DownloadItem(new Uri("https://example.invalid/good"), Path.Combine(_root, "good.txt"))
        ], null);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.False(result.AllSucceeded);
        Assert.True(File.Exists(Path.Combine(_root, "good.txt")));
    }

    [Fact]
    public async Task ProgressReachesCompleteFraction()
    {
        var payload = new byte[512];
        var transport = new FakeTransport { Handler = (_, _) => FakeTransport.Bytes(payload) };
        var batch = new DownloadBatch(new FileFetcher(transport, new MirrorRouter([])));

        var updates = new List<BatchProgress>();
        var progress = new CollectingProgress(updates);

        await batch.RunAsync(
            [new DownloadItem(new Uri("https://example.invalid/p"), Path.Combine(_root, "p.bin")) { Size = payload.Length }],
            progress);

        Assert.NotEmpty(updates);
        Assert.Equal(1d, updates[^1].Fraction, 3);
    }

    [Fact]
    public async Task CancellationStopsPromptly()
    {
        var transport = new FakeTransport
        {
            Delay = TimeSpan.FromSeconds(5),
            Handler = (_, _) => FakeTransport.Bytes("z"u8.ToArray())
        };
        var batch = new DownloadBatch(new FileFetcher(transport, new MirrorRouter([])));
        using var cts = new CancellationTokenSource(100);

        var items = Enumerable.Range(0, 5)
            .Select(i => new DownloadItem(new Uri("https://example.invalid/" + i), Path.Combine(_root, $"c{i}.txt")))
            .ToList();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch.RunAsync(items, null, cts.Token));
    }

    private sealed class CollectingProgress(List<BatchProgress> sink) : IProgress<BatchProgress>
    {
        public void Report(BatchProgress value) => sink.Add(value);
    }
}

public class HashStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccl-store-" + Guid.NewGuid().ToString("N"));

    public HashStoreTests() => Directory.CreateDirectory(_root);

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
    public async Task PutStoresUnderTwoCharPrefix()
    {
        var store = new HashStore(Path.Combine(_root, "objects"));
        var source = Path.Combine(_root, "src.bin");
        await File.WriteAllBytesAsync(source, "payload"u8.ToArray());
        var hash = await FileHasher.ComputeAsync(source, HashAlgorithmKind.Sha1);

        var objectPath = await store.PutAsync(source, hash);

        Assert.True(File.Exists(objectPath));
        Assert.Equal(hash[..2], Path.GetFileName(Path.GetDirectoryName(objectPath)));
        Assert.StartsWith(hash, Path.GetFileName(objectPath));
        Assert.True(store.Contains(hash));
    }

    [Fact]
    public async Task MaterializeCopiesContentToTarget()
    {
        var store = new HashStore(Path.Combine(_root, "objects"));
        var source = Path.Combine(_root, "src2.bin");
        var payload = "dedup-me"u8.ToArray();
        await File.WriteAllBytesAsync(source, payload);
        var hash = await FileHasher.ComputeAsync(source, HashAlgorithmKind.Sha1);
        await store.PutAsync(source, hash);

        var target = Path.Combine(_root, "nested", "copy.bin");
        var ok = store.TryMaterialize(hash, target);

        Assert.True(ok);
        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public void MaterializeReturnsFalseForUnknownHash()
    {
        var store = new HashStore(Path.Combine(_root, "objects"));
        Assert.False(store.TryMaterialize(new string('a', 40), Path.Combine(_root, "x.bin")));
    }
}
