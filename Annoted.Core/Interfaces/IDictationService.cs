namespace Annoted.Core.Interfaces;

public interface IDictationService : IDisposable
{
    event EventHandler<DictationSegmentEventArgs>? SegmentReady;
    event EventHandler<DictationLevelEventArgs>? LevelChanged;

    bool IsDictating { get; }

    /// <summary>WaveIn device index to capture from (-1 = system default).</summary>
    int InputDeviceNumber { get; set; }

    Task StartAsync(string modelPath);
    Task StopAsync();
}

/// <summary>A selectable audio input device. Number is internal; UI shows Name only.</summary>
public sealed record AudioInputDevice(int Number, string Name)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? "Unknown microphone" : Name;
}

public sealed class DictationSegmentEventArgs(string text, bool isFinal) : EventArgs
{
    public string Text { get; } = text;
    public bool IsFinal { get; } = isFinal;
}

public sealed class DictationLevelEventArgs(float level) : EventArgs
{
    public float Level { get; } = level;
}
