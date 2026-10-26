using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SwiftGet.Core.Models;
using SwiftGet.Core.Storage;
using SwiftGet.Engine.Categories;
using SwiftGet.Engine.Grabber;

namespace SwiftGet.App.ViewModels;

public sealed partial class GrabberFileItemViewModel : ObservableObject
{
    public GrabberFileItem Item { get; }

    [ObservableProperty]
    private bool _isSelected;

    public string FileName => Item.FileName;
    public string Extension => Item.Extension;
    public string Url => Item.Url;
    public int Depth => Item.Depth;

    public GrabberFileItemViewModel(GrabberFileItem item)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        _isSelected = item.IsSelected;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        Item.IsSelected = value;
    }
}

public sealed partial class GrabberWizardViewModel : ObservableObject, IDisposable
{
    private readonly ISwiftGetDatabase _db;
    private readonly IWebCrawler _crawler;
    private readonly ICategoryManager _categoryManager;
    private CancellationTokenSource? _crawlCts;

    [ObservableProperty]
    private int _currentStep = 1;

    // Step 1: Project & Start URL
    [ObservableProperty]
    private string _projectName = "Site Download";

    [ObservableProperty]
    private string _startUrl = "https://";

    [ObservableProperty]
    private int _domainScopeIndex = 0; // 0 = SameDomain, 1 = Subdomains, 2 = AnyDomain

    // Step 2: Depth & Presets
    [ObservableProperty]
    private double _maxDepth = 2.0;

    [ObservableProperty]
    private int _filePresetIndex = 0; // 0=Pictures, 1=Audio, 2=Video, 3=Documents, 4=Compressed, 5=All, 6=Custom

    [ObservableProperty]
    private string _customExtensions = ".jpg, .png, .pdf";

    // Step 3: Progress
    [ObservableProperty]
    private bool _isCrawling;

    [ObservableProperty]
    private int _pagesVisited;

    [ObservableProperty]
    private int _filesFound;

    [ObservableProperty]
    private string _currentCrawlUrl = string.Empty;

    [ObservableProperty]
    private string _crawlStatus = "Ready to start.";

    // Step 4: Discovered Files & Download
    [ObservableProperty]
    private ObservableCollection<GrabberFileItemViewModel> _discoveredFiles = [];

    [ObservableProperty]
    private string _saveDirectory = string.Empty;

    [ObservableProperty]
    private string _queueName = "default";

    [ObservableProperty]
    private string _selectedCountSummary = "0 files selected";

    [ObservableProperty]
    private string _stepTitle = "Step 1: Project & Target URL";

    public Microsoft.UI.Xaml.Visibility Step1Visibility => CurrentStep == 1 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility Step2Visibility => CurrentStep == 2 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility Step3Visibility => CurrentStep == 3 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility Step4Visibility => CurrentStep == 4 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public bool CanGoBack => CurrentStep > 1 && !IsCrawling;
    public bool CanGoNext => CurrentStep <= 4 && !IsCrawling;
    public string NextButtonText => CurrentStep switch
    {
        1 => "Next >",
        2 => "Start Search >",
        3 => "Next >",
        4 => "Add to Downloads",
        _ => "Next"
    };

