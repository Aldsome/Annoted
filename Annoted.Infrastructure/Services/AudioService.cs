using System.Drawing;
using System.Drawing.Drawing2D;
using Annoted.Core.Interfaces;
using Annoted.Core.Models;
using NAudio.Wave;

namespace Annoted.Infrastructure.Services;

public sealed class AudioService : IAudioService
{
    private const int PrimaryRecordingSampleRate = 48_000;
    private const int FallbackRecordingSampleRate = 44_100;
    private const int RecordingBitsPerSample = 16;
    private const int RecordingChannels = 2;
    private const int WaveformPreviewWidth = 320;
    private const int WaveformPreviewHeight = 150;
    private const int WaveformPreviewWindowFrames = 512;

    public event EventHandler<AudioLevelEventArgs>? LevelChanged;
#pragma warning disable CS0067
    public event EventHandler<PlaybackProgressEventArgs>? PlaybackProgressChanged;
#pragma warning restore CS0067
    public event EventHandler? PlaybackStopped;
    public event EventHandler<WaveformReadyEventArgs>? WaveformReady;

    public bool IsRecording { get; private set; }
    public bool IsRecordingPaused { get; private set; }
    public bool IsPlaying => _audioPlayer?.PlaybackState == PlaybackState.Playing;

    /// <summary>WaveIn device index (-1 = system default).</summary>
    public int InputDeviceNumber { get; set; } = -1;

    private WaveInEvent? _audioRecorder;
    private WaveFileWriter? _audioWriter;
    private WaveFormat? _activeRecordingWaveFormat;
    private WaveOutEvent? _audioPlayer;
    private AudioFileReader? _audioReader;
    private string? _activePlaybackPath;
    private string? _waveformCachedPath;
    private CancellationTokenSource? _waveformCts;
    private bool _isDisposed;

    // ── Recording ────────────────────────────────────────────────────────────

    /// <summary>Raised when the chosen input device fails and capture falls back to the default.</summary>
    public event EventHandler<string>? DeviceFellBack;

    public void StartRecording(string filePath, AudioMemoModel memo)
    {
        StopPlayback();

        // Try chosen device at primary then fallback sample rate; if the device itself is
        // unusable, fall back to the system default so recording never silently dies.
        if (TryStart(filePath, PrimaryRecordingSampleRate) || TryStart(filePath, FallbackRecordingSampleRate))
        {
            IsRecording = true;
            IsRecordingPaused = false;
            return;
        }

        if (InputDeviceNumber >= 0)
        {
            var bad = InputDeviceNumber;
            InputDeviceNumber = -1; // reset to default for this and future sessions
            CleanupRecordingResources();
            if (TryStart(filePath, PrimaryRecordingSampleRate) || TryStart(filePath, FallbackRecordingSampleRate))
            {
                IsRecording = true;
                IsRecordingPaused = false;
                DeviceFellBack?.Invoke(this, $"Mic #{bad} unavailable — using default device.");
                return;
            }
        }

        CleanupRecordingResources();
        throw new InvalidOperationException("No usable audio input device for recording.");
    }

    private bool TryStart(string filePath, int sampleRate)
    {
        try { StartRecordingWithFormat(filePath, sampleRate); return true; }
        catch { CleanupRecordingResources(); return false; }
    }

    private int ResolveDeviceNumber()
    {
        // -1 (or out of range) → NAudio's default device (0). Otherwise honor the chosen index.
        if (InputDeviceNumber < 0 || InputDeviceNumber >= WaveInEvent.DeviceCount) return 0;
        return InputDeviceNumber;
    }

    private void StartRecordingWithFormat(string filePath, int sampleRate)
    {
        var waveFormat = new WaveFormat(sampleRate, RecordingBitsPerSample, RecordingChannels);
        _activeRecordingWaveFormat = waveFormat;
        _audioWriter = new WaveFileWriter(filePath, waveFormat);
        _audioRecorder = new WaveInEvent { BufferMilliseconds = 50, WaveFormat = waveFormat, DeviceNumber = ResolveDeviceNumber() };
        _audioRecorder.DataAvailable += AudioRecorder_DataAvailable;
        _audioRecorder.StartRecording();
    }

