using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClickUpTodo.Configuration;

/// <summary>
/// Loads and saves <see cref="AppConfig"/> as <c>config.json</c> under the per-user app data
/// directory (<c>%APPDATA%\clickup-todo</c> on Windows, <c>~/.config/clickup-todo</c> elsewhere).
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Persist enums (the F3 view's fields/operators) as readable strings, not ordinals.
        Converters = { new JsonStringEnumConverter() },
    };

    public string DirectoryPath { get; }
    public string ConfigPath { get; }

    public ConfigStore(string? directoryPath = null)
    {
        DirectoryPath = directoryPath ?? DefaultDirectory();
        ConfigPath = Path.Combine(DirectoryPath, "config.json");
    }

    /// <summary>The shared data directory, used for both config and the encrypted token.</summary>
    public static string DefaultDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(root, "clickup-todo");
    }

    public bool Exists() => File.Exists(ConfigPath);

    public AppConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return new AppConfig();
        var json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }
}
