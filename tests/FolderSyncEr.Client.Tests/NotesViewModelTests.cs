using System;
using System.IO;
using System.Threading.Tasks;
using FolderSyncEr.Client.App.ViewModels;
using FolderSyncEr.Client.Platform;
using Xunit;

namespace FolderSyncEr.Client.Tests;

public class NotesViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public NotesViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FolderSyncEr_NotesTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task InitialState_WhenNoNotesFile_ShowsEmptyContentAndNormalViewMode()
    {
        var vm = new NotesViewModel(() => _tempDir);
        await vm.LoadAsync();

        Assert.Equal(string.Empty, vm.Content);
        Assert.Equal(string.Empty, vm.BaselineContent);
        Assert.False(vm.IsEditing);
        Assert.False(vm.IsSaveVisible);
        Assert.False(vm.CanSave);
        Assert.Equal("Edit", vm.EditButtonText);
        Assert.False(vm.ShowUnsavedChangesDialog);
    }

    [Fact]
    public async Task LoadAsync_WhenNotesFileExists_LoadsContent()
    {
        var notesFile = Path.Combine(_tempDir, "notes.txt");
        await File.WriteAllTextAsync(notesFile, "Idea 1: Dark mode toggle\nIdea 2: Faster sync");

        var vm = new NotesViewModel(() => _tempDir);
        await vm.LoadAsync();

        Assert.Equal("Idea 1: Dark mode toggle\nIdea 2: Faster sync", vm.Content);
        Assert.Equal(vm.Content, vm.BaselineContent);
        Assert.False(vm.IsEditing);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void ToggleEditMode_EntersEditMode_SaveIsVisibleButDisabled()
    {
        var vm = new NotesViewModel(() => _tempDir);
        vm.ToggleEditMode();

        Assert.True(vm.IsEditing);
        Assert.Equal("View", vm.EditButtonText);
        Assert.True(vm.IsSaveVisible);
        Assert.False(vm.CanSave); // Not changed yet
    }

    [Fact]
    public void ContentChange_EnablesSave_RevertingChangeDisablesSave()
    {
        var vm = new NotesViewModel(() => _tempDir);
        vm.BaselineContent = "Original text";
        vm.Content = "Original text";
        vm.ToggleEditMode();

        Assert.False(vm.CanSave);

        // Edit text
        vm.Content = "Original text with my new idea!";
        Assert.True(vm.CanSave);

        // Undo change back to baseline
        vm.Content = "Original text";
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void ToggleEditMode_WhenNoChanges_ExitsDirectlyToViewMode()
    {
        var vm = new NotesViewModel(() => _tempDir);
        vm.BaselineContent = "Sample";
        vm.Content = "Sample";

        vm.ToggleEditMode(); // Enter edit mode
        Assert.True(vm.IsEditing);

        vm.ToggleEditMode(); // Click "View" with no changes
        Assert.False(vm.IsEditing);
        Assert.Equal("Edit", vm.EditButtonText);
        Assert.False(vm.IsSaveVisible);
        Assert.False(vm.ShowUnsavedChangesDialog);
    }

    [Fact]
    public void ToggleEditMode_WhenChangesMade_ShowsUnsavedChangesDialog()
    {
        var vm = new NotesViewModel(() => _tempDir);
        vm.BaselineContent = "Baseline";
        vm.Content = "Baseline";

        vm.ToggleEditMode(); // Enter edit mode
        vm.Content = "Changed content";

        vm.ToggleEditMode(); // Click "View" button with unsaved changes
        Assert.True(vm.ShowUnsavedChangesDialog);
        Assert.True(vm.IsEditing); // Still in edit mode until user decides
    }

    [Fact]
    public void ConfirmDiscardChanges_RevertsToBaselineAndExitsEditMode()
    {
        var vm = new NotesViewModel(() => _tempDir);
        vm.BaselineContent = "Clean Baseline";
        vm.Content = "Clean Baseline";

        vm.ToggleEditMode();
        vm.Content = "Dirty edits";
        vm.ToggleEditMode(); // Triggers dialog

        Assert.True(vm.ShowUnsavedChangesDialog);

        vm.ConfirmDiscardChanges();

        Assert.False(vm.ShowUnsavedChangesDialog);
        Assert.False(vm.IsEditing);
        Assert.Equal("Clean Baseline", vm.Content);
        Assert.Equal("Edit", vm.EditButtonText);
        Assert.False(vm.IsSaveVisible);
    }

    [Fact]
    public void CancelDiscardChanges_KeepsEditModeAndChanges()
    {
        var vm = new NotesViewModel(() => _tempDir);
        vm.BaselineContent = "Clean Baseline";
        vm.Content = "Clean Baseline";

        vm.ToggleEditMode();
        vm.Content = "Dirty edits";
        vm.ToggleEditMode(); // Triggers dialog

        Assert.True(vm.ShowUnsavedChangesDialog);

        vm.CancelDiscardChanges();

        Assert.False(vm.ShowUnsavedChangesDialog);
        Assert.True(vm.IsEditing);
        Assert.Equal("Dirty edits", vm.Content);
        Assert.True(vm.CanSave);
    }

    [Fact]
    public async Task SaveAsync_OverwritesNotesFileAndUpdatesBaseline()
    {
        var activityService = new ClientActivityService();
        var vm = new NotesViewModel(() => _tempDir, activityService);
        vm.ToggleEditMode();
        vm.Content = "My important thoughts & feature ideas\n- Idea 1\n- Idea 2";

        await vm.SaveAsync();

        var notesFile = Path.Combine(_tempDir, "notes.txt");
        Assert.True(File.Exists(notesFile));
        var savedDiskText = await File.ReadAllTextAsync(notesFile);
        Assert.Equal("My important thoughts & feature ideas\n- Idea 1\n- Idea 2", savedDiskText);

        Assert.Equal(vm.Content, vm.BaselineContent);
        Assert.False(vm.IsEditing);
        Assert.Equal("Edit", vm.EditButtonText);
        Assert.False(vm.IsSaveVisible);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public async Task RefreshFromDiskAsync_WhenNotEditing_ReloadsUpdatedFile()
    {
        var notesFile = Path.Combine(_tempDir, "notes.txt");
        await File.WriteAllTextAsync(notesFile, "Initial notes");

        var vm = new NotesViewModel(() => _tempDir);
        await vm.LoadAsync();
        Assert.Equal("Initial notes", vm.Content);

        // Simulate remote sync overwriting notes.txt on disk
        await File.WriteAllTextAsync(notesFile, "Updated notes synced from Laptop");

        await vm.RefreshFromDiskAsync();
        Assert.Equal("Updated notes synced from Laptop", vm.Content);
        Assert.Equal("Updated notes synced from Laptop", vm.BaselineContent);
    }

    [Fact]
    public async Task RefreshFromDiskAsync_WhenEditing_DoesNotClobberUserEdits()
    {
        var notesFile = Path.Combine(_tempDir, "notes.txt");
        await File.WriteAllTextAsync(notesFile, "Initial notes");

        var vm = new NotesViewModel(() => _tempDir);
        await vm.LoadAsync();

        vm.ToggleEditMode();
        vm.Content = "User is typing a long note right now...";

        // Simulate remote sync updating notes.txt on disk while user is typing
        await File.WriteAllTextAsync(notesFile, "Remote change from server");

        await vm.RefreshFromDiskAsync();

        // User edits must NOT be lost!
        Assert.Equal("User is typing a long note right now...", vm.Content);
        Assert.True(vm.IsEditing);
    }
}

