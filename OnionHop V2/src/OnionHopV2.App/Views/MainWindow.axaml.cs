using Avalonia.Input;
using Avalonia.Interactivity;
using SukiUI.Controls;
using OnionHopV2.App.ViewModels;

namespace OnionHopV2.App.Views;

public partial class MainWindow : SukiWindow
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = Avalonia.Controls.WindowState.Minimized;
    }

    private void OnMaximizeRestoreClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == Avalonia.Controls.WindowState.Maximized
            ? Avalonia.Controls.WindowState.Normal
            : Avalonia.Controls.WindowState.Maximized;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell && shell.State.MinimizeToTray)
        {
            Hide();
            return;
        }

        Close();
    }

    private void OnDragRegionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
