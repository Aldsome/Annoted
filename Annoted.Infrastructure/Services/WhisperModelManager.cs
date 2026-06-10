using Annoted.Core.Interfaces;
using Whisper.net.Ggml;

namespace Annoted.Infrastructure.Services;

public sealed class WhisperModelManager : IWhisperModelManager
{
    private readonly string _modelsFolder;

    private static readonly WhisperModelInfo[] Models =
    [
        new("tiny",   "Tiny",   "~75 MB",   75L  << 20),
        new("base",   "Base",   "~142 MB",  142L << 20),
        new("small",  "Small",  "~466 MB",  466L << 20),
        new("medium", "Medium", "~1.5 GB",  1500L << 20),
        new("large",  "Large",  "~3 GB",    3000L << 20),
    ];

    private static readonly Dictionary<string, GgmlType> KeyToGgml = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tiny"]   = GgmlType.Tiny,
        ["base"]   = GgmlType.Base,
        ["small"]  = GgmlType.Small,
        ["medium"] = GgmlType.Medium,
        ["large"]  = GgmlType.LargeV3,
    };

    public IReadOnlyList<WhisperModelInfo> AvailableModels => Models;

    public WhisperModelManager(string modelsFolder)
    {
        _modelsFolder = modelsFolder;
        Directory.CreateDirectory(_modelsFolder);
    }

    public string? FindExistingModel()
        => Directory.EnumerateFiles(_modelsFolder, "*.bin")
                    .FirstOrDefault(f => new FileInfo(f).Length > 0);

    public async Task<string> DownloadModelAsync(
        string modelKey,
        IProgress<(int Percent, string Label)> progress,
        CancellationToken token)
    {
        if (!KeyToGgml.TryGetValue(modelKey, out var ggmlType))
            throw new ArgumentException($"Unknown model key: {modelKey}");

        var info = Models.First(m => string.Equals(m.Key, modelKey, StringComparison.OrdinalIgnoreCase));
        var modelPath = Path.Combine(_modelsFolder, $"ggml-{info.Key}.bin");
        var tempPath  = modelPath + ".part";

        using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(ggmlType, cancellationToken: token);
        using (var fileWriter = File.Create(tempPath))
        {
            var buffer = new byte[1 << 20];
            long downloaded = 0;
            int read;
            while ((read = await modelStream.ReadAsync(buffer, token)) > 0)
            {
                await fileWriter.WriteAsync(buffer.AsMemory(0, read), token);
                downloaded += read;
                var pct = (int)Math.Min(99, downloaded * 100 / Math.Max(1, info.ApproxBytes));
                progress?.Report((pct, $"{downloaded >> 20} MB of {info.Size}  ({pct}%)"));
            }
        }

        if (File.Exists(modelPath)) File.Delete(modelPath);
        File.Move(tempPath, modelPath);

        // Remove any other models so the reuse scan picks the newly chosen one
        foreach (var other in Directory.EnumerateFiles(_modelsFolder, "*.bin"))
        {
            if (!string.Equals(other, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(other); } catch { /* ignore */ }
            }
        }

        return modelPath;
    }
}
