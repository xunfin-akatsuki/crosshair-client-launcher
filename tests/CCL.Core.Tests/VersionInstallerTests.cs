using CCL.Core.Layout;
using CCL.Core.Minecraft.Versioning;
using CCL.Core.Net;

namespace CCL.Core.Tests;

public class VersionInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccl-install-" + Guid.NewGuid().ToString("N"));
    private readonly CclLayout _layout;

    public VersionInstallerTests()
    {
        Directory.CreateDirectory(_root);
        _layout = new CclLayout(_root, Path.Combine(_root, ".minecraft"));
        _layout.EnsureCreated();
    }

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
    public async Task InstallWritesClientJsonAndExtractsNatives()
    {
        var nativeZip = VersionFixtures.CreateZip(("libtest.dll", "binary"u8.ToArray()),
            ("META-INF/MANIFEST.MF", "manifest"u8.ToArray()));
        var clientJar = "client-jar-content"u8.ToArray();
        var meta = BuildSyntheticMeta(clientJar, nativeZip);

        var transport = new FakeTransport
        {
            Handler = (uri, _) => uri.AbsolutePath.EndsWith("client.jar", StringComparison.Ordinal)
                ? FakeTransport.Bytes(clientJar)
                : uri.AbsolutePath.EndsWith("natives.jar", StringComparison.Ordinal)
                    ? FakeTransport.Bytes(nativeZip)
                    : FakeTransport.Bytes("{}"u8.ToArray())
        };
        var installer = new VersionInstaller(new FileFetcher(transport, new MirrorRouter([])));

        var result = await installer.InstallAsync(meta, _layout,
            new InstallOptions
            {
                DownloadAssets = false,
                DownloadLogging = false,
                RuleContext = new RuleContext("windows", "x64")
            });

        Assert.True(result.AllSucceeded, string.Join("; ", result.Failures.Select(f => f.Reason)));

        var versionDir = Path.Combine(_layout.VersionsPath, meta.Id);
        Assert.True(File.Exists(Path.Combine(versionDir, $"{meta.Id}.jar")));
        Assert.True(File.Exists(Path.Combine(versionDir, $"{meta.Id}.json")));

        var nativesDir = Path.Combine(versionDir, "natives");
        Assert.True(File.Exists(Path.Combine(nativesDir, "libtest.dll")));
        Assert.False(File.Exists(Path.Combine(nativesDir, "META-INF", "MANIFEST.MF")));
        Assert.True(result.NativesExtracted >= 1);
    }

    [Fact]
    public async Task SecondRunSkipsEverything()
    {
        var clientJar = "client-jar-content"u8.ToArray();
        var nativeZip = VersionFixtures.CreateZip(("libtest.dll", "binary"u8.ToArray()));
        var meta = BuildSyntheticMeta(clientJar, nativeZip);

        var transport = new FakeTransport
        {
            Handler = (uri, _) => uri.AbsolutePath.EndsWith("natives.jar", StringComparison.Ordinal)
                ? FakeTransport.Bytes(nativeZip)
                : FakeTransport.Bytes(clientJar)
        };
        var installer = new VersionInstaller(new FileFetcher(transport, new MirrorRouter([])));
        var options = new InstallOptions
        {
            DownloadAssets = false,
            DownloadLogging = false,
            RuleContext = new RuleContext("windows", "x64")
        };

        await installer.InstallAsync(meta, _layout, options);
        var requestsAfterFirst = transport.Requests.Count;
        var second = await installer.InstallAsync(meta, _layout, options);

        Assert.Equal(0, second.Downloaded);
        Assert.Equal(requestsAfterFirst, transport.Requests.Count);
        Assert.True(second.Skipped >= 1);
    }

    [Fact]
    public async Task VerifyDetectsMissingAndCorruptedFiles()
    {
        var clientJar = "client-jar-content"u8.ToArray();
        var nativeZip = VersionFixtures.CreateZip(("libtest.dll", "binary"u8.ToArray()));
        var meta = BuildSyntheticMeta(clientJar, nativeZip);
        var transport = new FakeTransport
        {
            Handler = (uri, _) => uri.AbsolutePath.EndsWith("natives.jar", StringComparison.Ordinal)
                ? FakeTransport.Bytes(nativeZip)
                : FakeTransport.Bytes(clientJar)
        };
        var installer = new VersionInstaller(new FileFetcher(transport, new MirrorRouter([])));
        var options = new InstallOptions
        {
            DownloadAssets = false,
            DownloadLogging = false,
            RuleContext = new RuleContext("windows", "x64")
        };
        await installer.InstallAsync(meta, _layout, options);

        var plan = InstallPlanner.BuildCore(meta, _layout, options);
        var clean = await installer.VerifyAsync(plan);
        Assert.Equal(0, clean.Missing);
        Assert.Equal(0, clean.Corrupted);
        Assert.True(clean.Ok >= 2);

        await File.WriteAllTextAsync(plan.ClientJarPath, "tampered");
        File.Delete(plan.Files.First(f => f.ExtractNatives).TargetPath);

        var broken = await installer.VerifyAsync(plan);
        Assert.Equal(1, broken.Corrupted);
        Assert.Equal(1, broken.Missing);
        Assert.False(broken.IsComplete);
    }

    /// <summary>构造一个只含客户端 jar 与一个原生库的版本，URL 全部指向假传输层。</summary>
    private static VersionMeta BuildSyntheticMeta(byte[] clientJar, byte[] nativeZip)
    {
        var clientHash = VersionFixtures.Sha1(clientJar);
        var nativeHash = VersionFixtures.Sha1(nativeZip);
        var json = $$"""
        {
          "id": "synthetic-1.0",
          "mainClass": "net.minecraft.client.main.Main",
          "downloads": {
            "client": { "sha1": "{{clientHash}}", "size": {{clientJar.Length}},
                        "url": "https://piston-meta.mojang.com/v1/objects/client.jar" }
          },
          "libraries": [
            {
              "name": "test:natives:1.0:natives-windows",
              "downloads": {
                "artifact": {
                  "path": "test/natives/1.0/natives-1.0-natives-windows.jar",
                  "sha1": "{{nativeHash}}",
                  "size": {{nativeZip.Length}},
                  "url": "https://libraries.minecraft.net/test/natives/1.0/natives.jar"
                }
              },
              "extract": { "exclude": [ "META-INF/" ] },
              "rules": [ { "action": "allow", "os": { "name": "windows" } } ]
            }
          ]
        }
        """;

        return VersionMeta.Parse(json);
    }
}
