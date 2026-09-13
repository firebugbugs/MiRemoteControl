using System.Text.Json;
using MiRemoteControl.Contracts;

namespace MiRemoteControl.Plugin.XiaomiRemote;

public sealed record RemoteSettings(
    RemoteKey? VoiceKey = null,
    int? AudioDevice = null,
    int? BatteryLevel = null,
    DateTimeOffset? BatteryUpdatedAt = null);

public sealed class RemoteSettingsStore
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiRemoteControl", "config", "remote.json");
    private readonly object _sync = new();

    public RemoteSettings Load()
    {
        lock (_sync)
        {
            RemoteSettings settings;
            try { settings = JsonSerializer.Deserialize<RemoteSettings>(File.ReadAllText(_path), Wire.Json) ?? new(); }
            catch { settings = new(); }

            if (settings.BatteryLevel is >= 0 and <= 100) return settings;
            if (!TryLoadLegacyBattery(out var level, out var updatedAt)) return settings;

            settings = settings with { BatteryLevel = level, BatteryUpdatedAt = updatedAt };
            Save(settings);
            return settings;
        }
    }

    public void Save(RemoteSettings settings)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".new";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Wire.Json));
            File.Move(temporary, _path, true);
        }
    }

    private static bool TryLoadLegacyBattery(out int level, out DateTimeOffset updatedAt)
    {
        level = 0;
        updatedAt = default;
        const string marker = " BATTERY level=";
        try
        {
            var files = Directory.EnumerateFiles(VoiceDiagnostics.DirectoryPath, "voice-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc);
            foreach (var file in files)
            {
                var lines = File.ReadAllLines(file);
                for (var index = lines.Length - 1; index >= 0; index--)
                {
                    var line = lines[index];
                    var markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
                    if (markerIndex <= 0) continue;
                    var valueStart = markerIndex + marker.Length;
                    var valueEnd = line.IndexOf('%', valueStart);
                    if (valueEnd <= valueStart ||
                        !int.TryParse(line[valueStart..valueEnd], out level) ||
                        level is < 0 or > 100 ||
                        !DateTimeOffset.TryParse(line[..markerIndex], out updatedAt)) continue;
                    return true;
                }
            }
        }
        catch { /* legacy diagnostics are optional */ }

        return false;
    }
}
