using Avalonia.Controls;
using Avalonia.Input;
namespace MiRemoteControl.Desktop;

public partial class BigScreenTextWindow : Window
{
    public BigScreenTextWindow() => InitializeComponent();

    public void SetText(string? text)
    {
        var display = string.IsNullOrEmpty(text) ? "暂无输入内容" : text;
        DisplayTextBlock.Text = display;

        var fontSize = display.Length switch
        {
            <= 10 => 220d,
            <= 24 => 180d,
            <= 50 => 140d,
            <= 100 => 110d,
            <= 180 => 86d,
            _ => 68d
        };
        DisplayTextBlock.FontSize = fontSize;
        DisplayTextBlock.LineHeight = fontSize * 1.22;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
