using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeetleView.Helpers;
using BeetleView.Services;
using BeetleView.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BeetleView;

public sealed partial class MainPage : Page
{
    private const int ExceptionListBatchSize = 200;
    private const int ExceptionListCap = 5000;

    // Bound to the TreeView. Always contains exactly one entry — the
    // "(All processes)" sentinel — whose Children are the real process roots.
    private readonly ObservableCollection<ProcessViewModel> _processRoots = new();
    private readonly ObservableCollection<ExceptionViewModel> _exceptions = new();

    // Unfiltered source of truth for the Processes tree.
    private IReadOnlyList<ProcessViewModel> _allProcessRoots = Array.Empty<ProcessViewModel>();
    // Exceptions for the currently selected process (or all).
    private List<ExceptionViewModel> _allExceptionsForCurrentProcess = new();

    private LoadedSession? _loaded;

    // Cancels any in-flight exception-list population so a new selection or
    // filter change can supersede the previous one without races.
    private CancellationTokenSource? _populationCts;

    public MainPage()
    {
        InitializeComponent();
        ProcessTree.ItemsSource = _processRoots;
        ExceptionList.ItemsSource = _exceptions;
        Timeline.TimeRangeChanged += Timeline_TimeRangeChanged;

        Loaded += MainPage_Loaded;
    }

    // Time-range filter from the timeline drag selection. null = no filter.
    private double? _timeRangeStartMSec;
    private double? _timeRangeEndMSec;

