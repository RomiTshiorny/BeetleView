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
using GuiLabs.Dotnet.Recorder;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using RecorderProcess = GuiLabs.Dotnet.Recorder.Process;

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

    // Effective set of RecorderProcesses currently checked in the tree (a
    // node is "included" only when itself AND all its ancestors are checked).
    // Recomputed by RecomputeIncludedProcesses() whenever a checkbox toggles.
    private HashSet<RecorderProcess> _includedProcesses = new();
    // True when at least one process in the tree is currently excluded —
    // shortcuts the "do we need to filter at all?" check in the hot path.
    private bool _inclusionFilterActive;

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
        await RebuildTimelineAndApplyFiltersAsync();
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
            _processRoots.Clear();
            foreach (var r in _allProcessRoots) _processRoots.Add(r);
            ApplyProcessFilter();
            RecomputeIncludedProcesses();

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
        _includedProcesses = new HashSet<RecorderProcess>();
        _inclusionFilterActive = false;
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

    /// <summary>
    /// Re-applies the search-box filter AND the timeline time-range filter
    /// to the Processes tree. Mutates each node's
    /// <see cref="ProcessViewModel.Children"/> ObservableCollection in place —
    /// never clones — so node identities (and therefore checkbox state)
    /// survive filter changes.
    /// </summary>
    private void ApplyProcessFilter()
    {
        string f = ProcessFilterBox.Text?.Trim() ?? "";
        double? rs = _timeRangeStartMSec;
        double? re = _timeRangeEndMSec;
        foreach (var root in _allProcessRoots)
        {
            FilterSubtree(root, f, rs, re);
        }
    }

    /// <summary>
    /// Recursively rebuilds <paramref name="node"/>'s visible
    /// <c>Children</c> from its <c>AllChildren</c>, keeping only descendants
    /// that match (or contain a descendant that matches) the search filter
    /// AND overlap the timeline time range. Returns true when this node
    /// should remain visible to its parent.
    /// </summary>
    private static bool FilterSubtree(ProcessViewModel node, string filter, double? rangeStart, double? rangeEnd)
    {
        node.Children.Clear();
        int matchedKids = 0;
        foreach (var c in node.AllChildren)
        {
            if (FilterSubtree(c, filter, rangeStart, rangeEnd))
            {
                node.Children.Add(c);
                matchedKids++;
            }
        }
        // Sentinel ("(All processes)") always stays visible so the user can
        // navigate back to the global view even when no descendant matches.
        bool isSentinel = node.Process is null;
        bool selfVisible = node.MatchesFilter(filter) && (isSentinel || InTimeRange(node, rangeStart, rangeEnd));
        return selfVisible || matchedKids > 0 || isSentinel;
    }

    /// <summary>
    /// True when <paramref name="node"/>'s lifetime overlaps the active
    /// timeline range (or when no range is set). A process whose lifetime
    /// ends before the range starts (or starts after the range ends) has no
    /// activity inside the window so it's hidden from the tree.
    /// </summary>
    private static bool InTimeRange(ProcessViewModel node, double? rangeStart, double? rangeEnd)
    {
        if (rangeStart is not double rs || rangeEnd is not double re) return true;
        var p = node.Process;
        if (p is null) return true;
        double start = p.StartTimeRelativeMSec;
        double stop = p.StopTimeRelativeMSec > 0 ? p.StopTimeRelativeMSec : start;
        return !(stop < rs || start > re);
    }

    // -------------------- Checkbox inclusion --------------------

    /// <summary>
    /// Click handler for each tree row's checkbox. We use <c>Click</c> rather
    /// than <c>Checked</c>/<c>Unchecked</c> so we only react to genuine user
    /// input — the TwoWay binding's initial sync on container realization
    /// would otherwise fire those events too and cause spurious rebuilds.
    /// </summary>
    private async void ProcessIncludeCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not ProcessViewModel pvm) return;
        // Click fires after the TwoWay binding has updated pvm.IsIncluded.
        // Cascade that state to every descendant so the user's intent ("omit
        // this subtree") propagates without them having to expand each node.
        foreach (var c in pvm.AllChildren) c.SetIncludedRecursive(pvm.IsIncluded);

        RecomputeIncludedProcesses();
        await RebuildTimelineAndApplyFiltersAsync();
    }

    /// <summary>
    /// Walks the full tree to compute which processes are effectively
    /// included — a process is included only when its node and every
    /// ancestor up to the (always-included) sentinel are checked.
    /// </summary>
    private void RecomputeIncludedProcesses()
    {
        var set = new HashSet<RecorderProcess>();
        int totalProcessCount = 0;
        foreach (var root in _allProcessRoots)
        {
            GatherIncluded(root, parentIncluded: true, set, ref totalProcessCount);
        }
        _includedProcesses = set;
        _inclusionFilterActive = set.Count < totalProcessCount;
    }

    private static void GatherIncluded(
        ProcessViewModel node,
        bool parentIncluded,
        HashSet<RecorderProcess> set,
        ref int totalProcessCount)
    {
        bool effective = parentIncluded && node.IsIncluded;
        if (node.Process is not null)
        {
            totalProcessCount++;
            if (effective) set.Add(node.Process);
        }
        foreach (var c in node.AllChildren) GatherIncluded(c, effective, set, ref totalProcessCount);
    }

    /// <summary>
    /// Pushes the inclusion + time-range filters into the timeline (so
    /// excluded / out-of-range rows disappear), into the Processes tree (so
    /// out-of-range processes disappear), and into the exception list.
    /// </summary>
    private async Task RebuildTimelineAndApplyFiltersAsync()
    {
        // Refresh the tree first so the user sees the in-range processes
        // immediately; the timeline / exception rebuilds are async and may
        // yield several times for large sessions.
        ApplyProcessFilter();

        if (_loaded is not null)
        {
            var visible = new List<TimelineRowViewModel>(_loaded.TimelineRows.Count);
            double? rs = _timeRangeStartMSec;
            double? re = _timeRangeEndMSec;
            foreach (var row in _loaded.TimelineRows)
            {
                if (row.Process is not null && !_includedProcesses.Contains(row.Process)) continue;
                // Hide rows whose lifetime sits entirely outside the selected
                // range. A row that ends before the range starts (or starts
                // after it ends) has no activity inside the range, so it
                // wouldn't tell the user anything useful.
                if (rs is double s && re is double e2 && (row.StopMSec < s || row.StartMSec > e2)) continue;
                visible.Add(row);
            }
            Timeline.SetVisibleRows(visible);
        }

        CancelPopulation();
        var cts = new CancellationTokenSource();
        _populationCts = cts;
        try
        {
            await ApplyFilterAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
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
        // Inclusion filter: drop rows from processes whose checkbox (or an
        // ancestor's) is unchecked. Skipped entirely when every process is
        // included — saves a Contains() per row in the common case.
        if (_inclusionFilterActive)
        {
            source = source.Where(v => _includedProcesses.Contains(v.Process));
        }
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
            || _timeRangeEndMSec is not null
            || _inclusionFilterActive;

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
