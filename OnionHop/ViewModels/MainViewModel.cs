using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OnionHop;

internal sealed class MainViewModel : INotifyPropertyChanged
{
    private bool _isConnected;
    private string _selectedLocation = "Automatic";

    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<string> DnsLogLines { get; } = new();

    public bool IsConnected
    {
        get => _isConnected;
        set => SetField(ref _isConnected, value);
    }

    public string SelectedLocation
    {
        get => _selectedLocation;
        set => SetField(ref _selectedLocation, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
