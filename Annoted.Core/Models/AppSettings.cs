namespace Annoted.Core.Models;

public sealed class AppSettings
{
    public string? StorageFolder { get; set; }
    public bool IsDarkMode { get; set; }
    public bool IsWordWrap { get; set; }
    public string? FontFamily { get; set; }
    public float FontSize { get; set; }
    public int FontStyle { get; set; }
    public int? CustomEditorBackArgb { get; set; }
    public int? CustomEditorForeArgb { get; set; }
    public int? CustomAccentArgb { get; set; }
    public string? WhisperModelType { get; set; }
    /// <summary>Selected WaveIn input device index (-1 = system default).</summary>
    public int InputDeviceNumber { get; set; } = -1;
    /// <summary>"system" (follow OS), "light", or "dark". Default follows the OS.</summary>
    public string ThemeMode { get; set; } = "system";
    /// <summary>Preview playback volume (0..1). Playback only.</summary>
    public double PreviewVolume { get; set; } = 1.0;
}
