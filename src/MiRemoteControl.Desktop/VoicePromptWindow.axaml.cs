using Avalonia.Controls;

namespace MiRemoteControl.Desktop;

// Corner overlay for the voice-recognition prompt. It is display-only: it
// never takes focus, never enters the taskbar and never touches the mirrored
// composer text, so it can appear and disappear instantly while speaking.
public partial class VoicePromptWindow : Window
{
    public VoicePromptWindow() => InitializeComponent();
}
