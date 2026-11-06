using System;
using System.IO;
using System.Threading.Tasks;
using FolderSyncEr.Client.App.ViewModels;
using FolderSyncEr.Client.Platform;
using Xunit;

namespace FolderSyncEr.Client.Tests;

public class ActivitiesViewModelTests
{
    [Fact]
    public void FormatRelativeTime_MatchesUserSpecification()
    {
        var fixedNow = new DateTime(2026, 11, 6, 15, 0, 0, DateTimeKind.Utc);

        Assert.Equal("just now", ClientActivityService.FormatRelativeTime(fixedNow.AddSeconds(-10), fixedNow));
        Assert.Equal("just now", ClientActivityService.FormatRelativeTime(fixedNow.AddSeconds(-55), fixedNow));
        Assert.Equal("5min", ClientActivityService.FormatRelativeTime(fixedNow.AddMinutes(-5), fixedNow));
        Assert.Equal("8min", ClientActivityService.FormatRelativeTime(fixedNow.AddMinutes(-8), fixedNow));
        Assert.Equal("10min", ClientActivityService.FormatRelativeTime(fixedNow.AddMinutes(-10), fixedNow));
        Assert.Equal("1h", ClientActivityService.FormatRelativeTime(fixedNow.AddHours(-1), fixedNow));
        Assert.Equal("1h 30m", ClientActivityService.FormatRelativeTime(fixedNow.AddHours(-1).AddMinutes(-30), fixedNow));
        Assert.Equal("2h", ClientActivityService.FormatRelativeTime(fixedNow.AddHours(-2), fixedNow));
        Assert.Equal("1d", ClientActivityService.FormatRelativeTime(fixedNow.AddDays(-1), fixedNow));
        Assert.Equal("3d 2h", ClientActivityService.FormatRelativeTime(fixedNow.AddDays(-3).AddHours(-2), fixedNow));
    }

