using System.Text.Json;
using Annoted.Core.Interfaces;
using Annoted.Core.Models;

namespace Annoted.Infrastructure.Services;

public sealed class StorageService : IStorageService
{
    private const string AppSettingsFileName = "app-settings.json";
    private const string AutosaveFileName = "autosave-session.json";
    private const string DocumentsFolderName = "Documents";
    private const string DocumentRecordingFileName = "recording.wav";
    private const string AudioMemoFilePrefix = "memo-";
    private const string AudioMemoFileExtension = ".wav";

    private readonly string _defaultStorageFolder;
    private readonly string _settingsFilePath;
    private string _appDataFolder;

    public bool IsPortable { get; }

    public string AppDataFolder => _appDataFolder;
    public string ModelsFolder => Path.Combine(_appDataFolder, "models");

    public StorageService(bool portable, string baseDirectory)
    {
        IsPortable = portable;
        _defaultStorageFolder = portable
            ? Path.Combine(baseDirectory, "AnnotedData")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Annoted");

        _settingsFilePath = Path.Combine(_defaultStorageFolder, AppSettingsFileName);
        Directory.CreateDirectory(_defaultStorageFolder);

        var settings = LoadSettings();
        _appDataFolder = ResolveFolder(settings.StorageFolder);
        try
        {
            Directory.CreateDirectory(_appDataFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _appDataFolder = _defaultStorageFolder;
        }

        try { Directory.CreateDirectory(ModelsFolder); } catch { /* non-fatal */ }
    }

    public AppSettings LoadSettings()
    {
        if (!File.Exists(_settingsFilePath)) return new AppSettings();
        try
        {
            var json = File.ReadAllText(_settingsFilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFilePath)!);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // non-fatal — best-effort persist
        }
    }

    public AutosaveState? LoadAutosave()
    {
        var path = Path.Combine(_appDataFolder, AutosaveFileName);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<AutosaveState>(json);
            return state?.Documents.Count > 0 ? state : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public bool SaveAutosave(AutosaveState state)
    {
        try
        {
            Directory.CreateDirectory(_appDataFolder);
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(_appDataFolder, AutosaveFileName), json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public string GetDocumentFolder(string documentId)
        => Path.Combine(_appDataFolder, DocumentsFolderName, documentId);

    public string GetMemoFilePath(string documentId, string memoId)
        => Path.Combine(GetDocumentFolder(documentId), $"{AudioMemoFilePrefix}{memoId}{AudioMemoFileExtension}");

    public string GetRecordingFilePath(string documentId)
        => Path.Combine(GetDocumentFolder(documentId), DocumentRecordingFileName);

    /// <summary>
    /// Moves storage to <paramref name="newFolder"/>, copying autosave + recordings + Documents
    /// exactly like the WinForms ChooseStorageLocation. Returns the previous folder so callers
    /// can rebase in-memory audio paths. Throws on unwritable/invalid targets (caller shows the error).
    /// </summary>
    public string MoveStorage(string newFolder)
    {
        var selectedFolder = Path.GetFullPath(newFolder);
        var previous = _appDataFolder;

        Directory.CreateDirectory(selectedFolder);
        EnsureFolderIsWritable(selectedFolder);
        CopyStorageFile(previous, selectedFolder, AutosaveFileName);
        CopyStorageFile(previous, selectedFolder, DocumentRecordingFileName);
        CopyStorageDirectory(previous, selectedFolder, DocumentsFolderName);

        var settings = LoadSettings();
        settings.StorageFolder = string.Equals(selectedFolder, _defaultStorageFolder, StringComparison.OrdinalIgnoreCase)
            ? null
            : selectedFolder;
        SaveSettings(settings);

        _appDataFolder = selectedFolder;
        try { Directory.CreateDirectory(ModelsFolder); } catch { /* non-fatal */ }
        return previous;
    }

    private static void EnsureFolderIsWritable(string folderPath)
    {
        var probePath = Path.Combine(folderPath, $".annoted-write-test-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(probePath, string.Empty);
        File.Delete(probePath);
    }

    private static void CopyStorageFile(string sourceFolder, string destinationFolder, string fileName)
    {
        var sourceFile = Path.Combine(sourceFolder, fileName);
        var destinationFile = Path.Combine(destinationFolder, fileName);
        if (!File.Exists(sourceFile) || File.Exists(destinationFile)) return;
        File.Copy(sourceFile, destinationFile);
    }

    private static void CopyStorageDirectory(string sourceFolder, string destinationFolder, string directoryName)
    {
        var sourceDirectory = Path.Combine(sourceFolder, directoryName);
        var destinationDirectory = Path.Combine(destinationFolder, directoryName);
        if (!Directory.Exists(sourceDirectory) || Directory.Exists(destinationDirectory)) return;
        CopyDirectory(sourceDirectory, destinationDirectory);
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var filePath in Directory.GetFiles(sourceDirectory))
            File.Copy(filePath, Path.Combine(destinationDirectory, Path.GetFileName(filePath)));
        foreach (var childDirectory in Directory.GetDirectories(sourceDirectory))
            CopyDirectory(childDirectory, Path.Combine(destinationDirectory, Path.GetFileName(childDirectory)));
    }

    public string ResolveDocumentAudioPath(string? audioFilePath)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath)) return string.Empty;
        return Path.IsPathRooted(audioFilePath)
            ? audioFilePath
            : Path.GetFullPath(Path.Combine(_appDataFolder, audioFilePath));
    }

    public string? GetPersistedAudioPath(string? audioFilePath)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath)) return null;
        try
        {
            var full = Path.GetFullPath(audioFilePath);
            var root = Path.GetFullPath(_appDataFolder);
            var rel = Path.GetRelativePath(root, full);
            return !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel) ? rel : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return audioFilePath;
        }
    }

    public string? RebaseStoragePath(string filePath, string previousStorageFolder)
    {
        if (!IsPathInsideFolder(filePath, previousStorageFolder)) return null;
        try
        {
            var rel = Path.GetRelativePath(previousStorageFolder, filePath);
            var newPath = Path.GetFullPath(Path.Combine(_appDataFolder, rel));
            return File.Exists(newPath) ? newPath : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static bool IsPathInsideFolder(string filePath, string folderPath)
    {
        try
        {
            var fullFile = Path.GetFullPath(filePath);
            var fullFolder = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return fullFile.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private string ResolveFolder(string? configured)
    {
        if (IsPortable || string.IsNullOrWhiteSpace(configured))
            return _defaultStorageFolder;
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
    }
}
