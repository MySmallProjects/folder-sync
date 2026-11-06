using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderSyncEr.Client.Platform;

namespace FolderSyncEr.Client.App.ViewModels;

public partial class NotesViewModel : ViewModelBase
{
    private const string NotesFileName = "notes.txt";
    private readonly Func<string?> _getSyncFolderPath;
    private readonly IClientActivityService? _activityService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _content = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _baselineContent = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditButtonText))]
    [NotifyPropertyChangedFor(nameof(IsSaveVisible))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isEditing;

    [ObservableProperty]
    private bool _showUnsavedChangesDialog;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public string EditButtonText => IsEditing ? "View" : "Edit";

    public bool IsSaveVisible => IsEditing;

    public bool CanSave => IsEditing && Content != BaselineContent;

    public string? CurrentNotesFilePath
    {
        get
        {
            var folder = _getSyncFolderPath();
            return string.IsNullOrWhiteSpace(folder) ? null : Path.Combine(folder, NotesFileName);
        }
    }

    public NotesViewModel(Func<string?> getSyncFolderPath, IClientActivityService? activityService = null)
    {
        _getSyncFolderPath = getSyncFolderPath ?? throw new ArgumentNullException(nameof(getSyncFolderPath));
        _activityService = activityService;
    }

    public async Task LoadAsync()
    {
        try
        {
            var path = CurrentNotesFilePath;
            if (path != null && File.Exists(path))
            {
                var text = await File.ReadAllTextAsync(path);
                Content = text;
                BaselineContent = text;
            }
            else
            {
                Content = string.Empty;
                BaselineContent = string.Empty;
            }
            ShowUnsavedChangesDialog = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to read {NotesFileName}: {ex.Message}";
        }
    }

    public async Task RefreshFromDiskAsync()
    {
        // Zero CPU cost, no timer: only reload when not actively in edit mode
        if (IsEditing)
        {
            return;
        }

        await LoadAsync();
    }

    [RelayCommand]
    public void ToggleEditMode()
    {
        if (!IsEditing)
        {
            // Enter edit mode
            IsEditing = true;
            ShowUnsavedChangesDialog = false;
        }
        else
        {
            // User clicked "View" button to return to view mode
            if (Content != BaselineContent)
            {
                // Unsaved changes detected: show warning dialog with OK and Cancel
                ShowUnsavedChangesDialog = true;
            }
            else
            {
                // No changes made: exit cleanly
                IsEditing = false;
                ShowUnsavedChangesDialog = false;
            }
        }
    }

    [RelayCommand]
    public void ConfirmDiscardChanges()
    {
        // User clicked OK in dialog: discard changes and revert to baseline
        Content = BaselineContent;
        ShowUnsavedChangesDialog = false;
        IsEditing = false;
    }

    [RelayCommand]
    public void CancelDiscardChanges()
    {
        // User clicked Cancel in dialog: stay in edit mode
        ShowUnsavedChangesDialog = false;
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (!CanSave)
            return;

        var path = CurrentNotesFilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusMessage = "Sync folder is not configured.";
            return;
        }

        try
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            // Direct overwrite without atomic writes requirement as specified
            await File.WriteAllTextAsync(path, Content);

            BaselineContent = Content;
            IsEditing = false;
            ShowUnsavedChangesDialog = false;
            StatusMessage = $"{NotesFileName} saved.";

            _activityService?.LogActivity($"You modified {NotesFileName}", "File");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to save {NotesFileName}: {ex.Message}";
        }
    }
}
