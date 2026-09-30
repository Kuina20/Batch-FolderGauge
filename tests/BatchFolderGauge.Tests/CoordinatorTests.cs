using BatchFolderGauge.Core;
using Xunit;

namespace BatchFolderGauge.Tests;

public sealed class CoordinatorTests
{
    [Fact]
    public async Task TwoWorkersStayOwnedUntilBlockedIoReturnsAndQueuedRootsCancel()
    {
        using var source = new BlockingSource();
        using var cancellation = new CancellationTokenSource();
        var coordinator = new ScanCoordinator(new FolderScanner(source));
        var batch = coordinator.ScanAsync(["a", "b", "c", "d"], null, cancellation.Token);
        try
        {
            Assert.True(source.TwoEntered.Wait(TimeSpan.FromSeconds(10)), "Both workers must start.");
            cancellation.Cancel();
            Assert.False(batch.IsCompleted); // Cancellation does not abandon blocking SMB work.
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ScanAsync(["e"], null, CancellationToken.None));
        }
        finally { source.Release.Set(); }

        var results = await batch.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4, results.Count);
        Assert.All(results, result => Assert.Equal(ScanStatus.Cancelled, result.Status));
        Assert.Equal(2, source.Entered);
        Assert.Equal(2, source.MaximumActive);
        Assert.Equal(0, source.Active);
        var next = await coordinator.ScanAsync(["e"], null, CancellationToken.None);
        Assert.Equal(ScanStatus.Completed, Assert.Single(next).Status);
    }

    [Fact]
    public async Task BatchReturnsResultsInInputOrderAndFailureDoesNotStopOtherRoots()
    {
        var source = new ScannerTests.FakeSource { Attributes = path => path == "bad"
            ? throw new IOException("离线") : FileAttributes.Directory, Enumerate = _ => Array.Empty<ScanEntry>() };
        var coordinator = new ScanCoordinator(new FolderScanner(source));
        var results = await coordinator.ScanAsync(["good", "bad", "good2"], null, CancellationToken.None);
        Assert.Equal(new[] { "good", "bad", "good2" }, results.Select(r => r.RootPath));
        Assert.Equal(new[] { ScanStatus.Completed, ScanStatus.Failed, ScanStatus.Completed }, results.Select(r => r.Status));
    }

    private sealed class BlockingSource : IFileSystemSource, IDisposable
    {
        public ManualResetEventSlim TwoEntered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        private int entered, active, maximumActive;
        public int Entered => Volatile.Read(ref entered);
        public int Active => Volatile.Read(ref active);
        public int MaximumActive => Volatile.Read(ref maximumActive);

        public FileAttributes GetRootAttributes(string path)
        {
            int now = Interlocked.Increment(ref active);
            int old;
            do { old = Volatile.Read(ref maximumActive); }
            while (now > old && Interlocked.CompareExchange(ref maximumActive, now, old) != old);
            if (Interlocked.Increment(ref entered) == 2) TwoEntered.Set();
            try
            {
                if (!Release.Wait(TimeSpan.FromSeconds(20))) throw new IOException("Test I/O timed out.");
                return FileAttributes.Directory;
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public IEnumerable<ScanEntry> EnumerateDirectory(string path) => Array.Empty<ScanEntry>();
        public void Dispose() { Release.Dispose(); TwoEntered.Dispose(); }
    }
}
