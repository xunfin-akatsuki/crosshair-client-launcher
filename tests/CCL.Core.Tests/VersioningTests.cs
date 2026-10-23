using System.IO.Compression;
using System.Text;
using CCL.Core.Hashing;
using CCL.Core.Layout;
using CCL.Core.Minecraft.Versioning;
using CCL.Core.Net;

namespace CCL.Core.Tests;

internal static class VersionFixtures
{
    /// <summary>格式贴近官方 1.21 的版本 JSON，同时覆盖两代 natives 表达与平台规则。</summary>
    public const string SampleJson = """
    {
      "id": "1.21.1-test",
      "type": "release",
      "mainClass": "net.minecraft.client.main.Main",
      "assets": "17",
      "assetIndex": {
        "id": "17",
        "sha1": "1111111111111111111111111111111111111111",
        "size": 120,
        "totalSize": 4096,
        "url": "https://piston-meta.mojang.com/v1/packages/17.json"
      },
      "downloads": {
        "client": {
          "sha1": "2222222222222222222222222222222222222222",
          "size": 8,
          "url": "https://piston-meta.mojang.com/v1/objects/client.jar"
        }
      },
      "logging": {
        "client": {
          "argument": "-Dlog4j.configurationFile=${path}",
          "type": "log4j2-xml",
          "file": {
            "id": "client-1.12.xml",
            "sha1": "3333333333333333333333333333333333333333",
            "size": 16,
            "url": "https://launchermeta.mojang.com/v1/logging/client-1.12.xml"
          }
        }
      },
      "javaVersion": { "component": "java-runtime-delta", "majorVersion": 21 },
      "libraries": [
        {
          "name": "org.lwjgl:lwjgl:3.3.3",
          "downloads": {
            "artifact": {
              "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar",
              "sha1": "4444444444444444444444444444444444444444",
              "size": 64,
              "url": "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar"
            }
          }
        },
        {
          "name": "org.lwjgl:lwjgl:3.3.3:natives-windows",
          "downloads": {
            "artifact": {
              "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar",
              "sha1": "5555555555555555555555555555555555555555",
              "size": 128,
              "url": "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar"
            }
          },
          "rules": [ { "action": "allow", "os": { "name": "windows" } } ]
        },
        {
          "name": "example:legacy-native:1.0",
          "downloads": {
            "classifiers": {
              "natives-windows": {
                "path": "example/legacy-native/1.0/legacy-native-1.0-natives-windows.jar",
                "sha1": "6666666666666666666666666666666666666666",
                "size": 96,
                "url": "https://libraries.minecraft.net/example/legacy-native/1.0/legacy-native-1.0-natives-windows.jar"
              }
            }
          },
          "natives": { "windows": "natives-windows" },
          "extract": { "exclude": [ "META-INF/" ] }
        },
        {
          "name": "example:only-on-mac:1",
          "downloads": {
            "artifact": {
              "path": "example/only-on-mac/1/only-on-mac-1.jar",
              "sha1": "7777777777777777777777777777777777777777",
              "size": 32,
              "url": "https://libraries.minecraft.net/example/only-on-mac/1/only-on-mac-1.jar"
            }
          },
          "rules": [ { "action": "allow", "os": { "name": "osx" } } ]
        },
        {
          "name": "example:not-on-linux:1",
          "downloads": {
            "artifact": {
              "path": "example/not-on-linux/1/not-on-linux-1.jar",
              "sha1": "8888888888888888888888888888888888888888",
              "size": 32,
              "url": "https://libraries.minecraft.net/example/not-on-linux/1/not-on-linux-1.jar"
            }
          },
          "rules": [ { "action": "allow" }, { "action": "disallow", "os": { "name": "linux" } } ]
        }
      ]
    }
    """;

    public static string Sha1(byte[] content)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(content));
    }

    public static byte[] CreateZip(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
        }

        return stream.ToArray();
    }

    public static string Utf8(string text) => text;
}

