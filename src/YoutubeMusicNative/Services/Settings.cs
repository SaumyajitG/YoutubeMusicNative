using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YoutubeMusicNative.Infrastructure;

namespace YoutubeMusicNative.Services;

/// <summary>Plain preferences live in settings.json; credentials go to secrets.bin encrypted with DPAPI.</summary>
public sealed class Settings
{
    public static string Folder { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YoutubeMusicNative");

    private static string SettingsPath => System.IO.Path.Combine(Folder, "settings.json");
    private static string SecretsPath => System.IO.Path.Combine(Folder, "secrets.bin");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public double Volume { get; set; } = 70;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public bool WindowMaximized { get; set; }

    [JsonIgnore] public Secrets Secret { get; private set; } = new();

    public sealed class Secrets
    {
        public string? Cookie { get; set; }
    }

    public static Settings Load()
    {
        Settings s;
        try
        {
            s = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new()
                : new();
        }
        catch
        {
            s = new();
        }

        try
        {
            if (File.Exists(SecretsPath))
            {
                var json = Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(SecretsPath)));
                s.Secret = JsonSerializer.Deserialize<Secrets>(json) ?? new();
            }
        }
        catch
        {
            // Unreadable (e.g. different Windows user): start signed out.
        }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
            var secret = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Secret));
            File.WriteAllBytes(SecretsPath, Dpapi.Protect(secret));
        }
        catch
        {
            // Settings are best-effort; never crash the player over them.
        }
    }
}
