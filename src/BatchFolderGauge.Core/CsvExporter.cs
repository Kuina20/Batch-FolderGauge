using System.Globalization;
using System.Text;

namespace BatchFolderGauge.Core;

public static class CsvExporter
{
    public static void Write(Stream destination, IEnumerable<ScanSnapshot> results)
    {
        using var writer = new StreamWriter(destination, new UTF8Encoding(true), leaveOpen: true);
        writer.NewLine = "\r\n";
        writer.WriteLine("路径,逻辑字节数,大小,文件数,状态,错误数,跳过数,耗时秒,错误摘要,缓存复用数");
        foreach (var result in results)
        {
            string summary = string.Join("\n", result.Errors.Select(e => $"{e.Path}: {e.Message}"));
            if (result.ErrorCount > result.Errors.Count)
                summary += $"\n仅显示前 {result.Errors.Count} 条，共 {result.ErrorCount} 条错误。";
            string[] fields = [result.RootPath, result.TotalBytes?.ToString(CultureInfo.InvariantCulture) ?? "",
                ResultFormatting.Size(result.TotalBytes), result.FileCount.ToString(CultureInfo.InvariantCulture),
                ResultFormatting.Status(result.Status), result.ErrorCount.ToString(CultureInfo.InvariantCulture),
                result.SkippedCount.ToString(CultureInfo.InvariantCulture),
                result.Elapsed.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), summary,
                result.CacheHitCount.ToString(CultureInfo.InvariantCulture)];
            writer.WriteLine(string.Join(',', fields.Select(Escape)));
        }
    }

    private static string Escape(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
