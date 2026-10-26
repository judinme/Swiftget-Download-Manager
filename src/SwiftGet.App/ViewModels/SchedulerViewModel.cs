using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Queues;
using SwiftGet.Engine.Scheduling;

namespace SwiftGet.App.ViewModels;

public sealed partial class SchedulerViewModel : ObservableObject
{
    private readonly ISwiftGetDatabase _db;
    private readonly IQueueManager _queueManager;
    private readonly IScheduler _scheduler;

    [ObservableProperty]
    private ObservableCollection<DownloadQueue> _queues = [];

    [ObservableProperty]
    private DownloadQueue? _selectedQueue;

    [ObservableProperty]
    private ObservableCollection<DownloadItem> _queueItems = [];

    [ObservableProperty]
    private DownloadItem? _selectedQueueItem;

    [ObservableProperty]
    private bool _isScheduleEnabled;

    [ObservableProperty]
    private bool _isPeriodic;

    [ObservableProperty]
    private TimeSpan? _startTime = TimeSpan.Zero;

    [ObservableProperty]
    private bool _hasStopTime;

    [ObservableProperty]
    private TimeSpan? _stopTime = TimeSpan.FromHours(6);

    // Days of week flags (Bitmask: Mon=1, Tue=2, Wed=4, Thu=8, Fri=16, Sat=32, Sun=64)
    [ObservableProperty]
    private bool _dayMonday = true;

    [ObservableProperty]
    private bool _dayTuesday = true;

    [ObservableProperty]
    private bool _dayWednesday = true;

    [ObservableProperty]
    private bool _dayThursday = true;

    [ObservableProperty]
    private bool _dayFriday = true;

    [ObservableProperty]
    private bool _daySaturday = true;

    [ObservableProperty]
    private bool _daySunday = true;

    [ObservableProperty]
    private int _maxRetries = 3;

    [ObservableProperty]
    private bool _exitAppOnCompletion;

    [ObservableProperty]
    private bool _shutdownOnCompletion;

    [ObservableProperty]
    private bool _hibernateOnCompletion;

    [ObservableProperty]
    private bool _forceShutdown;

    [ObservableProperty]
    private double _maxConcurrentDownloads = 1;

    [ObservableProperty]
    private double _speedLimitKbps;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    public SchedulerViewModel(ISwiftGetDatabase db, IQueueManager queueManager, IScheduler scheduler)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _queueManager = queueManager ?? throw new ArgumentNullException(nameof(queueManager));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        var queues = await _db.GetAllQueuesAsync(ct).ConfigureAwait(false);
        Queues.Clear();
        foreach (var q in queues)
        {
            Queues.Add(q);
        }

