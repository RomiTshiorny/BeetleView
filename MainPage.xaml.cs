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

    // Bound to the TreeView in *unbound* (TreeViewNode) mode below. The
    // sentinel "(All processes)" lives at index 0 of _allProcessRoots; its
    // AllChildren are the real per-process roots. We materialize this into
    // TreeView.RootNodes via ApplyProcessFilter / BuildNode.
    private readonly ObservableCollection<ExceptionViewModel> _exceptions = new();

    // Maps each visible ProcessViewModel to its current TreeViewNode wrapper.
    // Rebuilt by ApplyProcessFilter(). Used to translate selection events
    // (which hand us TreeViewNodes) back to ProcessViewModels.
    private readonly Dictionary<ProcessViewModel, TreeViewNode> _vmToNode = new();

    // Unfiltered source of truth for the Processes tree.
    private IReadOnlyList<ProcessViewModel> _allProcessRoots = Array.Empty<ProcessViewModel>();
    // Exceptions for the currently selected process (or all).
    private List<ExceptionViewModel> _allExceptionsForCurrentProcess = new();
    // True when the user has drilled into a specific process (as opposed to
    // the "(All processes)" sentinel or nothing). When set, ApplyFilterAsync
    // skips the inclusion-checkbox filter for the exception list — the
    // explicit selection is a stronger signal than the checkbox state, and
    // dropping the selected process's own exceptions because its (or an
    // ancestor's) checkbox is unchecked would just leave the user staring at
    // an empty list and wondering what happened.
    private bool _hasSpecificProcessSelected;

    // When non-null, restricts the timeline to processes within this
    // node's subtree. Null = "(All processes)" sentinel or no selection,
    // i.e. show every row. Set by OnProcessSelectedAsync so the timeline
    // tracks the tree selection ("drill in to see just this subtree's
    // bars and exceptions").
    private ProcessViewModel? _timelineScope;
    private HashSet<RecorderProcess>? _timelineScopeProcesses;

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
        // We drive the TreeView in unbound (TreeViewNode) mode rather than
        // data-bound mode. The data-bound pattern requires a <TreeViewItem>
        // wrap at the root of the ItemTemplate, which makes
        // SelectionChanged.AddedItems return the container (not the bound
        // data) for nested items — a footgun that silently broke selection
        // for every non-root row. Unbound mode hands us TreeViewNode objects
        // whose Content is the ProcessViewModel, with no ambiguity.
        ExceptionList.ItemsSource = _exceptions;
        Timeline.TimeRangeChanged += Timeline_TimeRangeChanged;
        Timeline.DiagnosticState += s => WriteDebug($"[timeline] {s}");
        Timeline.LabelFilterRequested += Timeline_LabelFilterRequested;

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
            ApplyProcessFilter();
            RecomputeIncludedProcesses();

            // Yield so the toolbar / session info / process tree paint
            // before we kick off the (potentially expensive) timeline render.
            // Without this the user stares at a blank window while the
            // timeline builds its first batch of rows.
            await Task.Yield();
            Timeline.SetData(loaded.TimelineRows, loaded.SessionDurationMSec);

            // Auto-populate the exception list with everything from the
            // "(All processes)" sentinel so the user sees data immediately
            // after opening a file, without having to click into the tree.
            // The TreeView's SelectionChanged won't fire on its own here
            // (nothing's been clicked), so we drive OnProcessSelectedAsync
            // directly with the sentinel — which has a null Process and is
            // the first entry in ProcessRoots by construction.
            var sentinel = _allProcessRoots.Count > 0 ? _allProcessRoots[0] : null;
            if (sentinel is not null)
            {
                await OnProcessSelectedAsync(sentinel);
            }
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
        ProcessTree.RootNodes.Clear();
        _vmToNode.Clear();
        _allProcessRoots = Array.Empty<ProcessViewModel>();
        _includedProcesses = new HashSet<RecorderProcess>();
        _inclusionFilterActive = false;
        _hasSpecificProcessSelected = false;
        _timelineScope = null;
        _timelineScopeProcesses = null;
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

    private void HideEmptyButton_Click(object sender, RoutedEventArgs e) => ApplyProcessFilter();

    /// <summary>
    /// Rebuilds the TreeView's RootNodes from <see cref="_allProcessRoots"/>,
    /// applying the search-box filter. We use unbound (TreeViewNode) mode so
    /// selection events are unambiguous: every selected item is a
    /// TreeViewNode whose Content is the underlying ProcessViewModel.
    ///
    /// Filter changes rebuild the node tree from scratch — cheap, since the
    /// underlying ProcessViewModel instances (and therefore their IsIncluded
    /// checkbox state) are preserved. Expansion state is lost on rebuild;
    /// we re-expand every node so the hierarchy stays visible.
    ///
    /// The timeline time-range filter is deliberately NOT applied to the
    /// tree — dragging a range on the timeline zooms the timeline itself
    /// and filters the exception list, but leaves the Processes tree stable
    /// so the user doesn't lose their navigation context.
    /// </summary>
    private void ApplyProcessFilter()
    {
        string f = ProcessFilterBox.Text?.Trim() ?? "";
        bool hideEmpty = HideEmptyButton?.IsChecked == true;
        ProcessTree.RootNodes.Clear();
        _vmToNode.Clear();
        foreach (var root in _allProcessRoots)
        {
            var node = BuildNode(root, f, hideEmpty);
            if (node is not null) ProcessTree.RootNodes.Add(node);
        }
    }

    /// <summary>
    /// Recursively materializes a <see cref="TreeViewNode"/> for the given
    /// process VM and its filter-matching descendants. Returns null when
    /// neither the node itself nor any descendant matches the filter (so the
    /// caller can drop the whole subtree). The sentinel always materializes
    /// — it's the user's "back to global view" affordance.
    ///
    /// When <paramref name="hideEmpty"/> is true, also drops any non-sentinel
    /// node whose entire subtree has zero exceptions. ExceptionCount is a
    /// precomputed subtree aggregate, so this is a single property check.
    /// </summary>
    private TreeViewNode? BuildNode(ProcessViewModel vm, string filter, bool hideEmpty)
    {
        bool isSentinel = vm.Process is null;
        bool selfMatches = vm.MatchesFilter(filter);

        if (hideEmpty && !isSentinel && vm.ExceptionCount == 0)
        {
            return null;
        }

        var childNodes = new List<TreeViewNode>(vm.AllChildren.Count);
        foreach (var c in vm.AllChildren)
        {
            var cn = BuildNode(c, filter, hideEmpty);
            if (cn is not null) childNodes.Add(cn);
        }

        if (!selfMatches && childNodes.Count == 0 && !isSentinel)
        {
            return null;
        }

        var node = new TreeViewNode
        {
            Content = vm,
            IsExpanded = true,
        };
        foreach (var cn in childNodes) node.Children.Add(cn);
        _vmToNode[vm] = node;
        return node;
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
        if (sender is not CheckBox cb) return;
        // In unbound TreeView mode the container's DataContext is the
        // TreeViewNode; unwrap to the underlying ProcessViewModel.
        var pvm = ResolveProcessVm(cb.DataContext);
        if (pvm is null) return;
        WriteDebug($"CheckBox click: {pvm.DisplayName} → IsIncluded={pvm.IsIncluded}");
        // Click fires after the TwoWay binding has updated pvm.IsIncluded.
        // Cascade that state to every descendant so the user's intent ("omit
        // this subtree") propagates without them having to expand each node.
        foreach (var c in pvm.AllChildren) c.SetIncludedRecursive(pvm.IsIncluded);

        RecomputeIncludedProcesses();

        // The checkbox is the single way to "focus" a process — clicking it
        // also drills in: refresh the exception list + timeline scope for
        // this node, and reflect the visual selection in the tree.
        if (_vmToNode.TryGetValue(pvm, out var node))
        {
            ProcessTree.SelectedNode = node;
        }
        // Push the include-set change into the timeline FIRST so its rows
        // reflect the latest checkbox state, then do the focus-load (which
        // sets the scope override on top of those rows).
        ApplyTimelineVisibility();
        await OnProcessSelectedAsync(pvm);
    }

    private async void SelectAllButton_Click(object sender, RoutedEventArgs e) =>
        await SetAllIncludedAsync(true);

    private async void DeselectAllButton_Click(object sender, RoutedEventArgs e) =>
        await SetAllIncludedAsync(false);

    /// <summary>
    /// Bulk include / exclude every process in the tree. Walks
    /// <c>_allProcessRoots</c> (the unfiltered source) so the operation
    /// covers hidden-by-search-filter nodes too — the user clicking
    /// "Deselect all" expects literally every process to be unchecked, not
    /// just the ones currently scrolled into view.
    /// </summary>
    private async Task SetAllIncludedAsync(bool included)
    {
        if (_allProcessRoots.Count == 0) return;
        foreach (var root in _allProcessRoots)
        {
            root.SetIncludedRecursive(included);
        }
        RecomputeIncludedProcesses();
        // Reset the timeline drill-in scope so the global include filter
        // (which the user just bulk-changed) controls visibility again.
        // Otherwise a previously-selected subtree would keep showing even
        // after "Deselect all", because the scope overrides inclusion.
        _timelineScope = null;
        _timelineScopeProcesses = null;
        // Realign the tree selection visually so the user sees their
        // global action mapped to "(All processes)", and drive the same
        // "focus the sentinel" path the checkbox would take (the TreeView
        // no longer routes its own selection events to OnProcessSelected,
        // so we call it explicitly).
        var sentinel = _allProcessRoots[0];
        if (_vmToNode.TryGetValue(sentinel, out var sentinelNode))
        {
            ProcessTree.SelectedNode = sentinelNode;
        }
        ApplyTimelineVisibility();
        await OnProcessSelectedAsync(sentinel);
    }

    /// <summary>
    /// Walks the full tree to compute which processes are effectively
    /// included. A process is included iff its own checkbox is checked.
    /// We deliberately do NOT also require every ancestor to be checked —
    /// the cascade-on-click in <see cref="ProcessIncludeCheckBox_Click"/>
    /// already propagates a parent toggle down to its descendants, so any
    /// state the user can see on screen is the state they meant to set.
    /// Requiring ancestor inclusion as well surprised users who unchecked
    /// a parent, then re-checked a single child and expected to see it.
    /// </summary>
    private void RecomputeIncludedProcesses()
    {
        var set = new HashSet<RecorderProcess>();
        int totalProcessCount = 0;
        foreach (var root in _allProcessRoots)
        {
            GatherIncluded(root, set, ref totalProcessCount);
        }
        _includedProcesses = set;
        _inclusionFilterActive = set.Count < totalProcessCount;
    }

    private static void GatherIncluded(
        ProcessViewModel node,
        HashSet<RecorderProcess> set,
        ref int totalProcessCount)
    {
        if (node.Process is not null)
        {
            totalProcessCount++;
            if (node.IsIncluded) set.Add(node.Process);
        }
        foreach (var c in node.AllChildren) GatherIncluded(c, set, ref totalProcessCount);
    }

    /// <summary>
    /// Pushes the inclusion filter into the timeline (rows for unchecked
    /// processes disappear), refreshes the Processes tree (a no-op unless
    /// the search text changed — kept here so the method works as a general
    /// "filters changed, please refresh" entry point), and refilters the
    /// exception list. The timeline time-range filter only affects the
    /// exception list — the tree and the timeline rows themselves stay put
    /// so dragging a selection doesn't reshuffle the UI around the user.
    /// </summary>
    private async Task RebuildTimelineAndApplyFiltersAsync()
    {
        // Idempotent when nothing tree-relevant changed (see FilterSubtree).
        ApplyProcessFilter();
        ApplyTimelineVisibility();

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
    /// Pushes the current visibility filter (inclusion + selected subtree)
    /// into the timeline. Selection-driven subtree scope OVERRIDES the
    /// inclusion-checkbox filter — mirroring the exception list's logic
    /// where a specific drill-in beats the global include filter, so the
    /// user isn't surprised by an empty timeline after picking a subtree
    /// whose nodes happen to be unchecked.
    /// </summary>
    private void ApplyTimelineVisibility()
    {
        if (_loaded is null) return;

        bool useSubtree = _timelineScopeProcesses is not null;
        var visible = new List<TimelineRowViewModel>(_loaded.TimelineRows.Count);
        foreach (var row in _loaded.TimelineRows)
        {
            if (row.Process is null) continue; // shouldn't happen — timeline rows always have a Process
            if (useSubtree)
            {
                if (!_timelineScopeProcesses!.Contains(row.Process)) continue;
            }
            else
            {
                if (!_includedProcesses.Contains(row.Process)) continue;
            }
            visible.Add(row);
        }
        Timeline.SetVisibleRows(visible);
    }

    // Right-click "Show only this process" on a timeline row label.
    // Drops the image name into ProcessFilterBox AND drills the timeline /
    // exception list into the specific process, regardless of which
    // checkboxes were previously toggled — so the user immediately sees
    // only that process's data instead of their previous selection.
    private async void Timeline_LabelFilterRequested(object? sender, TimelineRowViewModel row)
    {
        if (_loaded is null || row.Process is null) return;

        var pvm = FindProcessVm(row.Process);
        if (pvm is null) return;

        // Strip the trailing pid from the row label to get the bare image
        // name — it's the more reusable filter token across sessions.
        string fullLabel = row.Label;
        int lastSpace = fullLabel.LastIndexOf(' ');
        string imageName = lastSpace > 0 ? fullLabel.Substring(0, lastSpace) : fullLabel;
        if (ProcessFilterBox is not null)
        {
            ProcessFilterBox.Text = imageName;
        }

        // ApplyProcessFilter rebuilt _vmToNode under the new filter.
        // Select the node so the user sees the row highlighted in the tree.
        if (_vmToNode.TryGetValue(pvm, out var node))
        {
            ProcessTree.SelectedNode = node;
        }

        await OnProcessSelectedAsync(pvm);
    }

    /// <summary>
    /// Linear lookup of the ProcessViewModel that wraps the given
    /// RecorderProcess. Used for cross-component drill-in from the timeline,
    /// where we only have the RecorderProcess reference. Cheap enough at
    /// O(N) for any realistic session size (a few thousand processes).
    /// </summary>
    private ProcessViewModel? FindProcessVm(RecorderProcess target)
    {
        foreach (var root in _allProcessRoots)
        {
            var hit = FindProcessVmIn(root, target);
            if (hit is not null) return hit;
        }
        return null;
    }

    private static ProcessViewModel? FindProcessVmIn(ProcessViewModel node, RecorderProcess target)
    {
        if (ReferenceEquals(node.Process, target)) return node;
        foreach (var c in node.AllChildren)
        {
            var hit = FindProcessVmIn(c, target);
            if (hit is not null) return hit;
        }
        return null;
    }

    private static void CollectSubtreeProcesses(ProcessViewModel node, HashSet<RecorderProcess> set)
    {
        if (node.Process is not null) set.Add(node.Process);
        foreach (var c in node.AllChildren) CollectSubtreeProcesses(c, set);
    }

    // -------------------- Selection / Exceptions list population --------------------

    private void DebugToggle_Toggled(object sender, RoutedEventArgs e)
    {
        DebugText.Visibility = DebugToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        if (!DebugToggle.IsOn) DebugText.Text = "";
    }

    private readonly System.Collections.Generic.Queue<string> _debugLog = new();

    private void WriteDebug(string message)
    {
        if (DebugToggle?.IsOn != true) return;
        _debugLog.Enqueue(message);
        while (_debugLog.Count > 8) _debugLog.Dequeue();
        DebugText.Text = string.Join("\n", _debugLog);
    }

    // Row-click selection has been intentionally removed (XAML no longer
    // wires ProcessTree.ItemInvoked / SelectionChanged) — the checkbox is
    // the single way to focus a process. See ProcessIncludeCheckBox_Click.

    /// <summary>
    /// Normalize whatever the TreeView hands us — in unbound mode it should
    /// always be a <see cref="TreeViewNode"/> whose Content is the bound
    /// <see cref="ProcessViewModel"/>. The other branches are belt-and-braces
    /// for unexpected runtime behavior; they should never fire.
    /// </summary>
    private static ProcessViewModel? ResolveProcessVm(object? source)
    {
        return source switch
        {
            null => null,
            TreeViewNode node => node.Content as ProcessViewModel,
            ProcessViewModel pvm => pvm,
            FrameworkElement fe => (fe.DataContext as TreeViewNode)?.Content as ProcessViewModel
                                    ?? fe.DataContext as ProcessViewModel,
            _ => null,
        };
    }

    private async Task OnProcessSelectedAsync(ProcessViewModel? pvm)
    {
        CancelPopulation();
        _exceptions.Clear();
        StackTraceText.Text = "Select an exception to view its stack trace.";

        if (_loaded is null || pvm is null)
        {
            _allExceptionsForCurrentProcess = new List<ExceptionViewModel>();
            _hasSpecificProcessSelected = false;
            ExceptionCountText.Text = "";
            return;
        }

        // A null Process is the "(All processes)" sentinel — that's the
        // "global view" so the inclusion filter applies. Any other node is
        // an explicit drill-in and overrides the inclusion filter for the
        // exception list (see ApplyFilterAsync).
        _hasSpecificProcessSelected = pvm.Process is not null;

        // Clear any stale timeline drag-selection: switching processes is a
        // fresh drill-in, the user expects to see the full exception list
        // for the new node, not still be constrained to a previous zoom
        // window. We zero the fields directly + call ClearSelection on the
        // timeline to update its overlay/status — but we don't go through
        // Timeline_TimeRangeChanged (which would kick off its own filter
        // rebuild that races with the one we're about to do here).
        _timeRangeStartMSec = null;
        _timeRangeEndMSec = null;
        Timeline.TimeRangeChanged -= Timeline_TimeRangeChanged;
        Timeline.ClearSelection();
        Timeline.TimeRangeChanged += Timeline_TimeRangeChanged;

        // Update the timeline scope so the bars track the user's drill-in.
        // Sentinel (Process == null) clears the scope so all rows show.
        _timelineScope = pvm.Process is null ? null : pvm;
        _timelineScopeProcesses = null;
        if (_timelineScope is not null)
        {
            var set = new HashSet<RecorderProcess>();
            CollectSubtreeProcesses(_timelineScope, set);
            _timelineScopeProcesses = set;
        }
        ApplyTimelineVisibility();

        var cts = new CancellationTokenSource();
        _populationCts = cts;
        var ct = cts.Token;

        ShowExceptionsOverlay(pvm.Process is null
            ? $"Gathering {pvm.ExceptionCount:N0} exceptions across all processes..."
            : $"Loading {pvm.ExceptionCount:N0} exceptions...");

        // Short-circuit: sentinel ("All processes") with nothing included
        // means every exception would be filtered out anyway. Skip the
        // expensive BuildAsync over the entire session.
        if (pvm.Process is null && _inclusionFilterActive && _includedProcesses.Count == 0)
        {
            _allExceptionsForCurrentProcess = new List<ExceptionViewModel>();
            _exceptions.Clear();
            UpdateExceptionCount(0, 0, filtered: true);
            HideExceptionsOverlay();
            return;
        }

        try
        {
            var rows = await ExceptionViewModelBuilder.BuildAsync(_loaded.Session, pvm, ct);
            if (ct.IsCancellationRequested) return;

            _allExceptionsForCurrentProcess = rows;
            await ApplyFilterAsync(ct);
            WriteDebug($"Sel {pvm.DisplayName}: label={pvm.ExceptionCount} own={(pvm.Process?.Exceptions.Count ?? -1)} kids={pvm.AllChildren.Count} rows={rows.Count} shown={_exceptions.Count} inclFilter={_inclusionFilterActive} specific={_hasSpecificProcessSelected}");
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
        // included — saves a Contains() per row in the common case. Also
        // skipped when the user has selected a specific process: that
        // explicit selection wins, otherwise unchecking the selected node's
        // own box (or an ancestor's) would silently wipe its exception list.
        if (_inclusionFilterActive && !_hasSpecificProcessSelected)
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
            || (_inclusionFilterActive && !_hasSpecificProcessSelected);

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
            Timeline.HighlightTimestamp(null);
            return;
        }

        // Drop a vertical marker on the timeline at this exception's
        // timestamp so the user can see WHERE in the run it came from.
        Timeline.HighlightTimestamp(v.Exception.TimestampMS);

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
