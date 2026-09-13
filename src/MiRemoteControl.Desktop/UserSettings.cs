using System.Text.Json;

namespace MiRemoteControl.Desktop;

public sealed class UserSettings
{
    public bool IsDarkTheme { get; set; }
}

internal static class UserSettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MiRemoteControl");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public static UserSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                var defaults = new UserSettings();
                Save(defaults);
                return defaults;
            }
            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath), SerializerOptions)
                   ?? new UserSettings();
        }
        catch
        {
            // A malformed user file must never prevent the desktop app from starting.
            return new UserSettings();
        }
    }

    public static void Save(UserSettings settings)
    {
        var temporaryPath = $"{SettingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, SerializerOptions));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch
        {
            // Theme changes remain usable for the current session if persistence fails.
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch { /* Best effort cleanup of an incomplete temporary write. */ }
        }
    }
}