    [Fact]
    public async Task ClientActivityService_PersistsToDiskAndLoadsCorrectly()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"act_test_{Guid.NewGuid():N}.json");
        try
        {
            using (var service = new ClientActivityService(tempFile))
            {
                service.LogActivity("You deleted readme.txt", "File", new DateTime(2026, 11, 6, 14, 0, 0, DateTimeKind.Utc));
                service.LogActivity("Synced MyFile.doc", "Sync", new DateTime(2026, 11, 6, 14, 10, 0, DateTimeKind.Utc));
                service.LogActivity("Connection lost", "Connection", new DateTime(2026, 11, 6, 14, 20, 0, DateTimeKind.Utc));
                await service.SaveAsync();
            }

            Assert.True(File.Exists(tempFile));

            // Load into a fresh instance
            using (var loadedService = new ClientActivityService(tempFile))
            {
                var list = loadedService.GetActivities();
                Assert.Equal(3, list.Count);
                // Newest first
                Assert.Equal("Connection lost", list[0].Activity);
                Assert.Equal("Synced MyFile.doc", list[1].Activity);
                Assert.Equal("You deleted readme.txt", list[2].Activity);
            }
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    [Fact]
    public void ClientActivityService_DebouncesRapidDuplicateEvents()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"act_debounce_{Guid.NewGuid():N}.json");
        try
        {
            using var service = new ClientActivityService(tempFile);
            var now = DateTime.UtcNow;

            service.LogActivity("You modified notes.txt", "File", now);
            service.LogActivity("You modified notes.txt", "File", now.AddSeconds(1));
            service.LogActivity("You modified notes.txt", "File", now.AddSeconds(2));

            // Only 1 entry should be logged due to rapid duplicate debounce
            Assert.Single(service.GetActivities());

            // After 4 seconds, another write is logged
            service.LogActivity("You modified notes.txt", "File", now.AddSeconds(5));
            Assert.Equal(2, service.GetActivities().Count);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    [Fact]
    public void ActivitiesViewModel_FiltersBySearchText()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"act_vm_{Guid.NewGuid():N}.json");
        try
        {
            using var service = new ClientActivityService(tempFile);
            service.LogActivity("You deleted readme.txt", "File");
            service.LogActivity("Synced MyFile.doc", "Sync");
            service.LogActivity("Connection lost", "Connection");
            service.LogActivity("Logged in as Admin", "Auth");

            var vm = new ActivitiesViewModel(service);

            Assert.Equal(4, vm.FilteredActivities.Count);
            Assert.Equal("4 activities", vm.SummaryText);
            Assert.True(vm.HasActivities);
            Assert.False(vm.IsEmpty);

            // Filter for "deleted"
            vm.SearchText = "deleted";
            Assert.Single(vm.FilteredActivities);
            Assert.Equal("You deleted readme.txt", vm.FilteredActivities[0].Activity);
            Assert.Equal("1 activity", vm.SummaryText);

            // Filter for non-matching text
            vm.SearchText = "xyz123";
            Assert.Empty(vm.FilteredActivities);
            Assert.True(vm.IsEmpty);
            Assert.False(vm.HasActivities);

            // Clear search
            vm.SearchText = string.Empty;
            Assert.Equal(4, vm.FilteredActivities.Count);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    [Fact]
    public void ActivitiesViewModel_ReceivesRealtimeActivityAddedEvent()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"act_realtime_{Guid.NewGuid():N}.json");
        try
        {
            using var service = new ClientActivityService(tempFile);
            var vm = new ActivitiesViewModel(service);

            Assert.Empty(vm.Activities);

            service.LogActivity("You created project.csproj", "File");

            Assert.Single(vm.Activities);
            Assert.Equal("You created project.csproj", vm.Activities[0].Activity);
            Assert.Equal("just now", vm.Activities[0].WhenText);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    [Fact]
    public void ParseSegments_ColorsDynamicEntitiesAccurately()
    {
        // 1. Sync successful with items
        var segments = ActivityItemViewModel.ParseSegments("Sync successful (3 items)");
        Assert.Equal(3, segments.Count);
        Assert.Equal("Sync ", segments[0].Text);
        Assert.Equal(ActivityItemViewModel.DefaultBrush, segments[0].Brush);
        Assert.Equal("successful", segments[1].Text);
        Assert.Equal(ActivityItemViewModel.SuccessBrush, segments[1].Brush);
        Assert.Equal(" (3 items)", segments[2].Text);
        Assert.Equal(ActivityItemViewModel.FileBrush, segments[2].Brush);

        // 2. Sync failed with error message
        segments = ActivityItemViewModel.ParseSegments("Sync failed: Server unreachable");
        Assert.Equal(3, segments.Count);
        Assert.Equal("Sync ", segments[0].Text);
        Assert.Equal("failed", segments[1].Text);
        Assert.Equal(ActivityItemViewModel.FailureBrush, segments[1].Brush);
        Assert.Equal(": Server unreachable", segments[2].Text);
        Assert.Equal(ActivityItemViewModel.FailureBrush, segments[2].Brush);

        // 3. You deleted <file>
        segments = ActivityItemViewModel.ParseSegments("You deleted readme.txt");
        Assert.Equal(2, segments.Count);
        Assert.Equal("You deleted ", segments[0].Text);
        Assert.Equal(ActivityItemViewModel.DefaultBrush, segments[0].Brush);
        Assert.Equal("readme.txt", segments[1].Text);
        Assert.Equal(ActivityItemViewModel.FileBrush, segments[1].Brush);

        // 4. Logged in as <user>
        segments = ActivityItemViewModel.ParseSegments("Logged in as Admin");
        Assert.Equal(2, segments.Count);
        Assert.Equal("Logged in as ", segments[0].Text);
        Assert.Equal("Admin", segments[1].Text);
        Assert.Equal(ActivityItemViewModel.UserBrush, segments[1].Brush);

        // 5. Connection lost / re-established
        segments = ActivityItemViewModel.ParseSegments("Connection lost");
        Assert.Equal(2, segments.Count);
        Assert.Equal("lost", segments[1].Text);
        Assert.Equal(ActivityItemViewModel.FailureBrush, segments[1].Brush);

        segments = ActivityItemViewModel.ParseSegments("Connection re-established");
        Assert.Equal(2, segments.Count);
        Assert.Equal("re-established", segments[1].Text);
        Assert.Equal(ActivityItemViewModel.SuccessBrush, segments[1].Brush);

        // 6. Server unreachable / Server online
        segments = ActivityItemViewModel.ParseSegments("Server unreachable");
        Assert.Equal(2, segments.Count);
        Assert.Equal("Server ", segments[0].Text);
        Assert.Equal(ActivityItemViewModel.DefaultBrush, segments[0].Brush);
        Assert.Equal("unreachable", segments[1].Text);
        Assert.Equal(ActivityItemViewModel.FailureBrush, segments[1].Brush);

        segments = ActivityItemViewModel.ParseSegments("Server online");
        Assert.Equal(2, segments.Count);
        Assert.Equal("Server ", segments[0].Text);
        Assert.Equal("online", segments[1].Text);
        Assert.Equal(ActivityItemViewModel.SuccessBrush, segments[1].Brush);
    }
}
