using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderSyncEr.Client.Platform;

namespace FolderSyncEr.Client.App.ViewModels;

public partial class ActivityItemViewModel : ViewModelBase
{
    public string Id { get; }
    public string Activity { get; }
    public DateTime TimestampUtc { get; }
    public string? Category { get; }

    [ObservableProperty]
    private string _whenText = string.Empty;

    public string FullTimestampText => TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

    public ActivityItemViewModel(ActivityRecord record, DateTime? nowUtc = null)
    {
        Id = record.Id;
        Activity = record.Activity;
        TimestampUtc = record.TimestampUtc;
        Category = record.Category;
        RefreshWhen(nowUtc);
    }

    public void RefreshWhen(DateTime? nowUtc = null)
    {
        WhenText = ClientActivityService.FormatRelativeTime(TimestampUtc, nowUtc);
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

