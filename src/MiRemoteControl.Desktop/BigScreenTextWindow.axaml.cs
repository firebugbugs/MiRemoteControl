using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
namespace MiRemoteControl.Desktop;

public partial class BigScreenTextWindow : Window
{
    private bool _applyingSnapshot;
    public event Action<string>? TextEdited;
    public event Action<int>? CaretChanged;
    public event Action? CloseRequested;
    public string CurrentText => EditorTextBox.Text ?? string.Empty;
    public int EditorCaretIndex => EditorTextBox.CaretIndex;
    /// <summary>When the editor last handled a caret key by itself.</summary>
    public DateTimeOffset LastNavigationAt { get; private set; }

    public BigScreenTextWindow()
    {
        InitializeComponent();
        EditorTextBox.TextChanged += (_, _) =>
        {
            if (_applyingSnapshot) return;
            UpdateFontSize(CurrentText.Length);
            TextEdited?.Invoke(CurrentText);
        };
        // Moving the caret does not change the text, but the mirror needs to
        // know where it is so a later insertion (voice) lands there.
        EditorTextBox.PropertyChanged += (_, args) =>
        {
            if (_applyingSnapshot) return;
            if (args.Property != TextBox.CaretIndexProperty &&
                args.Property != TextBox.SelectionStartProperty &&
                args.Property != TextBox.SelectionEndProperty) return;
            CaretChanged?.Invoke(EditorTextBox.CaretIndex);
        };
        // Tunnel handler: the TextBox marks arrow keys as handled, so a normal
        // bubbling handler never sees them. The remote event loop uses this
        // stamp to avoid moving the caret twice when the key did arrive as a
        // real keyboard event.
        EditorTextBox.AddHandler(
            KeyDownEvent,
            OnEditorPreviewKeyDown,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) => FocusEditor();
    }

    private void OnEditorPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // A bare Return must never reach the editor: the remote's confirm key can
        // arrive as a real keyboard Return, and this screen owns that gesture.
        // The stray newline used to become part of the draft, which was then
        // committed and sent. Shift+Return still inserts a line break on purpose.
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
            LastNavigationAt = DateTimeOffset.UtcNow;
    }

    public void ApplySnapshot(string? text, int? caretIndex = null)
    {
        var value = text ?? string.Empty;
        var previous = CurrentText;
        var previousCaret = EditorTextBox.CaretIndex;
        var changed = !string.Equals(previous, value, StringComparison.Ordinal);
        _applyingSnapshot = true;
        try
        {
            if (changed)
            {
                EditorTextBox.Text = value;
                // Place the caret and the selection together: Avalonia's
                // TextBox keeps the caret in sync with the selection, so
                // setting CaretIndex alone gets pulled back to the old offset.
                var target = caretIndex ?? InferCaretAfterChange(previous, value, previousCaret);
                target = Math.Clamp(target, 0, value.Length);
                EditorTextBox.CaretIndex = target;
                EditorTextBox.SelectionStart = target;
                EditorTextBox.SelectionEnd = target;
            }
            UpdateFontSize(value.Length);
        }
        finally { _applyingSnapshot = false; }
    }

    /// <summary>
    /// Fallback for snapshots that do not say where the caret belongs: when the
    /// new text is the old one plus something inserted, keep the user's place by
    /// moving the caret behind that insertion. A pure append lands at the end;
    /// anything else stays near the previous offset.
    /// </summary>
    private static int InferCaretAfterChange(string previous, string value, int previousCaret)
    {
        var limit = Math.Min(previous.Length, value.Length);
        var prefix = 0;
        while (prefix < limit && previous[prefix] == value[prefix]) prefix++;
        var suffix = 0;
        while (suffix < limit - prefix &&
               previous[previous.Length - 1 - suffix] == value[value.Length - 1 - suffix]) suffix++;
        // Everything of the old text is still present, in order: an insertion.
        if (value.Length >= previous.Length && prefix + suffix >= previous.Length)
            return value.Length - suffix;
        return Math.Clamp(previousCaret, 0, value.Length);
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

    /// <summary>
    /// Deletes the selection, or the text element before the caret, exactly
    /// like the Back key on a normal keyboard.
    /// </summary>
    public void DeleteBackward()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(DeleteBackward);
            return;
        }
        var text = CurrentText;
        var selectionStart = Math.Min(EditorTextBox.SelectionStart, EditorTextBox.SelectionEnd);
        var selectionEnd = Math.Max(EditorTextBox.SelectionStart, EditorTextBox.SelectionEnd);
        int removeStart, removeEnd;
        if (selectionStart != selectionEnd)
        {
            removeStart = selectionStart;
            removeEnd = selectionEnd;
        }
        else
        {
            var caret = EditorTextBox.CaretIndex;
            if (caret <= 0) return;
            removeStart = caret - 1;
            // Keep surrogate pairs (emoji) intact.
            if (removeStart > 0 && char.IsLowSurrogate(text[removeStart]) && char.IsHighSurrogate(text[removeStart - 1]))
                removeStart--;
            removeEnd = caret;
        }
        var updated = text.Remove(removeStart, removeEnd - removeStart);
        _applyingSnapshot = true;
        try
        {
            EditorTextBox.Text = updated;
            EditorTextBox.CaretIndex = removeStart;
            EditorTextBox.SelectionStart = removeStart;
            EditorTextBox.SelectionEnd = removeStart;
        }
        finally { _applyingSnapshot = false; }
        UpdateFontSize(updated.Length);
        TextEdited?.Invoke(updated);
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
