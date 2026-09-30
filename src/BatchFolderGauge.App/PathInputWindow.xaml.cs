using System.Windows;

namespace BatchFolderGauge.App;

public partial class PathInputWindow : Window
{
    public string PathsText => PathsBox.Text;
    public PathInputWindow() { InitializeComponent(); Loaded += (_, _) => PathsBox.Focus(); }
    private void AcceptPaths(object sender, RoutedEventArgs e) => DialogResult = true;
}
