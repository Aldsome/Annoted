using Annoted.Core.Models;

namespace Annoted.Core.Interfaces;

public interface IStorageService
{
    string AppDataFolder { get; }
    string ModelsFolder { get; }

    AppSettings LoadSettings();
    void SaveSettings(AppSettings settings);

    AutosaveState? LoadAutosave();
    bool SaveAutosave(AutosaveState state);

    string GetDocumentFolder(string documentId);
    string GetMemoFilePath(string documentId, string memoId);
    string GetRecordingFilePath(string documentId);

    /// <summary>Moves storage to the new folder and returns the previous folder (for path rebasing).</summary>
    string MoveStorage(string newFolder);
    bool IsPortable { get; }
}
