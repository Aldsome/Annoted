using Annoted.Core.Models;

namespace Annoted.Core.Interfaces;

public interface IAudioService : IDisposable
{
    event EventHandler<AudioLevelEventArgs>? LevelChanged;
    event EventHandler<PlaybackProgressEventArgs>? PlaybackProgressChanged;
    event EventHandler? PlaybackStopped;
    event EventHandler<WaveformReadyEventArgs>? WaveformReady;

    bool IsRecording { get; }
    bool IsRecordingPaused { get; }
    bool IsPlaying { get; }

    /// <summary>WaveIn device index to record from (-1 = system default).</summary>
    int InputDeviceNumber { get; set; }

    void StartRecording(string filePath, AudioMemoModel memo);
    void StopRecording();
    void PauseRecording();
    void ResumeRecording();

    void StartPlayback(string audioFilePath);
    void StopPlayback();
    void SeekPlayback(double ratio);

    void ExportRecording(string sourcePath, string destinationPath);
    Task UpdateWaveformAsync(string? audioFilePath, bool darkMode, int accentArgb, CancellationToken token);
    byte[]? GetCurrentWaveformBitmap();
}

public sealed class AudioLevelEventArgs(float level) : EventArgs
{
    public float Level { get; } = level;
}

public sealed class PlaybackProgressEventArgs(TimeSpan position, TimeSpan duration) : EventArgs
{
    public TimeSpan Position { get; } = position;
    public TimeSpan Duration { get; } = duration;
    public double Ratio => Duration.TotalSeconds > 0 ? Position.TotalSeconds / Duration.TotalSeconds : 0;
}

public sealed class WaveformReadyEventArgs(byte[] bitmapBytes) : EventArgs
{
    public byte[] BitmapBytes { get; } = bitmapBytes;
}
