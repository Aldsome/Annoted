namespace Annoted.Core.Interfaces;

public interface IWhisperModelManager
{
    string? FindExistingModel();
    Task<string> DownloadModelAsync(string modelKey, IProgress<(int Percent, string Label)> progress, CancellationToken token);
    IReadOnlyList<WhisperModelInfo> AvailableModels { get; }
}

public sealed record WhisperModelInfo(string Key, string Name, string Size, long ApproxBytes);
