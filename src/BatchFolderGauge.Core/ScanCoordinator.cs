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
            var cache = new BatchScanCache();
            var nodes = new Dictionary<string, ScanNode>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < roots.Length; i++)
            {
                string key = BatchScanCache.Key(roots[i]);
                if (!nodes.TryGetValue(key, out var node)) nodes.Add(key, node = new(roots[i], key, i));
                node.Indices.Add(i);
            }

            // Each root depends on its nearest selected descendants, through unselected directories too.
            // Look up whole path components: C:\ab is not an ancestor of C:\abc.
            foreach (var node in nodes.Values)
            {
                string ancestor = node.Key;
                int separator;
                while ((separator = ancestor.LastIndexOf('\\')) >= 0)
                {
                    ancestor = ancestor[..separator];
                    if (!nodes.TryGetValue(ancestor, out var parent)) continue;
                    node.Parent = parent;
                    parent.PendingChildren++;
                    break;
                }
            }

            var ready = new PriorityQueue<ScanNode, (int Depth, int Index)>();
            void Enqueue(ScanNode node) => ready.Enqueue(node, (-node.Key.Count(c => c == '\\'), node.FirstIndex));
            foreach (var node in nodes.Values.Where(node => node.PendingChildren == 0)) Enqueue(node);
            object gate = new();
            int remaining = nodes.Count;
            bool faulted = false;
            var workers = Enumerable.Range(0, Math.Min(2, nodes.Count)).Select(_ => Task.Run(() =>
            {
                while (true)
                {
                    ScanNode node;
                    lock (gate)
                    {
                        while (ready.Count == 0 && remaining > 0 && !faulted) Monitor.Wait(gate);
                        if (remaining == 0 || faulted) return;
                        node = ready.Dequeue();
                    }
                    try
                    {
                        // Never pass the token to Task.Run: queued roots still need cancelled final results.
                        var result = scanner.Scan(node.Path, progress, cancellationToken, cache);
                        cache.Store(result);
                        foreach (int index in node.Indices) results[index] = result with { RootPath = roots[index] };
                        lock (gate)
                        {
                            remaining--;
                            if (node.Parent is { } parent && --parent.PendingChildren == 0) Enqueue(parent);
                            Monitor.PulseAll(gate);
                        }
                    }
                    catch
                    {
                        // Release waiting workers if an unexpected source/progress exception occurs.
                        lock (gate) { faulted = true; Monitor.PulseAll(gate); }
                        throw;
                    }
                }
            })).ToArray();
            await Task.WhenAll(workers).ConfigureAwait(false);
            return results;
        }
        finally { Volatile.Write(ref running, 0); }
    }

    private sealed class ScanNode(string path, string key, int firstIndex)
    {
        public string Path { get; } = path;
        public string Key { get; } = key;
        public int FirstIndex { get; } = firstIndex;
        public List<int> Indices { get; } = [];
        public ScanNode? Parent { get; set; }
        public int PendingChildren { get; set; }
    }
}