    private async void Timeline_TimeRangeChanged(double? startMSec, double? endMSec)
    {
        _timeRangeStartMSec = startMSec;
        _timeRangeEndMSec = endMSec;

        CancelPopulation();
        var cts = new CancellationTokenSource();
        _populationCts = cts;
        try
        {
            await ApplyFilterAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
    }

    // -------------------- Lifecycle / Toolbar --------------------

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        // If launched with a file path argument (Open With / drag onto exe), auto-load it.
        var startup = App.StartupFilePath;
        if (!string.IsNullOrEmpty(startup) && File.Exists(startup))
        {
            await LoadFileAsync(startup);
        }
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            picker.FileTypeFilter.Add(".beetle");
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            await LoadFileAsync(file.Path);
        }
        catch (Exception ex)
        {
            await DialogHelper.ShowErrorAsync(XamlRoot, "Failed to open file", ex.ToString());
        }
    }

    private async void RegisterAssocButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string exePath = Environment.ProcessPath
                ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;
            FileAssociation.RegisterPerUser(".beetle", "BeetleView.BeetleFile", "Beetle exception log", exePath);

            await DialogHelper.ShowInfoAsync(
                XamlRoot,
                "File association registered",
                $".beetle files will now be associated with this BeetleView.exe:\n\n{exePath}\n\n" +
                "Right-click a .beetle file → Open with → BeetleView, or simply double-click it.\n\n" +
                "(Per-user registration in HKCU; no admin required. If you move or rebuild this .exe to a different path, re-register.)");
        }
        catch (Exception ex)
        {
            await DialogHelper.ShowErrorAsync(XamlRoot, "Failed to register file association", ex.ToString());
        }
    }

    // -------------------- File loading --------------------

    private async Task LoadFileAsync(string path)
    {
        ResetUiForLoad(path);

        try
        {
            var loaded = await SessionFileLoader.LoadAsync(path);
            _loaded = loaded;

            FilePathText.Text = path;
            SessionInfoText.Text = loaded.SummaryText;
            SessionInfoBorder.Visibility = Visibility.Visible;

            _allProcessRoots = loaded.ProcessRoots;
            ApplyProcessFilter();

            // Yield so the toolbar / session info / process tree paint
            // before we kick off the (potentially expensive) timeline render.
            // Without this the user stares at a blank window while the
            // timeline builds its first batch of rows.
            await Task.Yield();
            Timeline.SetData(loaded.TimelineRows, loaded.SessionDurationMSec);
        }
        catch (Exception ex)
        {
            FilePathText.Text = "No file loaded";
            SessionInfoBorder.Visibility = Visibility.Collapsed;
            await DialogHelper.ShowErrorAsync(XamlRoot, "Failed to load .beetle file", ex.ToString());
        }
        finally
        {
            LoadingRing.IsActive = false;
            OpenButton.IsEnabled = true;
        }
    }

    private void ResetUiForLoad(string path)
    {
        CancelPopulation();
        _processRoots.Clear();
        _allProcessRoots = Array.Empty<ProcessViewModel>();
        _exceptions.Clear();
        _allExceptionsForCurrentProcess = new List<ExceptionViewModel>();
        StackTraceText.Text = "";
        ExceptionCountText.Text = "";
        SessionInfoBorder.Visibility = Visibility.Collapsed;
        Timeline.Clear();
        _timeRangeStartMSec = null;
        _timeRangeEndMSec = null;

        FilePathText.Text = $"Loading {path}...";
        LoadingRing.IsActive = true;
        OpenButton.IsEnabled = false;
    }

    // -------------------- Processes tree filter --------------------

    private void ProcessFilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyProcessFilter();

    private void ApplyProcessFilter()
    {
        string f = ProcessFilterBox.Text?.Trim() ?? "";
        _processRoots.Clear();

        foreach (var root in _allProcessRoots)
        {
            var filtered = FilterTree(root, f, isTopLevel: true);
            if (filtered is not null)
            {
                _processRoots.Add(filtered);
            }
        }
    }

    /// <summary>
    /// Returns a filtered clone of <paramref name="node"/> — a subtree whose
    /// every leaf matches <paramref name="filter"/>, with ancestors of any
    /// match retained so the path is visible. Returns null when nothing in
    /// the subtree matches (and the node itself isn't a kept top-level item).
    /// </summary>
    private static ProcessViewModel? FilterTree(ProcessViewModel node, string filter, bool isTopLevel)
    {
        var filteredKids = new List<ProcessViewModel>(node.Children.Count);
        foreach (var c in node.Children)
        {
            var fc = FilterTree(c, filter, isTopLevel: false);
            if (fc is not null) filteredKids.Add(fc);
        }

        bool selfMatch = node.MatchesFilter(filter);
        // Keep the (All processes) sentinel at the top level even when it
        // doesn't directly "match" — losing it would hide the global option.
        bool isSentinel = node.Process is null;
        bool keep = selfMatch || filteredKids.Count > 0 || (isTopLevel && isSentinel);
        if (!keep) return null;

        return new ProcessViewModel
        {
            Process = node.Process,
            DisplayName = node.DisplayName,
            Pid = node.Pid,
            PidPrefix = node.PidPrefix,
            ExceptionCount = node.ExceptionCount,
            Children = filteredKids,
            // Auto-expand while filtering so matches are visible without
            // hunting through collapsed branches.
            IsExpanded = node.IsExpanded || (filter.Length > 0 && filteredKids.Count > 0),
        };
    }

    // -------------------- Selection / Exceptions list population --------------------

    private async void ProcessTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        var pvm = args.AddedItems.Count > 0 ? args.AddedItems[0] as ProcessViewModel : null;
        await OnProcessSelectedAsync(pvm);
    }

    private async Task OnProcessSelectedAsync(ProcessViewModel? pvm)
    {
        CancelPopulation();
        _exceptions.Clear();
        StackTraceText.Text = "Select an exception to view its stack trace.";

        if (_loaded is null || pvm is null)
        {
            _allExceptionsForCurrentProcess = new List<ExceptionViewModel>();
            ExceptionCountText.Text = "";
            return;
        }

        var cts = new CancellationTokenSource();
        _populationCts = cts;
        var ct = cts.Token;

        ShowExceptionsOverlay(pvm.Process is null
            ? $"Gathering {pvm.ExceptionCount:N0} exceptions across all processes..."
            : $"Loading {pvm.ExceptionCount:N0} exceptions...");

        try
        {
            var rows = await ExceptionViewModelBuilder.BuildAsync(_loaded.Session, pvm.Process, ct);
            if (ct.IsCancellationRequested) return;

            _allExceptionsForCurrentProcess = rows;
            await ApplyFilterAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await DialogHelper.ShowErrorAsync(XamlRoot, "Failed to load exceptions", ex.ToString());
        }
        finally
        {
            if (_populationCts == cts) HideExceptionsOverlay();
        }
    }

    private async void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        CancelPopulation();
        var cts = new CancellationTokenSource();
        _populationCts = cts;
        try
        {
            await ApplyFilterAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Re-fills <see cref="_exceptions"/> from <see cref="_allExceptionsForCurrentProcess"/>
    /// in batches, yielding between batches so the UI stays responsive on
    /// sessions with tens of thousands of exceptions.
    /// </summary>
    private async Task ApplyFilterAsync(CancellationToken ct)
    {
        _exceptions.Clear();
        string filter = FilterBox.Text?.Trim() ?? "";

        IEnumerable<ExceptionViewModel> source = _allExceptionsForCurrentProcess;
        if (_timeRangeStartMSec is double rs && _timeRangeEndMSec is double re)
        {
            source = source.Where(v => v.Exception.TimestampMS >= rs && v.Exception.TimestampMS <= re);
        }
        if (filter.Length > 0)
        {
            source = source.Where(v =>
                (v.ExceptionType?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (v.ExceptionMessage?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        int total = _allExceptionsForCurrentProcess.Count;
        int shown = 0;
        var batch = new List<ExceptionViewModel>(ExceptionListBatchSize);

        bool isFiltered = filter.Length > 0
            || _timeRangeStartMSec is not null
            || _timeRangeEndMSec is not null;

        foreach (var v in source)
        {
            if (ct.IsCancellationRequested) return;
            if (shown >= ExceptionListCap) break;

            batch.Add(v);
            shown++;

            if (batch.Count >= ExceptionListBatchSize)
            {
                foreach (var item in batch) _exceptions.Add(item);
                batch.Clear();
                UpdateExceptionCount(shown, total, isFiltered || shown < total);
                await Task.Delay(1, ct);
            }
        }

        foreach (var item in batch) _exceptions.Add(item);
        UpdateExceptionCount(shown, total, isFiltered || shown < total);
    }

    private void UpdateExceptionCount(int shown, int total, bool filtered)
    {
        ExceptionCountText.Text = filtered
            ? $"Showing {shown:N0} of {total:N0}"
            : $"{total:N0} total";
    }

    private void CancelPopulation()
    {
        try
        {
            _populationCts?.Cancel();
            _populationCts?.Dispose();
        }
        catch { }
        _populationCts = null;
    }

    private void ShowExceptionsOverlay(string message)
    {
        ExceptionsLoadingText.Text = message;
        ExceptionsLoadingOverlay.Visibility = Visibility.Visible;
    }

    private void HideExceptionsOverlay()
    {
        ExceptionsLoadingOverlay.Visibility = Visibility.Collapsed;
    }

    // -------------------- Stack trace view --------------------

    private void ExceptionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded is null || ExceptionList.SelectedItem is not ExceptionViewModel v)
        {
            StackTraceText.Text = "Select an exception to view its stack trace.";
            return;
        }

        try
        {
            string trace = _loaded.Session.ComputeStackTrace(v.Process, v.Exception);
            StackTraceText.Text = string.IsNullOrWhiteSpace(trace)
                ? $"{v.ExceptionType}: {v.ExceptionMessage}\n\n(no stack trace captured)"
                : trace;
        }
        catch (Exception ex)
        {
            StackTraceText.Text = $"Failed to compute stack trace:\n{ex}";
        }
    }
}