        SelectedQueue = Queues.FirstOrDefault();
    }

    partial void OnSelectedQueueChanged(DownloadQueue? value)
    {
        if (value != null)
        {
            LoadQueueSettings(value);
            _ = LoadQueueItemsAsync(value.Id);
        }
    }

    private void LoadQueueSettings(DownloadQueue q)
    {
        IsScheduleEnabled = q.IsActive;
        StartTime = q.StartTimeUtc ?? TimeSpan.Zero;
        HasStopTime = q.StopTimeUtc.HasValue;
        StopTime = q.StopTimeUtc ?? TimeSpan.FromHours(6);

        DayMonday = (q.ScheduleDaysMask & 1) != 0;
        DayTuesday = (q.ScheduleDaysMask & 2) != 0;
        DayWednesday = (q.ScheduleDaysMask & 4) != 0;
        DayThursday = (q.ScheduleDaysMask & 8) != 0;
        DayFriday = (q.ScheduleDaysMask & 16) != 0;
        DaySaturday = (q.ScheduleDaysMask & 32) != 0;
        DaySunday = (q.ScheduleDaysMask & 64) != 0;

        ExitAppOnCompletion = q.AfterCompletionAction == AfterCompletionAction.ExitApp;
        ShutdownOnCompletion = q.AfterCompletionAction == AfterCompletionAction.Shutdown;
        HibernateOnCompletion = q.AfterCompletionAction == AfterCompletionAction.Hibernate;

        MaxConcurrentDownloads = Math.Max(1, q.MaxConcurrentDownloads);
        SpeedLimitKbps = (int)(q.SpeedLimitBytesPerSec / 1024);
    }

    public async Task LoadQueueItemsAsync(string queueId, CancellationToken ct = default)
    {
        var items = await _db.GetDownloadsByQueueAsync(queueId, ct).ConfigureAwait(false);
        QueueItems.Clear();
        foreach (var item in items)
        {
            QueueItems.Add(item);
        }
    }

    [RelayCommand]
    public async Task MoveUpAsync()
    {
        if (SelectedQueueItem == null || QueueItems.Count <= 1) return;
        var index = QueueItems.IndexOf(SelectedQueueItem);
        if (index <= 0) return;

        var prev = QueueItems[index - 1];
        var current = SelectedQueueItem;

        var tempPriority = current.Priority;
        current.Priority = prev.Priority > current.Priority ? prev.Priority + 1 : current.Priority + 1;

        QueueItems.Move(index, index - 1);
        await _db.UpdateDownloadPriorityAsync(current.Id, current.Priority).ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task MoveDownAsync()
    {
        if (SelectedQueueItem == null || QueueItems.Count <= 1) return;
        var index = QueueItems.IndexOf(SelectedQueueItem);
        if (index < 0 || index >= QueueItems.Count - 1) return;

        var next = QueueItems[index + 1];
        var current = SelectedQueueItem;

        var tempPriority = current.Priority;
        current.Priority = next.Priority > 0 ? next.Priority - 1 : 0;

        QueueItems.Move(index, index + 1);
        await _db.UpdateDownloadPriorityAsync(current.Id, current.Priority).ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task StartQueueNowAsync()
    {
        if (SelectedQueue == null) return;
        StatusMessage = $"Starting queue '{SelectedQueue.Name}'...";
        await _queueManager.StartQueueAsync(SelectedQueue.Id).ConfigureAwait(false);
        StatusMessage = $"Queue '{SelectedQueue.Name}' started";
    }

    [RelayCommand]
    public async Task StopQueueNowAsync()
    {
        if (SelectedQueue == null) return;
        StatusMessage = $"Stopping queue '{SelectedQueue.Name}'...";
        await _queueManager.StopQueueAsync(SelectedQueue.Id).ConfigureAwait(false);
        StatusMessage = $"Queue '{SelectedQueue.Name}' stopped";
    }

    [RelayCommand]
    public async Task SaveSettingsAsync()
    {
        if (SelectedQueue == null) return;

        SelectedQueue.IsActive = IsScheduleEnabled;
        SelectedQueue.StartTimeUtc = StartTime;
        SelectedQueue.StopTimeUtc = HasStopTime ? StopTime : null;

        var mask = 0;
        if (DayMonday) mask |= 1;
        if (DayTuesday) mask |= 2;
        if (DayWednesday) mask |= 4;
        if (DayThursday) mask |= 8;
        if (DayFriday) mask |= 16;
        if (DaySaturday) mask |= 32;
        if (DaySunday) mask |= 64;
        SelectedQueue.ScheduleDaysMask = mask;

        SelectedQueue.MaxConcurrentDownloads = Math.Max(1, (int)MaxConcurrentDownloads);
        SelectedQueue.SpeedLimitBytesPerSec = (long)SpeedLimitKbps * 1024;

        if (ShutdownOnCompletion)
        {
            SelectedQueue.AfterCompletionAction = AfterCompletionAction.Shutdown;
        }
        else if (HibernateOnCompletion)
        {
            SelectedQueue.AfterCompletionAction = AfterCompletionAction.Hibernate;
        }
        else if (ExitAppOnCompletion)
        {
            SelectedQueue.AfterCompletionAction = AfterCompletionAction.ExitApp;
        }
        else
        {
            SelectedQueue.AfterCompletionAction = AfterCompletionAction.None;
        }

        await _db.SaveQueueAsync(SelectedQueue).ConfigureAwait(false);
        StatusMessage = $"Queue '{SelectedQueue.Name}' settings saved";
    }
}
