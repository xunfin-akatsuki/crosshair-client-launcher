using CCL.Core.Launching;
using CCL.Core.Layout;
using CCL.Core.Minecraft.Versioning;

namespace CCL.Core.Tests;

public class GameAccountTests
{
    [Theory]
    [InlineData("Notch", "b50ad385-829d-3141-a216-7e7d7539ba7f")]
    [InlineData("CCL", "f72fd600-1315-35b8-85e0-73e7a9c13582")]
    public void OfflineUuidMatchesJavaAlgorithm(string name, string expected) =>
        Assert.Equal(expected, GameAccount.ComputeOfflineUuid(name));

    [Fact]
    public void OfflineUuidIsStableAndVersion3()
    {
        var first = GameAccount.Offline("Player");
        var second = GameAccount.Offline("Player");

        Assert.Equal(first.Uuid, second.Uuid);
        Assert.Equal('3', first.Uuid[14]); // version 位
        Assert.Contains(first.Uuid[19], "89ab"); // variant 位
        Assert.Equal(32, first.UuidCompact.Length);
    }

    [Fact]
    public void EmptyNameIsRejected() => Assert.Throws<ArgumentException>(() => GameAccount.Offline(" "));
}

public class JavaLocatorTests
{
    [Theory]
    [InlineData("21.0.12", 21)]
    [InlineData("17", 17)]
    [InlineData("1.8.0_412", 8)]
    [InlineData("jdk-21.0.12.101-hotspot", 0)]
    public void MajorVersionParsing(string input, int expected) =>
        Assert.Equal(expected, JavaLocator.ParseMajorVersion(input));

    [Fact]
    public void SelectPrefersExactMajor()
    {
        var runtimes = new List<JavaRuntime>
        {
            new("a", "a", 17, "x64"),
            new("b", "b", 21, "x64"),
            new("c", "c", 22, "x64")
        };

        Assert.Equal("b", JavaLocator.Select(runtimes, 21)?.ExecutablePath);
    }

    [Fact]
    public void SelectFallsBackToSmallestSufficientMajor()
    {
        var runtimes = new List<JavaRuntime>
        {
            new("a", "a", 17, "x64"),
            new("c", "c", 22, "x64"),
            new("d", "d", 24, "x64")
        };

        Assert.Equal("c", JavaLocator.Select(runtimes, 21)?.ExecutablePath);
    }

    [Fact]
    public void SelectReturnsNullWhenNothingIsNewEnough()
    {
        var runtimes = new List<JavaRuntime> { new("a", "a", 17, "x64") };

        Assert.Null(JavaLocator.Select(runtimes, 21));
    }

    [Fact]
    public void DiscoverFindsInstalledRuntimes()
    {
        var runtimes = JavaLocator.Discover();

        // 断言只检查结果自身的自洽性，不假设宿主机装了 Java（CI runner 通常没有）
        Assert.All(runtimes, r => Assert.True(r.MajorVersion > 0));
        Assert.All(runtimes, r => Assert.True(File.Exists(r.ExecutablePath)));
        Assert.All(runtimes, r => Assert.True(Directory.Exists(r.Home)));
        Assert.Equal(runtimes.Count, runtimes.Select(r => r.ExecutablePath).Distinct().Count());
        Assert.Equal(runtimes.OrderByDescending(r => r.MajorVersion).Select(r => r.MajorVersion),
            runtimes.Select(r => r.MajorVersion));
    }
}

public class ArgumentBuilderTests
{
    private static readonly RuleContext WindowsRules = new("windows", "x64");

    private static LaunchContext Context(VersionMeta meta) => new()
    {
        Account = GameAccount.Offline("TestPlayer"),
        Meta = meta,
        GameDirectory = @"C:\game",
        AssetsRoot = @"C:\game\assets",
        AssetsIndexName = "17",
        NativesDirectory = @"C:\game\versions\x\natives",
        Classpath = [@"C:\a.jar", @"C:\b.jar"]
    };

    [Fact]
    public void OsSpecificJvmArgumentIsFilteredByRules()
    {
        var meta = VersionMeta.Parse("""
        {
          "id": "x",
          "arguments": {
            "jvm": [
              { "rules": [ { "action": "allow", "os": { "name": "osx" } } ], "value": [ "-XstartOnFirstThread" ] },
              { "rules": [ { "action": "allow", "os": { "name": "windows" } } ], "value": [ "-XX:HeapDumpPath=x.heapdump" ] },
              "-Xss1M",
              "-cp",
              "${classpath}"
            ]
          }
        }
        """);

        var jvm = ArgumentBuilder.BuildJvm(Context(meta));

        Assert.DoesNotContain("-XstartOnFirstThread", jvm);
        Assert.Contains("-XX:HeapDumpPath=x.heapdump", jvm);
        Assert.Contains("-Xss1M", jvm);
        Assert.Equal(string.Join(Path.PathSeparator, @"C:\a.jar", @"C:\b.jar"), jvm[^1]);
    }

