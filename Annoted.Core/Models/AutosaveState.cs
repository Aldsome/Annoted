namespace Annoted.Core.Models;

public sealed class AutosaveState
{
    public int ActiveTabIndex { get; set; }
    public bool IsDarkMode { get; set; }
    public bool IsWordWrap { get; set; }
    public string? FontFamily { get; set; }
    public float FontSize { get; set; }
    public int FontStyle { get; set; }
    public int? CustomEditorBackArgb { get; set; }
    public int? CustomEditorForeArgb { get; set; }
    public List<DocumentAutosaveState> Documents { get; set; } = [];
    public bool HasRecording { get; set; }
}

public sealed class DocumentAutosaveState
{
    public string? DocumentId { get; set; }
    public string? AudioFilePath { get; set; }
    public string? AudioDisplayName { get; set; }
    public string? ActiveAudioMemoId { get; set; }
    public List<AudioMemoState> AudioMemos { get; set; } = [];
    public string Text { get; set; } = string.Empty;
    public string? CurrentFilePath { get; set; }
    public bool IsDirty { get; set; }
    public float ZoomFactor { get; set; } = 1F;
    public List<string> UndoHistory { get; set; } = [];
    public List<string> RedoHistory { get; set; } = [];
}

public sealed class AudioMemoState
{
    public string? Id { get; set; }
    public string? FilePath { get; set; }
    public string? DisplayName { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