public class MavenCoordinateTests
{
    [Fact]
    public void ParsesPlainCoordinate()
    {
        var coordinate = MavenCoordinate.Parse("org.lwjgl:lwjgl:3.3.3");

        Assert.Equal("org.lwjgl", coordinate.Group);
        Assert.Equal("lwjgl", coordinate.Artifact);
        Assert.Equal("3.3.3", coordinate.Version);
        Assert.Null(coordinate.Classifier);
        Assert.Equal("org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar", coordinate.ToRelativePath());
    }

    [Fact]
    public void ParsesClassifierAndExtension()
    {
        var coordinate = MavenCoordinate.Parse("org.lwjgl:lwjgl:3.3.3:natives-windows@zip");

        Assert.Equal("natives-windows", coordinate.Classifier);
        Assert.Equal("zip", coordinate.Extension);
        Assert.Equal("org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.zip", coordinate.ToRelativePath());
    }

    [Fact]
    public void RejectsMalformedCoordinate() =>
        Assert.Throws<FormatException>(() => MavenCoordinate.Parse("not-a-coordinate"));
}

public class RuleEvaluatorTests
{
    private static readonly RuleContext Windows = new("windows", "x64");

    [Fact]
    public void NoRulesMeansAllowed() => Assert.True(RuleEvaluator.IsAllowed(null, Windows));

    [Fact]
    public void WindowsOnlyRuleRejectsLinux()
    {
        var rules = new List<Rule> { new() { Action = "allow", Os = new OsRule { Name = "windows" } } };

        Assert.True(RuleEvaluator.IsAllowed(rules, Windows));
        Assert.False(RuleEvaluator.IsAllowed(rules, new RuleContext("linux", "x64")));
    }

    [Fact]
    public void LaterRuleOverridesEarlierOne()
    {
        var rules = new List<Rule>
        {
            new() { Action = "allow" },
            new() { Action = "disallow", Os = new OsRule { Name = "linux" } }
        };

        Assert.True(RuleEvaluator.IsAllowed(rules, Windows));
        Assert.False(RuleEvaluator.IsAllowed(rules, new RuleContext("linux", "x64")));
    }

    [Fact]
    public void UnmatchedRulesMeanDisallowed()
    {
        var rules = new List<Rule> { new() { Action = "allow", Os = new OsRule { Name = "osx" } } };

        Assert.False(RuleEvaluator.IsAllowed(rules, Windows));
    }

    [Fact]
    public void FeatureRulesAreHonored()
    {
        var rules = new List<Rule>
        {
            new() { Action = "allow", Features = new Dictionary<string, bool> { ["has_custom_resolution"] = true } }
        };

        Assert.False(RuleEvaluator.IsAllowed(rules, Windows));
        Assert.True(RuleEvaluator.IsAllowed(rules,
            new RuleContext("windows", "x64", new Dictionary<string, bool> { ["has_custom_resolution"] = true })));
    }
}

public class LibraryResolverTests
{
    private static readonly RuleContext Windows = new("windows", "x64");

    [Fact]
    public void ResolvesModernAndLegacyNatives()
    {
        var meta = VersionMeta.Parse(VersionFixtures.SampleJson);
        var libraries = LibraryResolver.Resolve(meta.Libraries, Windows);

        var plain = Assert.Single(libraries, l => l.RelativePath.Contains("lwjgl-3.3.3.jar"));
        Assert.False(plain.ExtractNatives);

        var modernNative = Assert.Single(libraries, l => l.RelativePath.Contains("lwjgl-3.3.3-natives-windows.jar"));
        Assert.False(modernNative.ExtractNatives); // 1.19+ 的 natives 由 LWJGL 从 classpath 读取，不解压

        var legacyNative = Assert.Single(libraries, l => l.RelativePath.Contains("legacy-native-1.0-natives-windows.jar"));
        Assert.True(legacyNative.ExtractNatives);
        Assert.Contains("META-INF/", legacyNative.Exclude);
    }

