using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChromeRamReducer.Core;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Port passed to Chrome as --remote-debugging-port.</summary>
    public int DebuggingPort { get; set; } = 9222;

    /// <summary>
    /// Profile directory passed as --user-data-dir. Empty means the junction that points at the real
    /// profile, which is what lets Chrome 136 and later start the DevTools endpoint at all.
    /// </summary>
    public string UserDataDirectory { get; set; } = string.Empty;

    [JsonIgnore]
    public string EffectiveUserDataDirectory => string.IsNullOrWhiteSpace(UserDataDirectory)
        ? ChromeLocator.DebugProfileLink
        : UserDataDirectory;

    /// <summary>Run the aggressive V8 purge after the garbage collection pass.</summary>
    public bool PurgeJavaScriptMemory { get; set; } = true;

    /// <summary>
    /// Call EmptyWorkingSet on every Chrome process. Off by default: it lowers the Task Manager
    /// figure without releasing a single byte of committed memory.
    /// </summary>
    public bool EmptyWorkingSets { get; set; }

    public bool AutoTrimEnabled { get; set; }

    public int AutoTrimMinutes { get; set; } = 10;

    public bool MinimiseToTray { get; set; } = true;

    [JsonIgnore]
    public static string SettingsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ChromeRamReducer");

    [JsonIgnore]
    public static string SettingsPath { get; } = Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(SettingsPath), JsonOptions);

                if (loaded is not null)
                {
                    loaded.DebuggingPort = Math.Clamp(loaded.DebuggingPort, 1024, 65535);
                    loaded.AutoTrimMinutes = Math.Clamp(loaded.AutoTrimMinutes, 1, 720);
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings fall back to the defaults.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing preferences is not worth interrupting the user over.
        }
    }
}
