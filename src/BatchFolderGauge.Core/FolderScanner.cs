using System.Diagnostics;
using System.Security;

namespace BatchFolderGauge.Core;

public sealed class FolderScanner(IFileSystemSource source)
{
    public const int MaxErrorDetails = 100;
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(200);

    // Synchronous by design: the coordinator owns a bounded set of background workers.
    public ScanSnapshot Scan(string rootPath, IProgress<ScanSnapshot>? progress, CancellationToken cancellationToken) =>
        Scan(rootPath, progress, cancellationToken, null);

    internal ScanSnapshot Scan(string rootPath, IProgress<ScanSnapshot>? progress,
        CancellationToken cancellationToken, BatchScanCache? cache)
    {
        var clock = Stopwatch.StartNew();
        var errors = new List<ScanError>();
        long bytes = 0, files = 0, errorCount = 0, skipped = 0, cacheHits = 0;
        bool rootAccessible = false;
        string currentPath = rootPath;
        TimeSpan lastUpdate = TimeSpan.Zero;

        ScanSnapshot Snapshot(ScanStatus status) => new(rootPath, status,
            rootAccessible && status != ScanStatus.Failed ? bytes : null,
            files, errorCount, skipped, clock.Elapsed, currentPath, errors.ToArray()) { CacheHitCount = cacheHits };

        void Report(bool force = false)
        {
            if (!force && clock.Elapsed - lastUpdate < UpdateInterval) return;
            lastUpdate = clock.Elapsed;
            progress?.Report(Snapshot(ScanStatus.Scanning));
        }

        void AddError(string path, Exception exception)
        {
            errorCount++;
            if (errors.Count < MaxErrorDetails) errors.Add(new(path, exception.Message));
        }

        ScanSnapshot Finish(ScanStatus status)
        {
            var result = Snapshot(status);
            progress?.Report(result);
            return result;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(force: true);
            var attributes = source.GetRootAttributes(rootPath);
            cancellationToken.ThrowIfCancellationRequested();
            if ((attributes & FileAttributes.Directory) == 0)
                throw new IOException("所选路径不是文件夹。");
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                rootAccessible = true;
                skipped++;
                return Finish(ScanStatus.Partial);
            }

            var directories = new Stack<string>();
            directories.Push(rootPath);
            while (directories.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                currentPath = directory;
                Report();
                if (directory != rootPath && cache is not null && cache.TryGet(directory, out var cached))
                {
                    try
                    {
                        // Compute all counters before committing, so overflow cannot merge half a subtree.
                        long mergedBytes = checked(bytes + cached.TotalBytes!.Value);
                        long mergedFiles = checked(files + cached.FileCount);
                        long mergedErrors = checked(errorCount + cached.ErrorCount);
                        long mergedSkipped = checked(skipped + cached.SkippedCount);
                        bytes = mergedBytes; files = mergedFiles;
                        errorCount = mergedErrors; skipped = mergedSkipped;
                        errors.AddRange(cached.Errors.Take(MaxErrorDetails - errors.Count));
                        cacheHits++;
                    }
                    catch (OverflowException ex) { AddError(directory, ex); }
                    Report();
                    continue;
                }
                try
                {
                    using var entries = source.EnumerateDirectory(directory).GetEnumerator();
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        bool hasEntry = entries.MoveNext(); // SMB may block here until Windows returns.
                        if (directory == rootPath) rootAccessible = true;
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!hasEntry) break;
                        var entry = entries.Current;
                        currentPath = entry.Path;
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) skipped++;
                        else if ((entry.Attributes & FileAttributes.Directory) != 0) directories.Push(entry.Path);
                        else
                        {
                            try
                            {
                                if (entry.Length < 0) throw new IOException("文件长度无效。");
                                bytes = checked(bytes + entry.Length);
                                files++;
                            }
                            catch (Exception ex) when (ex is IOException or OverflowException)
                            {
                                AddError(entry.Path, ex);
                            }
                        }
                        Report();
                    }
                }
                catch (Exception ex) when (IsFileSystemError(ex))
                {
                    AddError(directory, ex);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (directory == rootPath && !rootAccessible) return Finish(ScanStatus.Failed);
                    // A failed enumeration cannot be resumed; continue other discovered directories.
                }
            }
            return Finish(errorCount != 0 || skipped != 0 ? ScanStatus.Partial : ScanStatus.Completed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Finish(ScanStatus.Cancelled);
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            AddError(currentPath, ex);
            if (cancellationToken.IsCancellationRequested) return Finish(ScanStatus.Cancelled);
            return Finish(ScanStatus.Failed);
        }
    }

    private static bool IsFileSystemError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException;
}
