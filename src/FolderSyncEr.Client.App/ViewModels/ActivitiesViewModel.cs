using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderSyncEr.Client.Platform;

namespace FolderSyncEr.Client.App.ViewModels;

public class ActivitySegment
{
    public string Text { get; }
    public IBrush Brush { get; }
    public FontWeight Weight { get; }

    public ActivitySegment(string text, IBrush brush, FontWeight weight = FontWeight.Normal)
    {
        Text = text;
        Brush = brush;
        Weight = weight;
    }
}

public partial class ActivityItemViewModel : ViewModelBase
{
    public string Id { get; }
    public string Activity { get; }
    public DateTime TimestampUtc { get; }
    public string? Category { get; }
    public IReadOnlyList<ActivitySegment> Segments { get; }

    [ObservableProperty]
    private string _whenText = string.Empty;

    public string FullTimestampText => TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

    public static readonly IBrush DefaultBrush = Brush.Parse("#D1D5DB"); // Silver / neutral
    public static readonly IBrush FileBrush = Brush.Parse("#60A5FA");    // Bright blue (file/folder)
    public static readonly IBrush UserBrush = Brush.Parse("#C084FC");    // Soft purple (user)
    public static readonly IBrush SuccessBrush = Brush.Parse("#34D399"); // Emerald green (success/restored/re-established)
    public static readonly IBrush FailureBrush = Brush.Parse("#F87171"); // Soft red (fail/lost/offline/deleted)
    public static readonly IBrush WarningBrush = Brush.Parse("#FBBF24"); // Amber (conflict/warning/paused)
    public static readonly IBrush MutedBrush = Brush.Parse("#9CA3AF");   // Muted gray

    public ActivityItemViewModel(ActivityRecord record, DateTime? nowUtc = null)
    {
        Id = record.Id;
        Activity = record.Activity;
        TimestampUtc = record.TimestampUtc;
        Category = record.Category;
        Segments = ParseSegments(Activity);
        RefreshWhen(nowUtc);
    }

    public void RefreshWhen(DateTime? nowUtc = null)
    {
        WhenText = ClientActivityService.FormatRelativeTime(TimestampUtc, nowUtc);
    }

