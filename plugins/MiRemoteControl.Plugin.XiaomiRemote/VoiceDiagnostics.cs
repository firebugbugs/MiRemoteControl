using System.Text;

namespace MiRemoteControl.Plugin.XiaomiRemote;

internal static class VoiceDiagnostics
{
    private static readonly object Sync = new();
    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiRemoteControl", "voice-diagnostics");

    public static void Log(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(Path.Combine(DirectoryPath, $"voice-{DateTime.Today:yyyyMMdd}.log"),
                    $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { /* diagnostics must never interrupt capture */ }
    }

    public static string? SaveCapture(byte[] pcm, byte[] adpcm)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                var stem = $"remote-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
                var wavPath = Path.Combine(DirectoryPath, stem + ".wav");
                using (var file = File.Create(wavPath))
                using (var writer = new BinaryWriter(file, Encoding.ASCII, leaveOpen: false))
                {
                    writer.Write("RIFF"u8); writer.Write(36 + pcm.Length); writer.Write("WAVE"u8);
                    writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                    writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
                    writer.Write("data"u8); writer.Write(pcm.Length); writer.Write(pcm);
                }
                File.WriteAllBytes(Path.Combine(DirectoryPath, stem + ".adpcm.bin"), adpcm);
                return wavPath;
            }
        }
        catch (Exception e) { Log($"SAVE-ERROR {e.GetType().Name}: {e.Message}"); return null; }
    }
}