    [Fact]
    public void PlatformRulesFilterLibraries()
    {
        var meta = VersionMeta.Parse(VersionFixtures.SampleJson);
        var libraries = LibraryResolver.Resolve(meta.Libraries, Windows);

        Assert.DoesNotContain(libraries, l => l.RelativePath.Contains("only-on-mac"));
        // 先 allow 后 disallow linux：在 windows 上仍然保留
        Assert.Contains(libraries, l => l.RelativePath.Contains("not-on-linux"));
        Assert.DoesNotContain(
            LibraryResolver.Resolve(meta.Libraries, new RuleContext("linux", "x64")),
            l => l.RelativePath.Contains("not-on-linux"));
    }

    [Fact]
    public void DuplicatePathsAreCollapsed()
    {
        var meta = VersionMeta.Parse("""
        {
          "id": "dup",
          "libraries": [
            { "name": "a:b:1", "downloads": { "artifact": { "path": "a/b/1/b-1.jar", "url": "https://x/1.jar" } } },
            { "name": "a:b:1", "downloads": { "artifact": { "path": "a/b/1/b-1.jar", "url": "https://x/1.jar" } } }
          ]
        }
        """);

        var libraries = LibraryResolver.Resolve(meta.Libraries, Windows);

        Assert.Single(libraries);
    }
}

public class InstallPlannerTests
{
    private static CclLayout Layout(string root) => new(root, Path.Combine(root, ".minecraft"));

    [Fact]
    public void CorePlanContainsClientIndexLoggingAndLibraries()
    {
        var layout = Layout(Path.Combine(Path.GetTempPath(), "ccl-plan-" + Guid.NewGuid().ToString("N")));
        var meta = VersionMeta.Parse(VersionFixtures.SampleJson);

        var plan = InstallPlanner.BuildCore(meta, layout, new InstallOptions { RuleContext = new RuleContext("windows", "x64") });

        Assert.Equal(Path.Combine(layout.VersionsPath, "1.21.1-test", "1.21.1-test.jar"), plan.ClientJarPath);
        Assert.Contains(plan.Files, f => f.Item.TargetPath == plan.ClientJarPath);
        Assert.Contains(plan.Files, f => f.Item.TargetPath.EndsWith("17.json"));
        Assert.Contains(plan.Files, f => f.Item.TargetPath.EndsWith("client-1.12.xml"));
        Assert.Contains(plan.Files, f => f.TargetPath.Contains("lwjgl-3.3.3.jar"));
        Assert.Contains(plan.Files, f => f.ExtractNatives);
        Assert.DoesNotContain(plan.Files, f => f.TargetPath.Contains("only-on-mac"));
    }

    [Fact]
    public void AssetFilesUseHashLayoutAndVirtualCopies()
    {
        var layout = Layout(Path.Combine(Path.GetTempPath(), "ccl-asset-" + Guid.NewGuid().ToString("N")));
        var meta = VersionMeta.Parse(VersionFixtures.SampleJson);
        var plan = InstallPlanner.BuildCore(meta, layout, new InstallOptions { RuleContext = new RuleContext("windows", "x64") });

        var index = AssetIndex.Parse("""
        {
          "virtual": true,
          "objects": {
            "minecraft/sounds/a.ogg": { "hash": "abcdef0123456789abcdef0123456789abcdef01", "size": 42 }
          }
        }
        """);

        InstallPlanner.AddAssetFiles(plan, index);

        var file = Assert.Single(plan.Files, f => f.TargetPath.Contains("abcdef0123"));
        Assert.EndsWith(Path.Combine("assets", "objects", "ab", "abcdef0123456789abcdef0123456789abcdef01"),
            file.TargetPath);
        Assert.Equal("https://resources.download.minecraft.net/ab/abcdef0123456789abcdef0123456789abcdef01",
            file.Item.Url.ToString());
        Assert.Single(plan.VirtualAssets);
        Assert.Contains(Path.Combine("assets", "virtual", "legacy"),
            plan.VirtualAssets[0].TargetPath);
    }
}
