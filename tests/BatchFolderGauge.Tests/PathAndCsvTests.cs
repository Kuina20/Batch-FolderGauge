using System.Text;
using BatchFolderGauge.Core;
using Xunit;

namespace BatchFolderGauge.Tests;

public sealed class PathAndCsvTests
{
    [Theory]
    [InlineData("c:/Data/./A/../", @"C:\Data")]
    [InlineData("\"C:\\Data\\\"", @"C:\Data")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"\\server\share\", @"\\server\share\")]
    [InlineData(@"\\server\share\folder\..", @"\\server\share\")]
    [InlineData(@"\\?\UNC\server\share\Data", @"\\server\share\Data")]
    [InlineData(@"\\?\C:\Data", @"C:\Data")]
    public void PathsNormalizeWithoutAccessingFileSystem(string input, string expected) =>
        Assert.Equal(expected, WindowsFolderPaths.Normalize(input));

    [Theory]
    [InlineData("Data")]
    [InlineData("C:Data")]
    [InlineData(@"\\server")]
    [InlineData(@"C:\..\Data")]
    [InlineData(@"\\server\share\..")]
    [InlineData(@"C:\Bad*Name")]
    [InlineData(@"C:\Bad.")]
    [InlineData(@"\\.\pipe\folder")]
    public void InvalidOrAmbiguousPathsAreRejected(string input) =>
        Assert.Throws<ArgumentException>(() => WindowsFolderPaths.Normalize(input));

    [Fact]
    public void NormalizedWindowsPathsDeduplicateCaseAndTrailingSeparators()
    {
        string[] inputs = [@"c:\Data\", @"C:\DATA", @"c:/data/.", @"C:\Data\Sub"];
        var normalized = inputs.Select(WindowsFolderPaths.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, normalized.Count);
    }

    [Fact]
    public void CsvIncludesBomQuotesNewlinesAndEmptyUnknownSizes()
    {
        var partial = new ScanSnapshot(@"\\server\share\中文,目录", ScanStatus.Partial, 1234, 2, 1, 3,
            TimeSpan.FromSeconds(1.25), "", [new("a", "错误 \"权限\"\r\n详情")]);
        using var destination = new MemoryStream();
        CsvExporter.Write(destination, [partial, ScanSnapshot.Queued(@"C:\Data") with { Status = ScanStatus.Cancelled }]);
        byte[] bytes = destination.ToArray();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        string csv = Encoding.UTF8.GetString(bytes[3..]);
        Assert.Contains("\"\\\\server\\share\\中文,目录\",\"1234\"", csv);
        Assert.Contains("\"a: 错误 \"\"权限\"\"\r\n详情\"", csv);
        Assert.Contains("\"C:\\Data\",\"\",\"—\",\"0\",\"已取消\"", csv);
        Assert.Contains("\"1.25\"", csv);
        Assert.True(destination.CanWrite); // Exporter leaves the caller's stream open.
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0L, "0 B")]
    [InlineData(1024L, "1 KiB")]
    [InlineData(1048576L, "1 MiB")]
    [InlineData(1073741824L, "1 GiB")]
    [InlineData(1099511627776L, "1 TiB")]
    public void SizeUnitsUseBinaryMultiples(long? bytes, string expected) =>
        Assert.Equal(expected, ResultFormatting.Size(bytes));
}