    public GrabberWizardViewModel(ISwiftGetDatabase db, IWebCrawler crawler, ICategoryManager categoryManager)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _crawler = crawler ?? throw new ArgumentNullException(nameof(crawler));
        _categoryManager = categoryManager ?? throw new ArgumentNullException(nameof(categoryManager));
        _saveDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        UpdateStepTitle();
    }

    partial void OnCurrentStepChanged(int value)
    {
        UpdateStepProperties();
    }

    partial void OnIsCrawlingChanged(bool value)
    {
        UpdateStepProperties();
    }

    private void UpdateStepProperties()
    {
        OnPropertyChanged(nameof(Step1Visibility));
        OnPropertyChanged(nameof(Step2Visibility));
        OnPropertyChanged(nameof(Step3Visibility));
        OnPropertyChanged(nameof(Step4Visibility));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(NextButtonText));
    }

    private void UpdateStepTitle()
    {
        StepTitle = CurrentStep switch
        {
            1 => "Step 1: Project & Target URL",
            2 => "Step 2: Exploration Depth & File Filters",
            3 => "Step 3: Crawling Website",
            4 => "Step 4: Select Files & Destination",
            _ => $"Step {CurrentStep}"
        };
    }

    [RelayCommand]
    public async Task NextStepAsync()
    {
        if (CurrentStep == 1)
        {
            if (string.IsNullOrWhiteSpace(StartUrl) || !Uri.TryCreate(StartUrl, UriKind.Absolute, out _))
            {
                CrawlStatus = "Please enter a valid starting URL.";
                return;
            }
            CurrentStep = 2;
            UpdateStepTitle();
        }
        else if (CurrentStep == 2)
        {
            CurrentStep = 3;
            UpdateStepTitle();
            await StartCrawlAsync().ConfigureAwait(true);
        }
        else if (CurrentStep == 3)
        {
            if (!IsCrawling)
            {
                CurrentStep = 4;
                UpdateStepTitle();
                UpdateSelectedCount();
            }
        }
    }

    [RelayCommand]
    public void PreviousStep()
    {
        if (CurrentStep > 1 && !IsCrawling)
        {
            CurrentStep--;
            UpdateStepTitle();
        }
    }

    [RelayCommand]
    public async Task StartCrawlAsync()
    {
        if (IsCrawling) return;

        _crawlCts = new CancellationTokenSource();
        IsCrawling = true;
        PagesVisited = 0;
        FilesFound = 0;
        CurrentCrawlUrl = StartUrl;
        CrawlStatus = "Crawling started...";
        DiscoveredFiles.Clear();

        void OnProgress(object? sender, GrabberProgressEventArgs e)
        {
            PagesVisited = e.PagesVisited;
            FilesFound = e.FilesFound;
            CurrentCrawlUrl = e.CurrentUrl;
            CrawlStatus = $"Visited {e.PagesVisited} page(s), found {e.FilesFound} file(s)...";
        }

        void OnDiscovered(object? sender, GrabberFileItem item)
        {
            DiscoveredFiles.Add(new GrabberFileItemViewModel(item));
        }

        _crawler.ProgressUpdated += OnProgress;
        _crawler.FileDiscovered += OnDiscovered;

        try
        {
            var project = new GrabberProject
            {
                Name = ProjectName,
                StartUrl = StartUrl,
                DomainScope = (DomainScope)DomainScopeIndex,
                MaxDepth = (int)MaxDepth,
                FilePreset = (FileFilterPreset)FilePresetIndex,
                CustomExtensions = CustomExtensions,
                SaveDirectory = SaveDirectory,
                QueueId = QueueName
            };

            var results = await _crawler.CrawlAsync(project, _crawlCts.Token).ConfigureAwait(true);
            CrawlStatus = $"Crawling complete! Discovered {results.Count} files.";
            CurrentStep = 4;
            UpdateStepTitle();
            UpdateSelectedCount();
        }
        catch (OperationCanceledException)
        {
            CrawlStatus = "Crawling canceled by user.";
            CurrentStep = 4;
            UpdateStepTitle();
            UpdateSelectedCount();
        }
        catch (Exception ex)
        {
            CrawlStatus = $"Error: {ex.Message}";
        }
        finally
        {
            _crawler.ProgressUpdated -= OnProgress;
            _crawler.FileDiscovered -= OnDiscovered;
            IsCrawling = false;
        }
    }

    [RelayCommand]
    public void CancelCrawl()
    {
        if (IsCrawling && _crawlCts != null)
        {
            _crawlCts.Cancel();
            CrawlStatus = "Canceling...";
        }
    }

    [RelayCommand]
    public void SelectAll()
    {
        foreach (var item in DiscoveredFiles)
        {
            item.IsSelected = true;
        }
        UpdateSelectedCount();
    }

    [RelayCommand]
    public void DeselectAll()
    {
        foreach (var item in DiscoveredFiles)
        {
            item.IsSelected = false;
        }
        UpdateSelectedCount();
    }

    public void UpdateSelectedCount()
    {
        var selected = DiscoveredFiles.Count(f => f.IsSelected);
        SelectedCountSummary = $"{selected} of {DiscoveredFiles.Count} files selected";
    }

    public async Task<int> AddSelectedDownloadsAsync(CancellationToken ct = default)
    {
        var count = 0;
        foreach (var fileVm in DiscoveredFiles.Where(f => f.IsSelected))
        {
            ct.ThrowIfCancellationRequested();
            var category = _categoryManager.ResolveCategory(fileVm.FileName);
            var targetDir = string.IsNullOrWhiteSpace(SaveDirectory) ? category.TargetDirectory : SaveDirectory;
            var destPath = Path.Combine(targetDir, fileVm.FileName);

            var item = new DownloadItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Url = fileVm.Url,
                FileName = fileVm.FileName,
                DestinationPath = destPath,
                QueueId = QueueName,
                CategoryId = category.Id,
                Status = DownloadStatus.Queued,
                CreatedAtUtc = DateTime.UtcNow
            };

            await _db.SaveDownloadAsync(item, ct).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    public void Dispose()
    {
        _crawlCts?.Dispose();
        _crawlCts = null;
    }
}

