using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;

namespace BatchFolderGauge.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = new();
    private bool closePending;

    public MainWindow() { InitializeComponent(); DataContext = viewModel; }

    private void AddFolders(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择要统计的文件夹（可多选）", Multiselect = true };
        try { if (dialog.ShowDialog(this) == true) ShowPathErrors(viewModel.AddPaths(dialog.FolderNames)); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法打开文件夹选择器", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void PastePaths(object sender, RoutedEventArgs e)
    {
        var dialog = new PathInputWindow { Owner = this };
        if (dialog.ShowDialog() == true)
            ShowPathErrors(viewModel.AddPaths(dialog.PathsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)));
    }

    private void ShowPathErrors(IReadOnlyList<string> errors)
    {
        if (errors.Count == 0) return;
        string message = string.Join("\n", errors.Take(10));
        if (errors.Count > 10) message += $"\n另有 {errors.Count - 10} 条无效路径。";
        MessageBox.Show(this, message, "部分路径未添加", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void RemoveSelected(object sender, RoutedEventArgs e) =>
        viewModel.Remove(ResultsGrid.SelectedItems.Cast<FolderRow>());

    private async void ExportCsv(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "导出统计结果", Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"文件夹大小-{DateTime.Now:yyyyMMdd-HHmmss}.csv", DefaultExt = ".csv", AddExtension = true, OverwritePrompt = true };
        try
        {
            if (dialog.ShowDialog(this) == true) await viewModel.ExportAsync(dialog.FileName);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!viewModel.IsRunning && !viewModel.IsExporting) return;
        e.Cancel = true;
        if (closePending) return;
        closePending = true;
        viewModel.Cancel();
        IsEnabled = false;
        if (viewModel.ActiveScan is { } scan) await scan;
        try { if (viewModel.ActiveExport is { } export) await export; }
        catch (Exception) { /* ExportCsv displays the export error to the user. */ }
        Close();
    }
}
