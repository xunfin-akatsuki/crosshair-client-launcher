using CCL.Core.Configuration;
using CCL.Core.Layout;

namespace CCL.Core.Tests;

public class CclLayoutTests
{
    [Fact]
    public void PortableMarkerKeepsDataNextToExecutable()
    {
        var appDir = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(appDir, "ccl.portable"), string.Empty);

            var layout = CclLayout.CreateDefault(appDir);

            Assert.Equal(Path.GetFullPath(appDir), layout.RootPath);
            Assert.Equal(Path.Combine(appDir, ".minecraft"), layout.MinecraftPath);
        }
        finally
        {
            Directory.Delete(appDir, recursive: true);
        }
    }

    [Fact]
    public void WithoutMarkerDataGoesToRoamingAppData()
    {
        var appDir = CreateTempDirectory();
        try
        {
            var layout = CclLayout.CreateDefault(appDir);

            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CCL");
            Assert.Equal(expected, layout.RootPath);
        }
        finally
        {
            Directory.Delete(appDir, recursive: true);
        }
    }

    [Fact]
    public void EnsureCreatedBuildsSubdirectories()
    {
        var root = CreateTempDirectory();
        try
        {
            var layout = new CclLayout(root, Path.Combine(root, ".minecraft"));
            layout.EnsureCreated();

            Assert.True(Directory.Exists(layout.VersionsPath));
            Assert.True(Directory.Exists(layout.LibrariesPath));
            Assert.True(Directory.Exists(layout.AssetsPath));
            Assert.True(Directory.Exists(layout.LogsPath));
            Assert.True(Directory.Exists(layout.CachePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ccl-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

public class CclSettingsTests
{
    [Fact]
    public void SaveThenLoadRoundTripsValues()
    {
        var path = Path.Combine(Path.GetTempPath(), "ccl-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new CclSettings
            {
                MaxConcurrentDownloads = 16,
                MaxMemoryMb = 8192,
                LastSelectedVersion = "1.21.1",
                UseMirror = false
            };
            settings.Save(path);

            var loaded = CclSettings.Load(path);

            Assert.Equal(16, loaded.MaxConcurrentDownloads);
            Assert.Equal(8192, loaded.MaxMemoryMb);
            Assert.Equal("1.21.1", loaded.LastSelectedVersion);
            Assert.False(loaded.UseMirror);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BrokenFileIsBackedUpAndDefaultsReturned()
    {
        var path = Path.Combine(Path.GetTempPath(), "ccl-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{ this is not json");

            var loaded = CclSettings.Load(path);

            Assert.Equal(8, loaded.MaxConcurrentDownloads);
            Assert.True(File.Exists(path + ".broken"));
        }
        finally
        {
            File.Delete(path);
            if (File.Exists(path + ".broken")) File.Delete(path + ".broken");
        }
    }

    [Fact]
    public void OutOfRangeValuesAreNormalized()
    {
        var settings = new CclSettings { MaxConcurrentDownloads = 0, MaxMemoryMb = 64 };

        settings.Normalize();

        Assert.Equal(8, settings.MaxConcurrentDownloads);
        Assert.Equal(4096, settings.MaxMemoryMb);
    }

    [Fact]
    public void MissingFileYieldsDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "ccl-missing-" + Guid.NewGuid().ToString("N") + ".json");

        var loaded = CclSettings.Load(path);

        Assert.Equal(8, loaded.MaxConcurrentDownloads);
        Assert.True(loaded.UseMirror);
    }
}
