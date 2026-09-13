using Avalonia.Controls;
using Avalonia.Input;
using ControlHub.Desktop.ViewModels;
using FluentAvalonia.UI.Controls;

namespace ControlHub.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var nav = this.FindControl<FANavigationView>("Nav");
        if (nav is not null)
        {
            nav.SelectionChanged += (_, e) =>
            {
                if (e.SelectedItem is FANavigationViewItem item && item.Tag is string tag
                    && DataContext is MainWindowViewModel vm)
                {
                    vm.NavigateCommand.Execute(tag);
                }
            };
            Loaded += (_, _) => nav.SelectedItem = nav.MenuItems.ElementAt(0);
        }

        // 标题栏拖拽
        PointerPressed += (_, e) =>
        {
            if (e.GetPosition(this).Y < 48)
            {
                BeginMoveDrag(e);
            }
        };
    }

    private void Minimize_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Maximize_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Close();
}
