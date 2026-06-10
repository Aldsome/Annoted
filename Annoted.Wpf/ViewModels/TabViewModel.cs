using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Annoted.Core.Models;

namespace Annoted.Wpf.ViewModels;

public sealed partial class TabViewModel : ObservableObject
{
    private const int MaxUndoStates = 50;
    private const int MaxRedoStates = 10;
    private const int UndoCheckpointCharacterInterval = 80;
    private const int UndoCheckpointSeconds = 2;

    public DocumentModel Model { get; }

    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private string _title = "Untitled";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private float _zoomFactor = 1F;
    [ObservableProperty] private bool _isWordWrap;

    private bool _suppressTextChanged;

    public TabViewModel(DocumentModel model)
    {
        Model = model;
        _text = model.PreviousText;
        _zoomFactor = model.ZoomFactor;
        _isDirty = model.IsDirty;
        UpdateTitle();
    }

    partial void OnTextChanged(string value)
    {
        if (_suppressTextChanged) return;
        Model.IsDirty = true;
        IsDirty = true;
        CheckUndoCheckpoint(value);
        UpdateTitle();
    }

    partial void OnIsDirtyChanged(bool value) => UpdateTitle();

    private void UpdateTitle()
    {
        var name = Model.CurrentFilePath is not null
            ? System.IO.Path.GetFileName(Model.CurrentFilePath)
            : "Untitled";
        Title = IsDirty ? name + " •" : name;
    }

    private void CheckUndoCheckpoint(string currentText)
    {
        var prev = Model.PreviousText;
        var now = DateTime.UtcNow;
        var elapsed = (now - Model.LastUndoCheckpointUtc).TotalSeconds;
        var charDiff = Math.Abs(currentText.Length - prev.Length);
        if (elapsed >= UndoCheckpointSeconds || charDiff >= UndoCheckpointCharacterInterval)
        {
            if (!string.Equals(prev, currentText, StringComparison.Ordinal))
            {
                Model.UndoHistory.Add(prev);
                while (Model.UndoHistory.Count > MaxUndoStates)
                    Model.UndoHistory.RemoveAt(0);
                Model.RedoHistory.Clear();
                Model.PreviousText = currentText;
                Model.LastUndoCheckpointUtc = now;
            }
        }
    }

    [RelayCommand]
    private void Undo()
    {
        if (Model.UndoHistory.Count == 0) return;
        var snapshot = Model.UndoHistory[^1];
        Model.UndoHistory.RemoveAt(Model.UndoHistory.Count - 1);
        Model.RedoHistory.Add(Model.PreviousText);
        while (Model.RedoHistory.Count > MaxRedoStates)
            Model.RedoHistory.RemoveAt(0);
        SetText(snapshot);
    }

    [RelayCommand]
    private void Redo()
    {
        if (Model.RedoHistory.Count == 0) return;
        var snapshot = Model.RedoHistory[^1];
        Model.RedoHistory.RemoveAt(Model.RedoHistory.Count - 1);
        Model.UndoHistory.Add(Model.PreviousText);
        while (Model.UndoHistory.Count > MaxUndoStates)
            Model.UndoHistory.RemoveAt(0);
        SetText(snapshot);
    }

    public void SetText(string text)
    {
        _suppressTextChanged = true;
        Model.PreviousText = text;
        Text = text;
        _suppressTextChanged = false;
    }

    public bool CanUndo => Model.UndoHistory.Count > 0;
    public bool CanRedo => Model.RedoHistory.Count > 0;
}