    public static IReadOnlyList<ActivitySegment> ParseSegments(string activity)
    {
        var list = new List<ActivitySegment>();
        if (string.IsNullOrWhiteSpace(activity))
            return list;

        var text = activity.Trim();

        // 1. "Sync successful" or "Sync successful (N items)"
        if (text.StartsWith("Sync successful", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new ActivitySegment("Sync ", DefaultBrush));
            list.Add(new ActivitySegment("successful", SuccessBrush, FontWeight.SemiBold));
            var remainder = text.Substring("Sync successful".Length);
            if (!string.IsNullOrEmpty(remainder))
            {
                list.Add(new ActivitySegment(remainder, FileBrush, FontWeight.SemiBold));
            }
            return list;
        }

        // 2. "Sync failed" or "Sync failed: <message>"
        if (text.StartsWith("Sync failed", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new ActivitySegment("Sync ", DefaultBrush));
            list.Add(new ActivitySegment("failed", FailureBrush, FontWeight.SemiBold));
            var remainder = text.Substring("Sync failed".Length);
            if (!string.IsNullOrEmpty(remainder))
            {
                list.Add(new ActivitySegment(remainder, FailureBrush));
            }
            return list;
        }

        // 3. "Connection lost"
        if (text.Equals("Connection lost", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new ActivitySegment("Connection ", DefaultBrush));
            list.Add(new ActivitySegment("lost", FailureBrush, FontWeight.SemiBold));
            return list;
        }

        // 4. "Connection re-established"
        if (text.Equals("Connection re-established", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new ActivitySegment("Connection ", DefaultBrush));
            list.Add(new ActivitySegment("re-established", SuccessBrush, FontWeight.SemiBold));
            return list;
        }

        // 5. "Network offline"
        if (text.Equals("Network offline", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new ActivitySegment("Network ", DefaultBrush));
            list.Add(new ActivitySegment("offline", FailureBrush, FontWeight.SemiBold));
            return list;
        }

        // 6. "Network restored"
        if (text.Equals("Network restored", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new ActivitySegment("Network ", DefaultBrush));
            list.Add(new ActivitySegment("restored", SuccessBrush, FontWeight.SemiBold));
            return list;
        }

        // 7. "Logged in as <User>"
        if (text.StartsWith("Logged in as ", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new ActivitySegment("Logged in as ", DefaultBrush));
            var user = text.Substring("Logged in as ".Length);
            list.Add(new ActivitySegment(user, UserBrush, FontWeight.SemiBold));
            return list;
        }

        // 8. "Logged out"
        if (text.Equals("Logged out", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new ActivitySegment("Logged ", DefaultBrush));
            list.Add(new ActivitySegment("out", MutedBrush, FontWeight.SemiBold));
            return list;
        }

        // 9. "Sync paused" / "Sync resumed"
        if (text.Equals("Sync paused", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new ActivitySegment("Sync ", DefaultBrush));
            list.Add(new ActivitySegment("paused", WarningBrush, FontWeight.SemiBold));
            return list;
        }
        if (text.Equals("Sync resumed", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new ActivitySegment("Sync ", DefaultBrush));
            list.Add(new ActivitySegment("resumed", SuccessBrush, FontWeight.SemiBold));
            return list;
        }

        // 10. "You created <file>"
        if (text.StartsWith("You created ", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new ActivitySegment("You created ", DefaultBrush));
            list.Add(new ActivitySegment(text.Substring("You created ".Length), FileBrush, FontWeight.SemiBold));
            return list;
        }

        // 11. "You modified <file>"
        if (text.StartsWith("You modified ", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new ActivitySegment("You modified ", DefaultBrush));
            list.Add(new ActivitySegment(text.Substring("You modified ".Length), FileBrush, FontWeight.SemiBold));
            return list;
        }

        // 12. "You deleted <file> forever" vs "You deleted <file>"
        if (text.StartsWith("You deleted ", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new ActivitySegment("You deleted ", DefaultBrush));
            var rest = text.Substring("You deleted ".Length);
            if (rest.EndsWith(" forever", StringComparison.OrdinalIgnoreCase))
            {
                var file = rest.Substring(0, rest.Length - " forever".Length);
                list.Add(new ActivitySegment(file, FileBrush, FontWeight.SemiBold));
                list.Add(new ActivitySegment(" forever", FailureBrush, FontWeight.SemiBold));
            }
            else
            {
                list.Add(new ActivitySegment(rest, FileBrush, FontWeight.SemiBold));
            }
            return list;
        }

        // 13. "You renamed <old> to <new>"
        if (text.StartsWith("You renamed ", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new ActivitySegment("You renamed ", DefaultBrush));
            var rest = text.Substring("You renamed ".Length);
            var toIdx = rest.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
            if (toIdx > 0)
            {
                var oldName = rest.Substring(0, toIdx);
                var newName = rest.Substring(toIdx + 4);
                list.Add(new ActivitySegment(oldName, FileBrush, FontWeight.SemiBold));
                list.Add(new ActivitySegment(" to ", DefaultBrush));
                list.Add(new ActivitySegment(newName, FileBrush, FontWeight.SemiBold));
            }
            else
            {
                list.Add(new ActivitySegment(rest, FileBrush, FontWeight.SemiBold));
            }
            return list;
        }

        // 14. "Restored <file>"
        if (text.StartsWith("Restored ", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new ActivitySegment("Restored ", SuccessBrush, FontWeight.SemiBold));
            list.Add(new ActivitySegment(text.Substring("Restored ".Length), FileBrush, FontWeight.SemiBold));
            return list;
        }

        // 15. "Trash emptied"
        if (text.Equals("Trash emptied", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new ActivitySegment("Trash ", DefaultBrush));
            list.Add(new ActivitySegment("emptied", SuccessBrush, FontWeight.SemiBold));
            return list;
        }

        // 16. "Conflict detected on <file>"
        if (text.StartsWith("Conflict detected on ", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new ActivitySegment("Conflict detected on ", WarningBrush, FontWeight.SemiBold));
            list.Add(new ActivitySegment(text.Substring("Conflict detected on ".Length), FileBrush, FontWeight.SemiBold));
            return list;
        }

        // 17. Legacy fallback: "Synced <item>"
        if (text.StartsWith("Synced ", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new ActivitySegment("Synced ", DefaultBrush));
            list.Add(new ActivitySegment(text.Substring("Synced ".Length), FileBrush, FontWeight.SemiBold));
            return list;
        }

        // General fallback
        list.Add(new ActivitySegment(text, DefaultBrush));
        return list;
    }
}

public partial class ActivitiesViewModel : ViewModelBase
{
    private readonly IClientActivityService _activityService;

    public ObservableCollection<ActivityItemViewModel> Activities { get; } = new();
    public ObservableCollection<ActivityItemViewModel> FilteredActivities { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [ObservableProperty]
    private string _summaryText = string.Empty;

    public bool HasActivities => FilteredActivities.Count > 0;
    public bool IsEmpty => FilteredActivities.Count == 0;

    public ActivitiesViewModel(IClientActivityService activityService)
    {
        _activityService = activityService ?? throw new ArgumentNullException(nameof(activityService));

        // Load existing activities
        var existing = _activityService.GetActivities();
        var now = DateTime.UtcNow;
        foreach (var record in existing)
        {
            Activities.Add(new ActivityItemViewModel(record, now));
        }

        ApplyFilter();

        _activityService.ActivityAdded += OnActivityAdded;
    }

    private void OnActivityAdded(object? sender, ActivityRecord record)
    {
        void AddToUi()
        {
            var item = new ActivityItemViewModel(record);
            Activities.Insert(0, item);
            ApplyFilter();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            AddToUi();
        }
        else
        {
            Dispatcher.UIThread.Post(AddToUi);
        }
    }

    public void UpdateWhenTimes()
    {
        var now = DateTime.UtcNow;
        foreach (var item in Activities)
        {
            item.RefreshWhen(now);
        }
    }

    private void ApplyFilter()
    {
        FilteredActivities.Clear();
        var query = SearchText?.Trim();

        var matches = string.IsNullOrEmpty(query)
            ? Activities
            : Activities.Where(a => a.Activity.Contains(query, StringComparison.OrdinalIgnoreCase));

        foreach (var match in matches)
        {
            FilteredActivities.Add(match);
        }

        OnPropertyChanged(nameof(HasActivities));
        OnPropertyChanged(nameof(IsEmpty));

        SummaryText = FilteredActivities.Count == 1
            ? "1 activity"
            : $"{FilteredActivities.Count} activities";
    }
}
