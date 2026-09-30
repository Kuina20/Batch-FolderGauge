using System.Globalization;
using System.Text;
using BatchFolderGauge.App;
using BatchFolderGauge.Core;
using Xunit;

namespace BatchFolderGauge.Tests;

public sealed class SizeUnitTests
{
    [Theory]
    [InlineData(1572864L, SizeUnit.B, "1572864 B")]
    [InlineData(1572864L, SizeUnit.KiB, "1536 KiB")]
    [InlineData(1572864L, SizeUnit.MiB, "1.5 MiB")]
    [InlineData(1610612736L, SizeUnit.GiB, "1.5 GiB")]
    [InlineData(1649267441664L, SizeUnit.TiB, "1.5 TiB")]
    [InlineData(0L, SizeUnit.TiB, "0 TiB")]
    [InlineData(null, SizeUnit.MiB, "—")]
    [InlineData(long.MaxValue, SizeUnit.B, "9223372036854775807 B")]
    public void FixedUnitsDoNotAutomaticallyScale(long? bytes, SizeUnit unit, string expected)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal(expected, ResultFormatting.Size(bytes, unit));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void UnitSelectionWaitsForAllRowsToFinishAndUpdatesEverySize()
    {
        var model = new MainViewModel();
        Assert.False(model.CanChangeSizeUnit);
        model.AddPaths([@"C:\First", @"C:\Second"]);
        model.SelectedSizeUnit = SizeUnit.KiB;
        Assert.Equal(SizeUnit.Automatic, model.SelectedSizeUnit);
        model.Rows[0].Update(model.Rows[0].Snapshot with { Status = ScanStatus.Completed, TotalBytes = 2048 });
        Assert.False(model.CanChangeSizeUnit);
        model.Rows[1].Update(model.Rows[1].Snapshot with { Status = ScanStatus.Scanning, TotalBytes = 1048576 });
        Assert.False(model.CanChangeSizeUnit);
        model.Rows[1].Update(model.Rows[1].Snapshot with { Status = ScanStatus.Partial });
        Assert.True(model.CanChangeSizeUnit);

        var changes = new List<string?>();
        model.Rows[0].PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        model.SelectedSizeUnit = SizeUnit.KiB;
        Assert.Equal("2 KiB", model.Rows[0].SizeText);
        Assert.Equal("1024 KiB", model.Rows[1].SizeText);
        Assert.Contains(nameof(FolderRow.SizeText), changes);
        Assert.Equal(2048L, model.Rows[0].TotalBytes);

        model.SelectedSizeUnit = SizeUnit.Automatic;
        Assert.Equal("2 KiB", model.Rows[0].SizeText);
        Assert.Equal("1 MiB", model.Rows[1].SizeText);

        var notifications = new List<string?>();
        model.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        model.AddPaths([@"C:\Third"]);
        Assert.False(model.CanChangeSizeUnit);
        Assert.Contains(nameof(MainViewModel.CanChangeSizeUnit), notifications);
        model.SelectedSizeUnit = SizeUnit.B;
        Assert.Equal(SizeUnit.Automatic, model.SelectedSizeUnit);
        model.Remove([model.Rows[2]]);
        Assert.True(model.CanChangeSizeUnit);
        model.ClearCommand.Execute(null);
        Assert.False(model.CanChangeSizeUnit);
    }

    [Theory]
    [InlineData(ScanStatus.Failed, null, "—")]
    [InlineData(ScanStatus.Cancelled, 2048L, "2 KiB")]
    public void EndedFailedAndCancelledRowsAllowUnitSelection(ScanStatus status, long? bytes, string expected)
    {
        var model = new MainViewModel();
        model.AddPaths([@"C:\Data"]);
        model.Rows[0].Update(model.Rows[0].Snapshot with { Status = status, TotalBytes = bytes });
        Assert.True(model.CanChangeSizeUnit);
        model.SelectedSizeUnit = SizeUnit.KiB;
        Assert.Equal(expected, model.Rows[0].SizeText);
    }

    [Fact]
    public async Task CsvExportUsesSelectedUnitAndKeepsExactBytes()
    {
        var model = new MainViewModel();
        model.AddPaths([@"C:\First", @"C:\Second"]);
        model.Rows[0].Update(model.Rows[0].Snapshot with { Status = ScanStatus.Completed, TotalBytes = 2048 });
        model.Rows[1].Update(model.Rows[1].Snapshot with { Status = ScanStatus.Completed, TotalBytes = 1048576 });
        model.SelectedSizeUnit = SizeUnit.KiB;
        string path = System.IO.Path.GetTempFileName();
        try
        {
            await model.ExportAsync(path);
            string csv = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
            Assert.Contains("\"2048\",\"2 KiB\"", csv);
            Assert.Contains("\"1048576\",\"1024 KiB\"", csv);
            Assert.True(model.CanChangeSizeUnit);
        }
        finally { File.Delete(path); }
    }
}
