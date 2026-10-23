using CCL.Core.Minecraft.Instances;

namespace CCL.Core.Tests;

public class InstanceManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccl-inst-" + Guid.NewGuid().ToString("N"));

    public InstanceManagerTests() => Directory.CreateDirectory(_root);

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

    private InstanceInfo MakeInstance(string id, bool withJar = true)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{id}.json"), $$"""{"id":"{{id}}","type":"release"}""");
        if (withJar) File.WriteAllBytes(Path.Combine(dir, $"{id}.jar"), new byte[2048]);

        return InstanceScanner.Read(dir) ?? throw new InvalidOperationException("实例解析失败");
    }

    [Fact]
    public void DeleteRemovesVersionDirectoryOnly()
    {
        var instance = MakeInstance("1.21.1");
        var sibling = Path.Combine(_root, "shared", "libraries");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "a.jar"), string.Empty);

        var result = InstanceManager.Delete(instance);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(instance.Directory));
        Assert.True(Directory.Exists(sibling));
    }

    [Fact]
    public void DeleteIsIdempotent()
    {
        var instance = MakeInstance("1.20.1");
        InstanceManager.Delete(instance);

        var second = InstanceManager.Delete(instance);

        Assert.True(second.Succeeded);
    }

    [Fact]
    public void ScanSkipsDeletedDirectory()
    {
        var a = MakeInstance("1.19");
        MakeInstance("1.20");

        InstanceManager.Delete(a);
        var remaining = InstanceScanner.Scan(_root);

        Assert.Single(remaining);
        Assert.Equal("1.20", remaining[0].Id);
    }

    [Fact]
    public void SizeCountsNestedFiles()
    {
        var instance = MakeInstance("1.18");
        var baseline = InstanceManager.GetSizeBytes(instance.Directory);
        var nested = Path.Combine(instance.Directory, "natives");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "x.dll"), new byte[512]);

        Assert.Equal(baseline + 512, InstanceManager.GetSizeBytes(instance.Directory));
        Assert.True(baseline >= 2048);
    }

    [Fact]
    public void IncompleteInstanceIsFlagged()
    {
        var instance = MakeInstance("broken", withJar: false);

        Assert.False(instance.IsComplete);
        Assert.Contains("安装未完成", instance.SubtitleLabel);
    }

    [Fact]
    public void CompleteInstanceShowsJavaLabel()
    {
        var instance = MakeInstance("ok");

        Assert.True(instance.IsComplete);
        Assert.Contains("release", instance.SubtitleLabel);
    }
}
