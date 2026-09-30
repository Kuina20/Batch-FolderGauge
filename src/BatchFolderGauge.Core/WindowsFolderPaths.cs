namespace BatchFolderGauge.Core;

/// <summary>Lexical Windows path normalization; never probes a drive or contacts a server.</summary>
public static class WindowsFolderPaths
{
    public static string Normalize(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        string path = input.Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"') path = path[1..^1];
        path = path.Replace('/', '\\');
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];

        string root;
        string[] parts;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var uncParts = path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (uncParts.Length < 2) throw new ArgumentException("UNC 路径必须包含服务器和共享名称，例如 \\\\server\\share。");
            ValidateSegment(uncParts[0]); ValidateSegment(uncParts[1]);
            if (uncParts[0] is "." or ".." || uncParts[1] is "." or "..")
                throw new ArgumentException("服务器和共享名称无效。");
            root = @"\\" + uncParts[0] + "\\" + uncParts[1] + "\\";
            parts = uncParts[2..];
        }
        else if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
        {
            root = char.ToUpperInvariant(path[0]) + @":\";
            parts = path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        }
        else throw new ArgumentException("请输入绝对文件夹路径，例如 C:\\Data 或 \\\\server\\share\\Data。");

        var segments = new List<string>();
        foreach (var part in parts)
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (segments.Count == 0) throw new ArgumentException("路径不能超出磁盘或共享根目录。");
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            ValidateSegment(part);
            segments.Add(part);
        }
        return root + string.Join('\\', segments);
    }

    private static void ValidateSegment(string segment)
    {
        if (segment.Any(c => c < ' ' || "<>:\"|?*".Contains(c)))
            throw new ArgumentException("路径包含无效字符。");
        // Ordinary Win32 paths trim these characters; reject rather than silently alias a different path.
        if (segment is not "." and not ".." && (segment.EndsWith(' ') || segment.EndsWith('.')))
            throw new ArgumentException("文件夹名称不能以空格或句点结尾。");
    }
}
