using System.Collections.Concurrent;
using BatchFolderGauge.Core;
using Xunit;
using static BatchFolderGauge.Tests.ScannerTests;

namespace BatchFolderGauge.Tests;

public sealed class CacheTests
{
    [Fact]
    public async Task ParentAndTwoChildrenEnumerateEachDirectoryOnceRegardlessOfInputOrder()
    {
        var source = new CountingSource();
        source.Directories[@"C:\ab"] = [File(@"C:\ab\a", 1), Dir(@"C:\ab\cd"), Dir(@"C:\ab\ef")];
        source.Directories[@"C:\ab\cd"] = [File(@"C:\ab\cd\b", 10)];
        source.Directories[@"C:\ab\ef"] = [File(@"C:\ab\ef\c", 20)];
        var results = await Run(source, @"C:\ab", @"C:\ab\cd", @"C:\ab\ef");
        Assert.Equal(new long?[] { 31, 10, 20 }, results.Select(r => r.TotalBytes));
        Assert.Equal(new long[] { 3, 1, 1 }, results.Select(r => r.FileCount));
        Assert.All(results, r => Assert.Equal(ScanStatus.Completed, r.Status));
        Assert.Equal(2, results[0].CacheHitCount);
        Assert.All(source.Enumerations.Values, count => Assert.Equal(1, count));
        Assert.Equal(3, source.Enumerations.Count);
    }

