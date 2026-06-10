namespace Annoted.Core.Models;

public sealed class DocumentModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? CurrentFilePath { get; set; }
    public string? AudioFilePath { get; set; }
    public string? AudioDisplayName { get; set; }
    public string? ActiveAudioMemoId { get; set; }
    public bool IsDirty { get; set; }
    public bool IsContentLoaded { get; set; } = true;
    public float ZoomFactor { get; set; } = 1F;
    public DateTime LastUndoCheckpointUtc { get; set; } = DateTime.UtcNow;
    public string PreviousText { get; set; } = string.Empty;
    public string PendingText { get; set; } = string.Empty;
    public List<string> UndoHistory { get; } = [];
    public List<string> RedoHistory { get; } = [];
    public List<string> PendingUndoHistory { get; } = [];
    public List<string> PendingRedoHistory { get; } = [];
    public List<AudioMemoModel> AudioMemos { get; } = [];
}
