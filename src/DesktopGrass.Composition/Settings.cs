// Settings.cs - the few choices worth remembering between runs (state.json next to the log).
using System;
using System.IO;
using System.Text.Json;

namespace DesktopGrass.Composition;

internal sealed class Settings
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    public string Scene { get; set; } = "Meadow";
    /// <summary>"Full" or "Calm"; null follows the Windows animation-effects setting.</summary>
    public string? Motion { get; set; }
    public bool Critters { get; set; } = true;
    public bool Sky { get; set; } = true;

    public static Settings Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), s_json) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Write($"settings: ignoring unreadable {path}: {ex.Message}");
        }
        return new Settings();
    }

    public void Save(string path)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, s_json));
        }
        catch (Exception ex)
        {
            Log.Write($"settings: save failed: {ex.Message}");
        }
    }
}
