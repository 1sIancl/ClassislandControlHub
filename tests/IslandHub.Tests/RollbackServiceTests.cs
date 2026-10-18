using IslandHub.Server.Options;
using IslandHub.Server.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IslandHub.Tests;

/// <summary>
/// 程序文件快照与回滚（#89）。
///
/// <para>
/// 为什么要有单测：这段逻辑是**成批覆盖程序文件**，在真机上验的成本极高（要真的发一版再退回去），
/// 而它最容易错的地方恰好是纯文件操作——「数据目录不能被覆盖」「数据目录不能被跳过到把程序文件也漏掉」
/// 「快照要能原样铺回子目录」。用临时目录能把这些点逐个钉住。
/// </para>
/// </summary>
public sealed class RollbackServiceTests
{
    [Fact]
    public void Snapshot_ExcludesDataDirectory_AndRestorePutsFilesBack()
    {
        using var fixture = new RollbackFixture();
        fixture.Write("app.dll", "v2");
        fixture.Write(Path.Combine("sub", "lib.dll"), "v2");
        fixture.Write(Path.Combine("data", "controlhub.db"), "db-v2");

        var snapshot = fixture.Service.Create("pre-update");
        Assert.NotNull(snapshot);
        // 只打包程序文件：app.dll + sub/lib.dll（data 目录整个排除）。
        Assert.Equal(2, snapshot!.FileCount);
        Assert.True(snapshot.SizeBytes > 0);

        // 更新之后：程序文件变了，数据也变了。
        fixture.Write("app.dll", "v3");
        fixture.Write(Path.Combine("sub", "lib.dll"), "v3");
        fixture.Write(Path.Combine("data", "controlhub.db"), "db-v3");

        var result = fixture.Service.Restore(snapshot.Id);

        Assert.True(result.Ok);
        Assert.True(result.RestartRequired);
        Assert.Equal("v2", fixture.Read("app.dll"));
        Assert.Equal("v2", fixture.Read(Path.Combine("sub", "lib.dll")));
        // 关键：数据目录**不动**——回滚的是版本，不是数据。
        Assert.Equal("db-v3", fixture.Read(Path.Combine("data", "controlhub.db")));
    }

    [Fact]
    public void Restore_TakesPreRollbackSnapshot_SoRollbackItselfIsReversible()
    {
        using var fixture = new RollbackFixture();
        fixture.Write("app.dll", "v1");
        var first = fixture.Service.Create("manual");
        Assert.NotNull(first);

        fixture.Write("app.dll", "v2");
        fixture.Service.Restore(first!.Id);

        // 回滚前自动拍的那份应当存在（内容 = 回滚前的 v2），所以「回滚错了」还能再退回来。
        var snapshots = fixture.Service.List();
        Assert.Contains(snapshots, s => s.Reason == "pre-rollback");
        Assert.Equal("v1", fixture.Read("app.dll"));
    }

    [Fact]
    public void List_KeepsOnlyRecentSnapshots_AndNewestFirst()
    {
        using var fixture = new RollbackFixture();
        fixture.Write("app.dll", "v1");

        for (var i = 0; i < 5; i++)
        {
            Assert.NotNull(fixture.Service.Create("manual"));
        }

        var snapshots = fixture.Service.List();
        Assert.Equal(3, snapshots.Count);
        Assert.True(snapshots[0].CreatedAt >= snapshots[^1].CreatedAt);
    }

    [Fact]
    public void Restore_UnknownSnapshot_ReportsFailureInsteadOfThrowing()
    {
        using var fixture = new RollbackFixture();
        var result = fixture.Service.Restore("rollback-does-not-exist");

        Assert.False(result.Ok);
        Assert.Contains("找不到", result.Message);
    }

    /// <summary>临时程序目录 + 指向它的服务实例。</summary>
    private sealed class RollbackFixture : IDisposable
    {
        private readonly string _root;

        public RollbackFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), $"islandhub-rollback-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(_root, "data"));

            var options = new ServerOptions { DataDirectory = Path.Combine(_root, "data") };
            Service = new RollbackService(
                new StaticOptionsMonitor<ServerOptions>(options),
                new FakeHostEnvironment(_root),
                NullLogger<RollbackService>.Instance);
        }

        public RollbackService Service { get; }

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public string Read(string relativePath) =>
            File.ReadAllText(Path.Combine(_root, relativePath));

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // 临时目录删不掉不影响结论（Windows 上偶尔有句柄延迟释放）。
            }
        }
    }

    private sealed class FakeHostEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "IslandHub.Tests";

        public string ContentRootPath { get; set; } = root;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
