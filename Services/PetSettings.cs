using System.IO;
using System.Text.Json;

namespace ClaudePetOverlay.Services;

public sealed class PetSettings
{
    public double Scale { get; set; } = 2.5;
    public int TargetFps { get; set; } = 60;
    public bool SmoothInterpolation { get; set; }
    public bool Topmost { get; set; } = true;
    public bool ClickThrough { get; set; }
    public bool ShowSpeechBubble { get; set; } = true;
    public bool SoundEnabled { get; set; } = true;
    public double? Left { get; set; }
    public double? Top { get; set; }

    // CodexPetOverlay と設定を共有しない。並行起動時に位置やスケールが衝突するため。
    private static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudePetOverlay");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static PetSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<PetSettings>(File.ReadAllText(SettingsPath))
                       ?? new PetSettings();
            }
        }
        catch
        {
            // A damaged settings file should never prevent the pet from starting.
        }

        return new PetSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
