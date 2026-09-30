namespace BatchFolderGauge.Core;

public sealed class ScanCoordinator(FolderScanner scanner)
{
    private int running;

    public async Task<IReadOnlyList<ScanSnapshot>> ScanAsync(
        IReadOnlyList<string> paths, IProgress<ScanSnapshot>? progress, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("上一轮扫描尚未结束。");
        try
        {
            var roots = paths.ToArray();
            var results = new ScanSnapshot[roots.Length];
            int next = -1;
            var workers = Enumerable.Range(0, Math.Min(2, roots.Length)).Select(_ => Task.Run(() =>
            {
                int index;
                while ((index = Interlocked.Increment(ref next)) < roots.Length)
                {
                    // Do not pass the token to Task.Run: even cancelled queued roots need a final result.
                    results[index] = scanner.Scan(roots[index], progress, cancellationToken);
                }
            })).ToArray();
            await Task.WhenAll(workers).ConfigureAwait(false);
            return results;
        }
        finally { Volatile.Write(ref running, 0); }
    }
}