    [Fact]
    public void PlaceholdersAreSubstituted()
    {
        var meta = VersionMeta.Parse("""
        {
          "id": "1.21.1",
          "type": "release",
          "arguments": {
            "game": [
              "--username", "${auth_player_name}",
              "--version", "${version_name}",
              "--gameDir", "${game_directory}",
              "--assetsDir", "${assets_root}",
              "--assetIndex", "${assets_index_name}",
              "--uuid", "${auth_uuid}",
              "--accessToken", "${auth_access_token}",
              "--userType", "${user_type}",
              "--versionType", "${version_type}"
            ]
          }
        }
        """);
        var context = Context(meta);

        var game = ArgumentBuilder.BuildGame(context);

        Assert.Equal("TestPlayer", game[1]);
        Assert.Equal("1.21.1", game[3]);
        Assert.Equal(@"C:\game", game[5]);
        Assert.Equal("17", game[9]);
        Assert.Equal(context.Account.UuidCompact, game[11]);
        Assert.Equal("legacy", game[15]);
        Assert.DoesNotContain(game, a => a.Contains("${", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyMinecraftArgumentsAreSplit()
    {
        var meta = VersionMeta.Parse("""
        { "id": "1.7.10", "minecraftArguments": "--username ${auth_player_name} --gameDir ${game_directory} --demo" }
        """);

        var game = ArgumentBuilder.BuildGame(Context(meta));

        Assert.Equal(["--username", "TestPlayer", "--gameDir", @"C:\game", "--demo"], game);
    }

    [Fact]
    public void FeatureRulesGateResolutionArguments()
    {
        var meta = VersionMeta.Parse("""
        {
          "id": "x",
          "arguments": {
            "game": [
              { "rules": [ { "action": "allow", "features": { "has_custom_resolution": true } } ],
                "value": [ "--width", "${resolution_width}", "--height", "${resolution_height}" ] }
            ]
          }
        }
        """);

        Assert.Empty(ArgumentBuilder.BuildGame(Context(meta)));

        var withResolution = new LaunchContext
        {
            Account = GameAccount.Offline("TestPlayer"),
            Meta = meta,
            GameDirectory = @"C:\game",
            AssetsRoot = @"C:\game\assets",
            AssetsIndexName = "17",
            NativesDirectory = @"C:\game\versions\x\natives",
            Classpath = [],
            ResolutionWidth = 1280,
            ResolutionHeight = 720
        };

        var game = ArgumentBuilder.BuildGame(withResolution);
        Assert.Equal(["--width", "1280", "--height", "720"], game);
    }
}

public class GameLauncherTests
{
    [Fact]
    public void BuildProducesCommandWithMainClassAndClasspath()
    {
        var root = Path.Combine(Path.GetTempPath(), "ccl-launch-" + Guid.NewGuid().ToString("N"));
        var layout = new CclLayout(root, Path.Combine(root, ".minecraft"));
        layout.EnsureCreated();
        var versionDirectory = Path.Combine(layout.VersionsPath, "1.21.1");
        Directory.CreateDirectory(versionDirectory);

        var libraryDir = Path.Combine(layout.LibrariesPath, "org", "lwjgl", "lwjgl", "3.3.3");
        Directory.CreateDirectory(libraryDir);
        File.WriteAllText(Path.Combine(libraryDir, "lwjgl-3.3.3.jar"), string.Empty);
        File.WriteAllText(Path.Combine(versionDirectory, "1.21.1.jar"), string.Empty);

        var meta = VersionMeta.Parse("""
        {
          "id": "1.21.1",
          "mainClass": "net.minecraft.client.main.Main",
          "assets": "17",
          "arguments": {
            "jvm": [ "-cp", "${classpath}" ],
            "game": [ "--username", "${auth_player_name}" ]
          },
          "libraries": [
            { "name": "org.lwjgl:lwjgl:3.3.3",
              "downloads": { "artifact": { "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar", "url": "https://x/1.jar" } } }
          ]
        }
        """);

        var launcher = new GameLauncher();
        var command = launcher.Build(meta, new JavaRuntime("java.exe", "home", 21, "x64"),
            GameAccount.Offline("Player"), new LaunchOptions { Layout = layout, VersionId = "1.21.1" });

        try
        {
            Assert.Equal("java.exe", command.Executable);
            Assert.Equal(Path.Combine(root, ".minecraft"), command.WorkingDirectory);
            Assert.Contains("-Xmx4096M", command.Arguments);
            Assert.Contains("net.minecraft.client.main.Main", command.Arguments);
            Assert.Contains("--username", command.Arguments);
            Assert.Contains("Player", command.Arguments);

            var arguments = command.Arguments.ToList();
            var classpath = arguments[arguments.IndexOf("-cp") + 1];
            Assert.Contains("lwjgl-3.3.3.jar", classpath);
            Assert.Contains("1.21.1.jar", classpath);
            Assert.Contains(Path.PathSeparator, classpath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingMainClassIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "ccl-launch-" + Guid.NewGuid().ToString("N"));
        var layout = new CclLayout(root, Path.Combine(root, ".minecraft"));
        layout.EnsureCreated();

        try
        {
            var meta = VersionMeta.Parse("""{ "id": "broken" }""");
            var launcher = new GameLauncher();

            Assert.Throws<InvalidOperationException>(() => launcher.Build(meta,
                new JavaRuntime("java.exe", "home", 21, "x64"), GameAccount.Offline("Player"),
                new LaunchOptions { Layout = layout, VersionId = "broken" }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
