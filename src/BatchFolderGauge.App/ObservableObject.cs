using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace BatchFolderGauge.App;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class RelayCommand(Action execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute();
    public void Execute(object? parameter) { if (canExecute()) execute(); }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