    [Fact]
    public async Task MultipleNestedSelectionsMergeOnlyNearestCachedSubtreeWithoutDoubleCounting()
    {
        var source = new CountingSource();
        source.Directories["root"] = [File("root/a", 1), Dir("root/sub")];
        source.Directories["root/sub"] = [File("root/sub/b", 10), Dir("root/sub/deep")];
        source.Directories["root/sub/deep"] = [File("root/sub/deep/c", 100)];
        var results = await Run(source, "root", "root/sub", "root/sub/deep");
        Assert.Equal(new long?[] { 111, 110, 100 }, results.Select(r => r.TotalBytes));
        Assert.Equal(new long[] { 1, 1, 0 }, results.Select(r => r.CacheHitCount));
        Assert.All(source.Enumerations.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task SelectedDeepPathThroughUnselectedIntermediateDirectoryIsReused()
    {
        var source = new CountingSource();
        source.Directories["root"] = [Dir("root/mid")];
        source.Directories["root/mid"] = [File("root/mid/a", 1), Dir("root/mid/deep")];
        source.Directories["root/mid/deep"] = [File("root/mid/deep/b", 20)];
        var results = await Run(source, "root", "root/mid/deep");
        Assert.Equal(21, results[0].TotalBytes);
        Assert.Equal(1, results[0].CacheHitCount);
        Assert.All(source.Enumerations.Values, count => Assert.Equal(1, count));
    }

    [Theory]
    [InlineData(@"C:\", @"C:\ab")]
    [InlineData(@"\\server\share\", @"\\server\share\ab")]
    public async Task DriveAndUncRootsFindChildCaches(string root, string child)
    {
        var source = new CountingSource();
        source.Directories[root] = [Dir(child)];
        source.Directories[child] = [File(child + @"\data", 10)];
        var results = await Run(source, root, child);
        Assert.Equal(10, results[0].TotalBytes);
        Assert.Equal(1, results[0].CacheHitCount);
        Assert.Equal(1, source.Enumerations[child]);
    }

    [Fact]
    public async Task SiblingNamePrefixesAreNotTreatedAsAncestors()
    {
        var source = new CountingSource();
        source.Directories[@"C:\ab"] = [File(@"C:\ab\data", 1)];
        source.Directories[@"C:\abc"] = [File(@"C:\abc\data", 20)];
        var results = await Run(source, @"C:\ab", @"C:\abc");
        Assert.Equal(new long?[] { 1, 20 }, results.Select(r => r.TotalBytes));
        Assert.All(results, r => Assert.Equal(0, r.CacheHitCount));
    }

    [Fact]
    public async Task DirectoryEnumerationAndSelectionsUseCaseInsensitiveCanonicalKeys()
    {
        var source = new CountingSource();
        source.Directories[@"C:\AB"] = [Dir(@"C:\AB\CD")];
        source.Directories[@"c:\ab\cd\"] = [File(@"C:\AB\CD\data", 12)];
        var results = await Run(source, @"C:\AB", @"c:\ab\cd\");
        Assert.Equal(12, results[0].TotalBytes);
        Assert.Equal(1, results[0].CacheHitCount);
        Assert.Equal(1, source.Enumerations[@"c:\ab\cd\"]);
    }

    [Fact]
    public async Task PartialCachePropagatesErrorsAndSkippedItemsToParent()
    {
        var source = new CountingSource();
        source.Directories["root"] = [Dir("root/sub")];
        source.Directories["root/sub"] = [File("root/sub/data", 10), Dir("root/sub/bad"),
            Dir("root/sub/link", FileAttributes.ReparsePoint)];
        source.Enumerate = path => path == "root/sub/bad"
            ? throw new UnauthorizedAccessException("无权限") : source.Directories[path];
        var results = await Run(source, "root", "root/sub");
        Assert.All(results, r => Assert.Equal(ScanStatus.Partial, r.Status));
        Assert.All(results, r => Assert.Equal(10, r.TotalBytes));
        Assert.All(results, r => Assert.Equal(1, r.ErrorCount));
        Assert.All(results, r => Assert.Equal(1, r.SkippedCount));
        Assert.Equal(results[1].Errors, results[0].Errors);
        Assert.Equal(1, source.Enumerations["root/sub/bad"]);
        Assert.Equal(1, results[0].CacheHitCount);
    }

    [Fact]
    public async Task CacheMergingPreservesFullErrorCountAndLimitsParentDetails()
    {
        var source = new CountingSource();
        source.Directories["root"] = [Dir("root/a"), Dir("root/b")];
        foreach (string child in new[] { "root/a", "root/b" })
            source.Directories[child] = Enumerable.Range(0, 80).Select(i => Dir($"{child}/{i}")).ToArray();
        source.Enumerate = path => source.Directories.TryGetValue(path, out var entries)
            ? entries : throw new IOException("失败");
        var results = await Run(source, "root", "root/a", "root/b");
        Assert.Equal(160, results[0].ErrorCount);
        Assert.Equal(FolderScanner.MaxErrorDetails, results[0].Errors.Count);
        Assert.Equal(2, results[0].CacheHitCount);
        Assert.Equal(ScanStatus.Partial, results[0].Status);
        Assert.All(source.Enumerations.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task FailedRootIsNotCachedAndParentCanRetrySubtree()
    {
        var source = new CountingSource();
        source.Directories["root"] = [Dir("root/sub")];
        source.Directories["root/sub"] = [File("root/sub/data", 15)];
        source.Attributes = path => path == "root/sub" ? throw new IOException("暂时离线") : FileAttributes.Directory;
        var results = await Run(source, "root", "root/sub");
        Assert.Equal(ScanStatus.Failed, results[1].Status);
        Assert.Equal(ScanStatus.Completed, results[0].Status);
        Assert.Equal(15, results[0].TotalBytes);
        Assert.Equal(0, results[0].CacheHitCount);
    }

    [Fact]
    public async Task ANewBatchDoesNotReuseOldSizes()
    {
        var source = new CountingSource();
        source.Directories["root"] = [Dir("root/sub")];
        source.Directories["root/sub"] = [File("root/sub/data", 10)];
        var coordinator = new ScanCoordinator(new FolderScanner(source));
        var first = await coordinator.ScanAsync(["root", "root/sub"], null, CancellationToken.None);
        source.Directories["root/sub"] = [File("root/sub/data", 30)];
        var second = await coordinator.ScanAsync(["root", "root/sub"], null, CancellationToken.None);
        Assert.Equal(10, first[0].TotalBytes);
        Assert.Equal(30, second[0].TotalBytes);
        Assert.Equal(2, source.Enumerations["root/sub"]);
    }

    [Fact]
    public async Task ParentDoesNotStartWhileItsSelectedChildIsStillBlocked()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var source = new CountingSource();
        source.Directories["root"] = [Dir("root/sub")];
        source.Directories["root/sub"] = [File("root/sub/data", 10)];
        source.Attributes = path =>
        {
            if (path == "root/sub")
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Test timed out.");
            }
            return FileAttributes.Directory;
        };
        var batch = Run(source, "root", "root/sub");
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(source.RootAccesses.ContainsKey("root"));
        }
        finally { release.Set(); }
        var results = await batch.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, results[0].CacheHitCount);
        Assert.Equal(1, source.Enumerations["root/sub"]);
    }

    [Fact]
    public async Task CancellationReleasesDependentParentsWithoutContactingThem()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new CountingSource();
        source.Enumerate = _ => Entries();
        IEnumerable<ScanEntry> Entries()
        {
            yield return File("root/sub/data", 10);
            cancellation.Cancel();
            yield return File("root/sub/other", 20);
        }
        var coordinator = new ScanCoordinator(new FolderScanner(source));
        var results = await coordinator.ScanAsync(["root", "root/sub"], null, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, r => Assert.Equal(ScanStatus.Cancelled, r.Status));
        Assert.Equal(10, results[1].TotalBytes);
        Assert.Null(results[0].TotalBytes);
        Assert.False(source.RootAccesses.ContainsKey("root"));
        source.Enumerate = _ => Array.Empty<ScanEntry>();
        var retry = await coordinator.ScanAsync(["root"], null, CancellationToken.None);
        Assert.Equal(ScanStatus.Completed, retry[0].Status);
    }

    [Fact]
    public async Task UnexpectedChildFailureDoesNotLeaveWaitingWorkerDeadlocked()
    {
        var source = new CountingSource { Attributes = _ => throw new InvalidOperationException("Unexpected provider error") };
        var coordinator = new ScanCoordinator(new FolderScanner(source));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ScanAsync(["root", "root/sub"], null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        source.Attributes = _ => FileAttributes.Directory;
        source.Enumerate = _ => Array.Empty<ScanEntry>();
        Assert.Equal(ScanStatus.Completed, (await coordinator.ScanAsync(["root"], null, CancellationToken.None))[0].Status);
    }

    [Fact]
    public async Task CacheMergeOverflowDoesNotCorruptCounters()
    {
        var source = new CountingSource();
        source.Directories["root"] = [File("root/a", long.MaxValue), Dir("root/sub")];
        source.Directories["root/sub"] = [File("root/sub/b", 1)];
        var results = await Run(source, "root", "root/sub");
        Assert.Equal(ScanStatus.Partial, results[0].Status);
        Assert.Equal(long.MaxValue, results[0].TotalBytes);
        Assert.Equal(1, results[0].FileCount);
        Assert.Equal(1, results[0].ErrorCount);
    }

    private static Task<IReadOnlyList<ScanSnapshot>> Run(CountingSource source, params string[] paths) =>
        new ScanCoordinator(new FolderScanner(source)).ScanAsync(paths, null, CancellationToken.None);

    private sealed class CountingSource : IFileSystemSource
    {
        public Dictionary<string, ScanEntry[]> Directories { get; } = new();
        public ConcurrentDictionary<string, int> Enumerations { get; } = new();
        public ConcurrentDictionary<string, int> RootAccesses { get; } = new();
        public Func<string, FileAttributes> Attributes { get; set; } = _ => FileAttributes.Directory;
        public Func<string, IEnumerable<ScanEntry>>? Enumerate { get; set; }
        public FileAttributes GetRootAttributes(string path)
        {
            RootAccesses.AddOrUpdate(path, 1, (_, count) => count + 1);
            return Attributes(path);
        }
        public IEnumerable<ScanEntry> EnumerateDirectory(string path)
        {
            Enumerations.AddOrUpdate(path, 1, (_, count) => count + 1);
            return Enumerate?.Invoke(path) ?? Directories[path];
        }
    }
}
