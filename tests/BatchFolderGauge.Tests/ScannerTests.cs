using BatchFolderGauge.Core;
using Xunit;

namespace BatchFolderGauge.Tests;

public sealed class ScannerTests
{
    [Fact]
    public void EmptyDirectoryHasKnownZeroSize()
    {
        var result = Scan(new FakeSource());
        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(0, result.TotalBytes);
        Assert.Equal(0, result.FileCount);
    }

    [Fact]
    public void NestedHiddenAndSystemEntriesAreIncludedAndSizesUseInt64()
    {
        var source = new FakeSource();
        source.Directories["root"] = [File("root/a", 3_000_000_000),
            new("root/hidden", FileAttributes.Hidden, 7), Dir("root/sub", FileAttributes.System)];
        source.Directories["root/sub"] = [File("root/sub/b", 2_000_000_000), File("root/sub/empty", 0)];
        var result = Scan(source);
        Assert.Equal(5_000_000_007, result.TotalBytes);
        Assert.Equal(4, result.FileCount);
        Assert.Equal(ScanStatus.Completed, result.Status);
    }

    [Fact]
    public void RealFileSystemMetadataMatchesNestedFileLengths()
    {
        string root = Path.Combine(Path.GetTempPath(), "bfg-test-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        try
        {
            System.IO.File.WriteAllBytes(Path.Combine(root, "a"), new byte[123]);
            System.IO.File.WriteAllBytes(Path.Combine(root, "nested", "b"), new byte[456]);
            var result = new FolderScanner(new FileSystemSource()).Scan(root, null, CancellationToken.None);
            Assert.Equal(579, result.TotalBytes);
            Assert.Equal(2, result.FileCount);
            Assert.Equal(ScanStatus.Completed, result.Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InaccessibleRootIsFailedWithUnknownSize()
    {
        var source = new FakeSource { Attributes = _ => throw new UnauthorizedAccessException("拒绝访问") };
        var result = Scan(source);
        Assert.Equal(ScanStatus.Failed, result.Status);
        Assert.Null(result.TotalBytes);
        Assert.Equal(1, result.ErrorCount);
        Assert.Equal("拒绝访问", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public void RootEnumerationFailureBeforeFirstEntryIsFailed()
    {
        var source = new FakeSource { Enumerate = _ => throw new IOException("共享离线") };
        var result = Scan(source);
        Assert.Equal(ScanStatus.Failed, result.Status);
        Assert.Null(result.TotalBytes);
    }

    [Fact]
    public void ChildEnumerationFailureDoesNotLoseAccessibleSiblings()
    {
        var source = new FakeSource();
        source.Directories["root"] = [File("root/a", 10), Dir("root/good"), Dir("root/bad")];
        source.Directories["root/good"] = [File("root/good/b", 20)];
        source.Enumerate = path => path == "root/bad"
            ? throw new UnauthorizedAccessException("无权限") : source.Directories[path];
        var result = Scan(source);
        Assert.Equal(30, result.TotalBytes);
        Assert.Equal(2, result.FileCount);
        Assert.Equal(ScanStatus.Partial, result.Status);
        Assert.Equal("root/bad", Assert.Single(result.Errors).Path);
    }

    [Fact]
    public void NetworkFailureMidEnumerationPreservesPartialSizeAndOtherDirectories()
    {
        var source = new FakeSource();
        source.Directories["root/sub"] = [File("root/sub/b", 20)];
        source.Enumerate = path => path == "root" ? BrokenRoot() : source.Directories[path];
        static IEnumerable<ScanEntry> BrokenRoot()
        {
            yield return File("root/a", 10);
            yield return Dir("root/sub");
            throw new IOException("连接中断");
        }
        var result = Scan(source);
        Assert.Equal(ScanStatus.Partial, result.Status);
        Assert.Equal(30, result.TotalBytes);
        Assert.Equal(1, result.ErrorCount);
    }

    [Fact]
    public void FileAndDirectoryReparsePointsAreSkippedIncludingCycles()
    {
        var source = new FakeSource();
        source.Directories["root"] = [File("root/a", 5),
            new("root/link", FileAttributes.ReparsePoint, 999), Dir("root/cycle", FileAttributes.ReparsePoint)];
        var result = Scan(source);
        Assert.Equal(5, result.TotalBytes);
        Assert.Equal(1, result.FileCount);
        Assert.Equal(2, result.SkippedCount);
        Assert.Equal(ScanStatus.Partial, result.Status);
    }

    [Fact]
    public void SelectedReparseRootIsAlsoSkipped()
    {
        var source = new FakeSource { Attributes = _ => FileAttributes.Directory | FileAttributes.ReparsePoint,
            Enumerate = _ => throw new InvalidOperationException("Should never enumerate a link") };
        var result = Scan(source);
        Assert.Equal(ScanStatus.Partial, result.Status);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(0, result.TotalBytes);
    }

    [Fact]
    public void ErrorDetailsAreBoundedButAllFailuresAreCounted()
    {
        var source = new FakeSource();
        source.Directories["root"] = Enumerable.Range(0, 150).Select(i => Dir($"root/{i}")).ToArray();
        source.Enumerate = path => path == "root" ? source.Directories[path] : throw new IOException("文件夹已消失");
        var result = Scan(source);
        Assert.Equal(150, result.ErrorCount);
        Assert.Equal(FolderScanner.MaxErrorDetails, result.Errors.Count);
        Assert.Equal(ScanStatus.Partial, result.Status);
    }

    [Fact]
    public void CancelBeforeStartingDoesNotContactServerOrPretendSizeIsZero()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = new FakeSource { Attributes = _ => throw new InvalidOperationException("Must not contact server") };
        var result = new FolderScanner(source).Scan("root", null, cancellation.Token);
        Assert.Equal(ScanStatus.Cancelled, result.Status);
        Assert.Null(result.TotalBytes);
    }

    [Fact]
    public void CancellationAfterEntryRetainsObservedBytes()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new FakeSource { Enumerate = _ => Entries() };
        IEnumerable<ScanEntry> Entries()
        {
            yield return File("root/a", 12);
            cancellation.Cancel();
            yield return File("root/b", 99);
        }
        var progress = new CaptureProgress();
        var result = new FolderScanner(source).Scan("root", progress, cancellation.Token);
        Assert.Equal(ScanStatus.Cancelled, result.Status);
        Assert.Equal(12, result.TotalBytes);
        Assert.Equal(1, result.FileCount);
        Assert.Equal(result, progress.Items[^1]);
    }

    [Fact]
    public void CancellationWhileBlockedIoReturnsAnErrorStillEndsAsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new FakeSource { Enumerate = _ => Entries() };
        IEnumerable<ScanEntry> Entries()
        {
            yield return File("root/a", 12);
            cancellation.Cancel();
            throw new IOException("断线时请求返回错误");
        }
        var result = new FolderScanner(source).Scan("root", null, cancellation.Token);
        Assert.Equal(ScanStatus.Cancelled, result.Status);
        Assert.Equal(12, result.TotalBytes);
        Assert.Equal(1, result.ErrorCount);
    }

    [Fact]
    public void OverflowIsReportedInsteadOfReturningNegativeBytes()
    {
        var source = new FakeSource();
        source.Directories["root"] = [File("root/a", long.MaxValue), File("root/b", 1)];
        var result = Scan(source);
        Assert.Equal(long.MaxValue, result.TotalBytes);
        Assert.Equal(ScanStatus.Partial, result.Status);
        Assert.Equal(1, result.ErrorCount);
    }

    private static ScanSnapshot Scan(FakeSource source) => new FolderScanner(source).Scan("root", null, CancellationToken.None);
    internal static ScanEntry File(string path, long bytes) => new(path, FileAttributes.Normal, bytes);
    internal static ScanEntry Dir(string path, FileAttributes extra = 0) => new(path, FileAttributes.Directory | extra, 0);

    internal sealed class FakeSource : IFileSystemSource
    {
        public Dictionary<string, ScanEntry[]> Directories { get; } = new() { ["root"] = [] };
        public Func<string, FileAttributes> Attributes { get; init; } = _ => FileAttributes.Directory;
        public Func<string, IEnumerable<ScanEntry>>? Enumerate { get; set; }
        public FileAttributes GetRootAttributes(string path) => Attributes(path);
        public IEnumerable<ScanEntry> EnumerateDirectory(string path) => Enumerate?.Invoke(path) ?? Directories[path];
    }

    internal sealed class CaptureProgress : IProgress<ScanSnapshot>
    {
        public List<ScanSnapshot> Items { get; } = [];
        public void Report(ScanSnapshot value) => Items.Add(value);
    }
}