    public void StopRecording()
    {
        if (!IsRecording && !IsRecordingPaused) return;
        try { if (IsRecording) _audioRecorder?.StopRecording(); } catch { /* ignore */ }
        CleanupRecordingResources();
        IsRecording = false;
        IsRecordingPaused = false;
    }

    public void PauseRecording()
    {
        if (!IsRecording || IsRecordingPaused) return;
        try { _audioRecorder?.StopRecording(); } catch { /* ignore */ }
        CleanupRecordingInput();
        IsRecording = false;
        IsRecordingPaused = true;
    }

    public void ResumeRecording()
    {
        if (!IsRecordingPaused || _activeRecordingWaveFormat is null || _audioWriter is null) return;
        _audioRecorder = new WaveInEvent { BufferMilliseconds = 50, WaveFormat = _activeRecordingWaveFormat, DeviceNumber = ResolveDeviceNumber() };
        _audioRecorder.DataAvailable += AudioRecorder_DataAvailable;
        _audioRecorder.StartRecording();
        IsRecording = true;
        IsRecordingPaused = false;
    }

    private void AudioRecorder_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_audioWriter is null) return;

        // Always report input peak so the visualizer reacts on every recording path.
        ReportInputLevel(e.Buffer, e.BytesRecorded);

        var inputCh = _audioRecorder!.WaveFormat.Channels;
        var outputCh = _audioWriter.WaveFormat.Channels;

        if (inputCh == 1 && outputCh == 2)
        {
            var stereo = new byte[e.BytesRecorded * 2];
            for (var i = 0; i < e.BytesRecorded; i += 2)
            {
                stereo[i * 2]     = e.Buffer[i];
                stereo[i * 2 + 1] = e.Buffer[i + 1];
                stereo[i * 2 + 2] = e.Buffer[i];
                stereo[i * 2 + 3] = e.Buffer[i + 1];
            }
            _audioWriter.Write(stereo, 0, stereo.Length);
            return;
        }

        if (inputCh == 2 && outputCh == 2)
        {
            WriteStereoAsCenteredMono(e.Buffer, e.BytesRecorded);
            return;
        }

        _audioWriter.Write(e.Buffer, 0, e.BytesRecorded);
    }

    private void ReportInputLevel(byte[] buffer, int bytesRecorded)
    {
        var peak = 0F;
        for (var i = 0; i + 1 < bytesRecorded; i += 2)
        {
            var s = (short)(buffer[i] | (buffer[i + 1] << 8));
            var v = Math.Abs(s / 32768F);
            if (v > peak) peak = v;
        }
        LevelChanged?.Invoke(this, new AudioLevelEventArgs(peak));
    }

    private void WriteStereoAsCenteredMono(byte[] buffer, int bytesRecorded)
    {
        if (_audioWriter is null) return;
        var frameCount = bytesRecorded / 4;
        var output = new byte[frameCount * 4];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var src = frame * 4;
            var left  = (short)(buffer[src] | (buffer[src + 1] << 8));
            var right = (short)(buffer[src + 2] | (buffer[src + 3] << 8));
            var mono = Math.Clamp(left + right, (int)short.MinValue, (int)short.MaxValue);
            var lo = (byte)(mono & 0xFF);
            var hi = (byte)((mono >> 8) & 0xFF);
            var dst = frame * 4;
            output[dst] = lo; output[dst + 1] = hi;
            output[dst + 2] = lo; output[dst + 3] = hi;
        }
        _audioWriter.Write(output, 0, output.Length);
    }

    // ── Playback ─────────────────────────────────────────────────────────────

    private float _playbackVolume = 1f;

    /// <summary>Preview playback volume (0..1). Affects playback only, never the recorded file.</summary>
    public float PlaybackVolume
    {
        get => _playbackVolume;
        set
        {
            _playbackVolume = Math.Clamp(value, 0f, 1f);
            if (_audioReader is not null) _audioReader.Volume = _playbackVolume;
        }
    }

    /// <summary>Starts playback already positioned at <paramref name="ratio"/> (0..1) — no start-then-jump.</summary>
    public void StartPlaybackAt(string audioFilePath, double ratio)
    {
        StopPlayback();
        _audioReader = new AudioFileReader(audioFilePath) { Volume = _playbackVolume };
        _audioReader.CurrentTime = TimeSpan.FromTicks((long)(_audioReader.TotalTime.Ticks * Math.Clamp(ratio, 0, 1)));
        _audioPlayer = new WaveOutEvent();
        _audioPlayer.PlaybackStopped += OnPlaybackStopped;
        _audioPlayer.Init(_audioReader);
        _activePlaybackPath = audioFilePath;
        _audioPlayer.Play();
    }

    public void StartPlayback(string audioFilePath)
    {
        StopPlayback();
        _audioReader = new AudioFileReader(audioFilePath) { Volume = _playbackVolume };
        _audioPlayer = new WaveOutEvent();
        _audioPlayer.PlaybackStopped += OnPlaybackStopped;
        _audioPlayer.Init(_audioReader);
        _activePlaybackPath = audioFilePath;
        _audioPlayer.Play();
    }

    public void StopPlayback()
    {
        if (_audioPlayer is not null)
        {
            _audioPlayer.PlaybackStopped -= OnPlaybackStopped;
            try { _audioPlayer.Stop(); } catch { /* ignore */ }
        }
        CleanupPlaybackResources();
    }

    public void SeekPlayback(double ratio)
    {
        if (_audioReader is null || _audioPlayer is null) return;
        _audioReader.CurrentTime = TimeSpan.FromTicks((long)(_audioReader.TotalTime.Ticks * Math.Clamp(ratio, 0, 1)));
    }

    public PlaybackProgressEventArgs? GetPlaybackProgress()
    {
        if (_audioReader is null || _audioPlayer is null) return null;
        return new PlaybackProgressEventArgs(_audioReader.CurrentTime, _audioReader.TotalTime);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        CleanupPlaybackResources();
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    // ── Export ────────────────────────────────────────────────────────────────

    public void ExportRecording(string sourcePath, string destinationPath)
        => File.Copy(sourcePath, destinationPath, overwrite: true);

    // ── Waveform ──────────────────────────────────────────────────────────────

    public async Task UpdateWaveformAsync(string? audioFilePath, bool darkMode, int accentArgb, CancellationToken token)
    {
        // Re-render even for the same path if the accent changed (so theme/accent edits apply).
        var cacheKey = $"{audioFilePath}|{darkMode}|{accentArgb}";
        if (string.Equals(_waveformCachedPath, cacheKey, StringComparison.OrdinalIgnoreCase)) return;
        _waveformCachedPath = cacheKey;

        _waveformCts?.Cancel();
        _waveformCts?.Dispose();
        _waveformCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cts = _waveformCts;

        byte[]? bitmapBytes;
        try
        {
            bitmapBytes = await Task.Run(() => CreateWaveformBytes(audioFilePath, darkMode, accentArgb, cts.Token), cts.Token);
        }
        catch (OperationCanceledException) { return; }

        if (cts.Token.IsCancellationRequested) return;
        if (bitmapBytes is not null)
            WaveformReady?.Invoke(this, new WaveformReadyEventArgs(bitmapBytes));
    }

    public byte[]? GetCurrentWaveformBitmap() => null; // caller uses event

    private static byte[]? CreateWaveformBytes(string? audioFilePath, bool dark, int accentArgb, CancellationToken token)
    {
        var accent = Color.FromArgb(255, (byte)((accentArgb >> 16) & 0xFF), (byte)((accentArgb >> 8) & 0xFF), (byte)(accentArgb & 0xFF));
        var backColor = dark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(248, 248, 250);
        using var bitmap = new Bitmap(WaveformPreviewWidth, WaveformPreviewHeight);
        try
        {
            token.ThrowIfCancellationRequested();
            if (audioFilePath is null)
            {
                using var g2 = Graphics.FromImage(bitmap);
                g2.Clear(backColor);
                using var tb = new SolidBrush(dark ? Color.FromArgb(170, 170, 170) : Color.Gray);
                using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g2.DrawString("No waveform", SystemFonts.DefaultFont, tb, new RectangleF(0, 0, WaveformPreviewWidth, WaveformPreviewHeight), fmt);
            }
            else
            {
                using var reader = new AudioFileReader(audioFilePath);
                var peaks = ReadWaveformPeaks(reader, WaveformPreviewWidth);
                token.ThrowIfCancellationRequested();
                using var g = Graphics.FromImage(bitmap);
                g.Clear(backColor);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var centerY = bitmap.Height / 2;
                using var centerPen = new Pen(dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(225, 225, 225));
                using var wavePen = new Pen(accent, 1.4F);
                g.DrawLine(centerPen, 0, centerY, bitmap.Width, centerY);
                for (var x = 0; x < peaks.Length && x < bitmap.Width; x++)
                {
                    var amp = Math.Max(1F, peaks[x] * (bitmap.Height - 12) / 2F);
                    g.DrawLine(wavePen, x, centerY - (int)amp, x, centerY + (int)amp);
                }
            }

            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static float[] ReadWaveformPeaks(AudioFileReader reader, int width)
    {
        var peaks = new float[width];
        var channels = Math.Max(1, reader.WaveFormat.Channels);
        var buffer = new float[WaveformPreviewWindowFrames * channels];
        if (reader.TotalTime <= TimeSpan.Zero) return peaks;
        for (var pixel = 0; pixel < width; pixel++)
        {
            reader.CurrentTime = TimeSpan.FromTicks((long)(reader.TotalTime.Ticks * ((double)pixel / width)));
            var samplesRead = reader.Read(buffer, 0, buffer.Length);
            var peak = 0F;
            for (var i = 0; i < samplesRead; i++) peak = Math.Max(peak, Math.Abs(buffer[i]));
            peaks[pixel] = Math.Clamp(peak, 0F, 1F);
        }
        return peaks;
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void CleanupRecordingResources()
    {
        CleanupRecordingInput();
        _audioWriter?.Dispose();
        _audioWriter = null;
        IsRecording = false;
        IsRecordingPaused = false;
        _activeRecordingWaveFormat = null;
    }

    private void CleanupRecordingInput()
    {
        if (_audioRecorder is null) return;
        _audioRecorder.DataAvailable -= AudioRecorder_DataAvailable;
        _audioRecorder.Dispose();
        _audioRecorder = null;
    }

    private void CleanupPlaybackResources()
    {
        if (_audioPlayer is not null)
        {
            _audioPlayer.PlaybackStopped -= OnPlaybackStopped;
            _audioPlayer.Dispose();
            _audioPlayer = null;
        }
        _audioReader?.Dispose();
        _audioReader = null;
        _activePlaybackPath = null;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        CleanupRecordingResources();
        CleanupPlaybackResources();
        _waveformCts?.Cancel();
        _waveformCts?.Dispose();
    }

    /// <summary>Enumerates available WaveIn capture devices, with a leading "System default" entry (-1).</summary>
    public static IReadOnlyList<Annoted.Core.Interfaces.AudioInputDevice> EnumerateInputDevices()
    {
        var list = new List<Annoted.Core.Interfaces.AudioInputDevice>
        {
            new(-1, "System default")
        };
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            try
            {
                var name = WaveInEvent.GetCapabilities(i).ProductName;
                list.Add(new(i, string.IsNullOrWhiteSpace(name) ? "Unknown microphone" : name));
            }
            catch { /* skip unreadable device */ }
        }
        return list;
    }
}
