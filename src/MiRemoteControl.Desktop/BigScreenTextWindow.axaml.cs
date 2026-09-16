using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
namespace MiRemoteControl.Desktop;

public partial class BigScreenTextWindow : Window
{
    private bool _applyingSnapshot;
    public event Action<string>? TextEdited;
    public event Action? CloseRequested;
    public string CurrentText => EditorTextBox.Text ?? string.Empty;
    public int EditorCaretIndex => EditorTextBox.CaretIndex;

    public BigScreenTextWindow()
    {
        InitializeComponent();
        EditorTextBox.TextChanged += (_, _) =>
        {
            if (_applyingSnapshot) return;
            UpdateFontSize(CurrentText.Length);
            TextEdited?.Invoke(CurrentText);
        };
        Opened += (_, _) => FocusEditor();
    }

    public void ApplySnapshot(string? text, int? caretIndex = null)
    {
        var value = text ?? string.Empty;
        var changed = !string.Equals(CurrentText, value, StringComparison.Ordinal);
        _applyingSnapshot = true;
        try
        {
            if (changed) EditorTextBox.Text = value;
            // Do not pull the local caret back to ZCode's caret on an
            // acknowledgement of the same text. While TV is active, its
            // native TextBox owns caret navigation.
            if (changed || !IsVisible)
                EditorTextBox.CaretIndex = Math.Clamp(caretIndex ?? value.Length, 0, value.Length);
            UpdateFontSize(value.Length);
        }
        finally { _applyingSnapshot = false; }
    }

    public void FocusEditor()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(FocusEditor);
            return;
        }
        Activate();
        EditorTextBox.Focus();
    }

    public void MoveCaret(string direction)
    {
        var key = direction.ToLowerInvariant() switch
        {
            "left" => Key.Left,
            "right" => Key.Right,
            "up" => Key.Up,
            "down" => Key.Down,
            _ => Key.None
        };
        if (key == Key.None) return;
        // Simulated remote buttons are rare; use the TextBox's line helpers
        // for deterministic caret movement without inserting any character.
        var caret = EditorTextBox.CaretIndex;
        if (key == Key.Left) EditorTextBox.CaretIndex = Math.Max(0, caret - 1);
        else if (key == Key.Right) EditorTextBox.CaretIndex = Math.Min(CurrentText.Length, caret + 1);
        else EditorTextBox.CaretIndex = MoveVertically(CurrentText, caret, key == Key.Up ? -1 : 1);
        EditorTextBox.SelectionStart = EditorTextBox.CaretIndex;
        EditorTextBox.SelectionEnd = EditorTextBox.CaretIndex;
        FocusEditor();
    }

    private static int MoveVertically(string text, int caret, int direction)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, caret - 1)) + 1;
        var column = caret - lineStart;
        if (direction < 0)
        {
            if (lineStart == 0) return caret;
            var previousEnd = lineStart - 1;
            var previousStart = text.LastIndexOf('\n', Math.Max(0, previousEnd - 1)) + 1;
            return Math.Min(previousStart + column, previousEnd);
        }
        var lineEnd = text.IndexOf('\n', caret);
        if (lineEnd < 0) return caret;
        var nextStart = lineEnd + 1;
        var nextEnd = text.IndexOf('\n', nextStart);
        if (nextEnd < 0) nextEnd = text.Length;
        return Math.Min(nextStart + column, nextEnd);
    }

    private void UpdateFontSize(int length)
    {
        var fontSize = length switch
        {
            <= 10 => 220d,
            <= 24 => 180d,
            <= 50 => 140d,
            <= 100 => 110d,
            <= 180 => 86d,
            _ => 68d
        };
        EditorTextBox.FontSize = fontSize;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseRequested?.Invoke();
    }
}
