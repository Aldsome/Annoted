using System.Text;
using Annoted.Core.Interfaces;
using NAudio.Wave;
using Whisper.net;

namespace Annoted.Infrastructure.Services;

public sealed class DictationService : IDictationService
{
    public event EventHandler<DictationSegmentEventArgs>? SegmentReady;
    public event EventHandler<DictationLevelEventArgs>? LevelChanged;

    public bool IsDictating { get; private set; }

    /// <summary>WaveIn device index (-1 = system default).</summary>
    public int InputDeviceNumber { get; set; } = -1;

    /// <summary>Raised when the chosen input device fails and capture falls back to the default.</summary>
    public event EventHandler<string>? DeviceFellBack;

    private WaveInEvent CreateInput() => new()
    {
        WaveFormat = new WaveFormat(16000, 16, 1),
        BufferMilliseconds = 100,
        DeviceNumber = (InputDeviceNumber < 0 || InputDeviceNumber >= WaveInEvent.DeviceCount) ? 0 : InputDeviceNumber
    };

    private WaveInEvent? _input;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private System.Threading.Timer? _tickTimer;
    private readonly List<float> _buffer = new();
    private readonly object _lock = new();
    private volatile bool _busy;
    private volatile float _level;
    private bool _isDisposed;

    public async Task StartAsync(string modelPath)
    {
        if (IsDictating) return;

        _factory = await Task.Run(() => WhisperFactory.FromPath(modelPath));
        _processor = _factory.CreateBuilder().WithLanguage("auto").Build();

        lock (_lock) { _buffer.Clear(); }

        _input = CreateInput();
        _input.DataAvailable += OnDataAvailable;
        try
        {
            _input.StartRecording();
        }
        catch when (InputDeviceNumber >= 0)
        {
            // Chosen device unusable → fall back to system default so dictation still starts.
            var bad = InputDeviceNumber;
            try { _input.DataAvailable -= OnDataAvailable; _input.Dispose(); } catch { /* ignore */ }
            InputDeviceNumber = -1;
            _input = CreateInput();
            _input.DataAvailable += OnDataAvailable;
            _input.StartRecording();
            DeviceFellBack?.Invoke(this, $"Mic #{bad} unavailable — using default device.");
        }

        // Fire a transcription tick every second
        _tickTimer = new System.Threading.Timer(
            async _ => await TickAsync(final: false),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        IsDictating = true;
    }

    public async Task StopAsync()
    {
        if (!IsDictating) { Cleanup(); return; }
        IsDictating = false;

        _tickTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        try { _input?.StopRecording(); } catch { /* ignore */ }

        var spins = 0;
        while (_busy && spins++ < 200) await Task.Delay(25);

        await TickAsync(final: true);

        // Signal final with null text so the caller can finalize ghost text
        SegmentReady?.Invoke(this, new DictationSegmentEventArgs(string.Empty, isFinal: true));
        Cleanup();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var peak = 0F;
        lock (_lock)
        {
            for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                var sample = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
                var value = sample / 32768F;
                _buffer.Add(value);
                var mag = Math.Abs(value);
                if (mag > peak) peak = mag;
            }
        }
        if (peak > _level) _level = peak;
        LevelChanged?.Invoke(this, new DictationLevelEventArgs(peak));
    }

    // Whisper emits "[BLANK_AUDIO]" (and variants) for silence — strip before any output.
    private static readonly System.Text.RegularExpressions.Regex BlankAudioRegex =
        new(@"[\[\(]?\s*BLANK_AUDIO\s*[\]\)]?", System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string CleanTranscript(string text)
        => BlankAudioRegex.Replace(text, string.Empty).Trim();

    private async Task TickAsync(bool final)
    {
        var processor = _processor;
        if (_busy || processor is null) return;

        float[] snapshot;
        lock (_lock) { snapshot = _buffer.ToArray(); }

        // Whisper.cpp is unstable on very short clips — require ~0.75s of audio (16000 Hz * 0.75)
        if (snapshot.Length < 12000 && !final) return;
        if (snapshot.Length == 0) return;

        _busy = true;
        try
        {
            var text = await Task.Run(async () =>
            {
                var sb = new StringBuilder();
                await foreach (var segment in processor.ProcessAsync(snapshot))
                    sb.Append(segment.Text);
                return sb.ToString();
            });

            var trimmed = CleanTranscript(text);

            // Keep buffer bounded — commit every ~10s to free memory. Fire a final segment so
            // the caller commits the preview to the editor (parity with WinForms
            // FinalizeDictationPreview before clearing the buffer), then start a fresh preview.
            if (snapshot.Length > 16000 * 10)
            {
                if (!string.IsNullOrEmpty(trimmed))
                    SegmentReady?.Invoke(this, new DictationSegmentEventArgs(trimmed, isFinal: false));
                SegmentReady?.Invoke(this, new DictationSegmentEventArgs(string.Empty, isFinal: true));
                lock (_lock) { _buffer.Clear(); }
            }
            else if (!string.IsNullOrEmpty(trimmed))
            {
                SegmentReady?.Invoke(this, new DictationSegmentEventArgs(trimmed, isFinal: false));
            }
        }
        catch { /* ignore transcription errors */ }
        finally { _busy = false; }
    }

    private void Cleanup()
    {
        _tickTimer?.Dispose();
        _tickTimer = null;

        if (_input is not null)
        {
            _input.DataAvailable -= OnDataAvailable;
            try { _input.Dispose(); } catch { /* ignore */ }
            _input = null;
        }

        try { _processor?.Dispose(); } catch { /* ignore */ }
        _processor = null;
        _factory?.Dispose();
        _factory = null;

        lock (_lock) { _buffer.Clear(); }
        _level = 0F;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        IsDictating = false;
        Cleanup();
    }
}
