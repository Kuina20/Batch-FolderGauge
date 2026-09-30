using System.IO.Enumeration;

namespace BatchFolderGauge.Core;

public readonly record struct ScanEntry(string Path, FileAttributes Attributes, long Length);

/// <summary>Enumeration errors must be surfaced, rather than silently skipping inaccessible entries.</summary>
public interface IFileSystemSource
{
    FileAttributes GetRootAttributes(string path);
    IEnumerable<ScanEntry> EnumerateDirectory(string path);
}

public sealed class FileSystemSource : IFileSystemSource
{
    public FileAttributes GetRootAttributes(string path) => File.GetAttributes(path);

    public IEnumerable<ScanEntry> EnumerateDirectory(string path) =>
        new FileSystemEnumerable<ScanEntry>(path, (ref FileSystemEntry entry) =>
            new ScanEntry(entry.ToFullPath(), entry.Attributes, entry.IsDirectory ? 0 : entry.Length),
            new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false,
                BufferSize = 64 * 1024
            });
}
