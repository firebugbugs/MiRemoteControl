using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MiRemoteControl.Desktop;

public partial class AboutWindow : Window
{
    public AboutWindow() => InitializeComponent();

    private void TitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void CloseAbout(object? sender, RoutedEventArgs e) => Close();

    private void OpenLink(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string url } || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Opening a browser is best effort; the about dialog remains usable offline.
        }
    }
}
