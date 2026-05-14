using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeetleView.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace BeetleView.Controls;

/// <summary>
/// Renders a horizontal swimlane per process: a lifetime bar plus a vertical
/// tick per exception event, colored by exception type. Supports drag-to-
/// select a time range (raised via <see cref="TimeRangeChanged"/>; consumers
/// typically use that to filter the exceptions list).
///
/// Performance: exception markers are binned by pixel column per row so a
/// process that threw 50,000 exceptions yields at most ~timelineWidth ticks
/// rather than 50,000 individual XAML elements. Rendering is also batched
/// with cooperative yields so very wide sessions don't hang the UI thread.
/// </summary>
public sealed partial class ProcessTimelineControl : UserControl
{
    private const double LabelWidth = 220;
    private const double RowHeight = 22;
    private const double HeaderHeight = 24;
    private const double LeftPadding = 6;
    private const double RightPadding = 12;

    private const int MaxRowsRendered = 400;
    private const int RenderYieldEvery = 25;

    // Below this drag width (in pixels) treat the gesture as a click and
    // clear any existing selection instead of committing a new one.
    private const double MinDragWidthPx = 3;

    private static readonly SolidColorBrush BarBrush = new(Color.FromArgb(0xFF, 0x60, 0x80, 0xA8));
    private static readonly SolidColorBrush LabelBrush = new(Color.FromArgb(0xFF, 0xDD, 0xDD, 0xDD));
    private static readonly SolidColorBrush AxisBrush = new(Color.FromArgb(0x80, 0xCC, 0xCC, 0xCC));
    private static readonly SolidColorBrush GridBrush = new(Color.FromArgb(0x30, 0xCC, 0xCC, 0xCC));
    private static readonly SolidColorBrush SelectionFill = new(Color.FromArgb(0x40, 0x4A, 0x8C, 0xE6));
    private static readonly SolidColorBrush SelectionStroke = new(Color.FromArgb(0xC0, 0x4A, 0x8C, 0xE6));

    private static readonly Color[] Palette = new[]
    {
        Color.FromArgb(0xFF, 0xE5, 0x4B, 0x4B),
        Color.FromArgb(0xFF, 0xE6, 0x8A, 0x2E),
        Color.FromArgb(0xFF, 0xE6, 0xC8, 0x2E),
        Color.FromArgb(0xFF, 0x6F, 0xC8, 0x3C),
        Color.FromArgb(0xFF, 0x2E, 0xB3, 0x6B),
        Color.FromArgb(0xFF, 0x2E, 0xC8, 0xC8),
        Color.FromArgb(0xFF, 0x4A, 0x8C, 0xE6),
        Color.FromArgb(0xFF, 0x8E, 0x6E, 0xE6),
        Color.FromArgb(0xFF, 0xC8, 0x55, 0xE6),
        Color.FromArgb(0xFF, 0xE6, 0x66, 0xA6),
        Color.FromArgb(0xFF, 0xA0, 0x6A, 0x42),
        Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E),
    };

    private IReadOnlyList<TimelineRowViewModel> _rows = Array.Empty<TimelineRowViewModel>();
    private double _maxMSec = 1;
    // Active viewport in MSec. Defaults to the full session (0.._maxMSec)
    // and shrinks when the user drags a range on the timeline. All
    // px↔MSec math goes through these so rendering, gridlines, axis labels,
    // and pointer events all stay consistent.
    private double _viewStartMSec;
    private double _viewEndMSec = 1;
    private readonly Dictionary<string, SolidColorBrush> _typeBrushCache = new();

    // Cancels any in-flight render when data changes or the size changes —
    // without this two renders could race and double-add elements.
    private CancellationTokenSource? _renderCts;

    // Static children of DrawCanvas (labels, axis, gridlines). Tracked so a
    // re-render on zoom only clears these — leaving the expensive ZoomLayer
    // bars/ticks (and the named ZoomClip / SelectionOverlay) intact.
    private readonly List<UIElement> _staticElements = new();

    // Rows + width signature used by the last ZoomLayer build. If either
    // changes we rebuild; otherwise zoom is just a CompositeTransform update,
    // which is essentially free.
    private IReadOnlyList<TimelineRowViewModel>? _zoomLayerBuiltRows;
    private double _zoomLayerBuiltWidth = -1;
    private int _zoomLayerBuiltRowCount;

    // Cached after each render so drag handlers can convert pointer X back
    // to a millisecond offset without recomputing layout.
    private double _timelineLeftPx = LabelWidth;
    private double _timelineWidthPx;
    private bool _truncated;
    private int _renderedRowCount;

    // Drag state. Selection coordinates are stored in MSec so they survive
    // re-renders (e.g. on resize). The overlay rectangle is a single Canvas
    // child whose Left/Width/Height we mutate directly.
    private bool _isDragging;
    private bool _hasDragMoved;
    private double _dragOriginX;
    private Rectangle? _selectionOverlay;

    // Timestamp of the currently selected exception (from the host's
    // exception list). Drawn as a vertical marker line in the static
    // layer so the user can see which row+time the selected exception
    // came from. null = no highlight.
    private double? _highlightMSec;
    private Line? _highlightMarker;

    public double? SelectionStartMSec { get; private set; }
    public double? SelectionEndMSec { get; private set; }

    /// <summary>
    /// Raised when the user finishes a drag selection or clears it. Both
    /// arguments are null when the selection is cleared; otherwise they are
    /// in milliseconds relative to session start, with start &lt;= end. The
    /// timeline simultaneously zooms its viewport to that range, so a drag
    /// scopes both the timeline display and any downstream consumers (e.g.
    /// the exceptions list) to the same time window.
    /// </summary>
    public event Action<double?, double?>? TimeRangeChanged;

    /// <summary>
    /// Diagnostic event fired whenever the timeline's data state changes
    /// (set / cleared / visible-rows replaced / rendered). Hosts can wire
    /// this to a debug surface to trace unexpected re-renders.
    /// </summary>
    public event Action<string>? DiagnosticState;

    /// <summary>
    /// Highlights a specific timestamp (in MSec from session start) on the
    /// timeline with a vertical marker line. Pass null to clear. Used by
    /// the host to show which exception is currently selected.
    /// </summary>
    public void HighlightTimestamp(double? msec)
    {
        _highlightMSec = msec;
        UpdateHighlightMarker();
    }

    private void UpdateHighlightMarker()
    {
        if (_highlightMarker is null)
        {
            _highlightMarker = new Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD0, 0x40)),
                StrokeThickness = 2,
                IsHitTestVisible = false,
            };
            DrawCanvas.Children.Add(_highlightMarker);
        }

        if (_highlightMSec is null || _timelineWidthPx <= 0 || ViewSpan <= 0)
        {
            _highlightMarker.Visibility = Visibility.Collapsed;
            return;
        }

        double t = _highlightMSec.Value;
        if (t < _viewStartMSec || t > _viewEndMSec)
        {
            // Outside the current zoom window — hide rather than clip
            // (the user knows where the selected exception is from the
            // list itself; cluttering with an off-screen indicator helps
            // no one).
            _highlightMarker.Visibility = Visibility.Collapsed;
            return;
        }

        double x = _timelineLeftPx + (t - _viewStartMSec) / ViewSpan * _timelineWidthPx;
        _highlightMarker.X1 = x;
        _highlightMarker.X2 = x;
        _highlightMarker.Y1 = HeaderHeight;
        _highlightMarker.Y2 = DrawCanvas.Height;
        _highlightMarker.Visibility = Visibility.Visible;
        // Make sure it paints on top of bars/labels.
        Canvas.SetZIndex(_highlightMarker, 100);
    }

    public ProcessTimelineControl()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ScheduleRender();

        DrawCanvas.PointerPressed += DrawCanvas_PointerPressed;
        DrawCanvas.PointerMoved += DrawCanvas_PointerMoved;
        DrawCanvas.PointerReleased += DrawCanvas_PointerReleased;
        DrawCanvas.PointerCaptureLost += DrawCanvas_PointerCaptureLost;
    }

    /// <summary>Assigns the data and triggers a render.</summary>
    public void SetData(IReadOnlyList<TimelineRowViewModel> rows, double maxMSec)
    {
        _rows = rows ?? Array.Empty<TimelineRowViewModel>();
        _maxMSec = maxMSec > 0 ? maxMSec : 1;
        InvalidateZoomLayer();
        ResetViewport();
        ClearSelectionInternal(notify: false);
        DiagnosticState?.Invoke($"Timeline.SetData rows={_rows.Count} max={_maxMSec:0}ms");
        ScheduleRender();
    }

    /// <summary>
    /// Replaces the visible rows without touching the time axis or the
    /// active selection. Used when callers want to hide a subset of rows
    /// (e.g. unchecked processes, or processes outside a selected range)
    /// while keeping the drag-selection overlay intact.
    /// </summary>
    public void SetVisibleRows(IReadOnlyList<TimelineRowViewModel> rows)
    {
        _rows = rows ?? Array.Empty<TimelineRowViewModel>();
        InvalidateZoomLayer();
        DiagnosticState?.Invoke($"Timeline.SetVisibleRows rows={_rows.Count}");
        ScheduleRender();
    }

    public void Clear()
    {
        _rows = Array.Empty<TimelineRowViewModel>();
        _maxMSec = 1;
        InvalidateZoomLayer();
        ResetViewport();
        ClearSelectionInternal(notify: false);
        DiagnosticState?.Invoke("Timeline.Clear");
        ScheduleRender();
    }

    private void InvalidateZoomLayer()
    {
        _zoomLayerBuiltRows = null;
        _zoomLayerBuiltWidth = -1;
        ZoomLayer.Children.Clear();
    }

    private void ResetViewport()
    {
        _viewStartMSec = 0;
        _viewEndMSec = _maxMSec > 0 ? _maxMSec : 1;
    }

    private double ViewSpan => Math.Max(1e-6, _viewEndMSec - _viewStartMSec);

    /// <summary>Public API for callers to clear the current selection.</summary>
    public void ClearSelection() => ClearSelectionInternal(notify: true);

    private void ClearSelectionInternal(bool notify)
    {
        SelectionStartMSec = null;
        SelectionEndMSec = null;
        if (_selectionOverlay is not null)
        {
            _selectionOverlay.Visibility = Visibility.Collapsed;
        }
        UpdateStatusBar();
        if (notify)
        {
            TimeRangeChanged?.Invoke(null, null);
        }
    }

    private void ScheduleRender()
    {
        _renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        _renderCts = cts;
        _ = RenderAsync(cts.Token);
    }

    private async Task RenderAsync(CancellationToken ct)
    {
        // Yield once before mutating the canvas so the rest of the page
        // (toolbar, process tree, exceptions list) paints first.
        await Task.Yield();
        if (ct.IsCancellationRequested) return;

        // Clear ONLY the static layer (labels/axis/gridlines/overlay).
        // ZoomLayer is preserved unless data/width changed (see below).
        ClearStaticElements();
        _selectionOverlay = null; // gets re-created below if a selection exists

        if (_rows.Count == 0)
        {
            DrawCanvas.Width = 0;
            DrawCanvas.Height = 0;
            _renderedRowCount = 0;
            _truncated = false;
            EmptyText.Visibility = Visibility.Visible;
            InvalidateZoomLayer();
            DiagnosticState?.Invoke($"Render: EMPTY (_rows.Count=0) — showing 'No session loaded'");
            UpdateStatusBar();
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;

        double availableWidth = VScroll.ViewportWidth > 0 ? VScroll.ViewportWidth : ActualWidth;
        if (availableWidth <= 0) return;

        var rowList = new List<TimelineRowViewModel>(_rows);

        IReadOnlyList<TimelineRowViewModel> rowsToRender;
        if (rowList.Count > MaxRowsRendered)
        {
            rowList.Sort((a, b) => b.Exceptions.Count.CompareTo(a.Exceptions.Count));
            rowList.RemoveRange(MaxRowsRendered, rowList.Count - MaxRowsRendered);
            rowList.Sort((a, b) => a.StartMSec.CompareTo(b.StartMSec));
            rowsToRender = rowList;
            _truncated = true;
        }
        else
        {
            rowList.Sort((a, b) => a.StartMSec.CompareTo(b.StartMSec));
            rowsToRender = rowList;
            _truncated = false;
        }

        _renderedRowCount = rowsToRender.Count;
        UpdateStatusBar();

        DrawCanvas.Width = availableWidth;
        DrawCanvas.Height = HeaderHeight + rowsToRender.Count * RowHeight;

        _timelineLeftPx = LabelWidth;
        _timelineWidthPx = availableWidth - LabelWidth - RightPadding;
        if (_timelineWidthPx < 50) _timelineWidthPx = 50;

        // Rebuild the heavy ZoomLayer only if the underlying _rows
        // reference, count, or canvas width changed. ScheduleRender called
        // from a pure zoom (drag-select / reset) hits this with the same
        // _rows, so we keep the existing rectangles and only update the
        // ZoomTransform below — turning zoom into a near-free operation
        // even for thousands of bars.
        bool needsZoomRebuild =
            !ReferenceEquals(_zoomLayerBuiltRows, _rows) ||
            Math.Abs(_zoomLayerBuiltWidth - _timelineWidthPx) > 0.5 ||
            _zoomLayerBuiltRowCount != rowsToRender.Count;

        if (needsZoomRebuild)
        {
            BuildZoomLayer(rowsToRender);
            _zoomLayerBuiltRows = _rows;
            _zoomLayerBuiltWidth = _timelineWidthPx;
            _zoomLayerBuiltRowCount = rowsToRender.Count;
        }

        // Position + clip the zoom layers. ZoomClip is fixed at the
        // timeline column so bars never spill into labels; ZoomLayer is
        // session-full-scale and gets transformed.
        Canvas.SetLeft(ZoomClip, _timelineLeftPx);
        Canvas.SetTop(ZoomClip, 0);
        ZoomClip.Width = _timelineWidthPx;
        ZoomClip.Height = DrawCanvas.Height;
        ZoomClip.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, _timelineWidthPx, DrawCanvas.Height),
        };
        ZoomLayer.Width = _timelineWidthPx;
        ZoomLayer.Height = DrawCanvas.Height;

        // Apply the zoom transform: bars were rendered at "session-full"
        // local x = t / _maxMSec * _timelineWidthPx. Map that to viewport:
        //   screenX = (t - viewStart) * (_timelineWidthPx / ViewSpan)
        //   = localX * (_maxMSec / ViewSpan) + (-viewStart * _timelineWidthPx / ViewSpan)
        //
        // Hard-cap ScaleX so any unexpectedly tight view doesn't blow up
        // the canvas transform (the drag handler already clamps the view
        // span to 1 ms; this is a defensive ceiling that matches that).
        double rawScale = _maxMSec / ViewSpan;
        double maxScale = Math.Max(1.0, _maxMSec);   // ≥ 1 ms span ⇒ scale ≤ _maxMSec
        double scaleX = Math.Min(rawScale, maxScale);
        double translateX = -_viewStartMSec * _timelineWidthPx / ViewSpan;
        ZoomTransform.ScaleX = scaleX;
        ZoomTransform.TranslateX = translateX;
        // Counter-scale tick rectangles so they stay 2px regardless of zoom.
        double invScale = 1.0 / scaleX;
        foreach (var child in ZoomLayer.Children)
        {
            if (child is Rectangle r && r.Tag is "tick")
            {
                if (r.RenderTransform is CompositeTransform ctr)
                {
                    ctr.ScaleX = invScale;
                }
            }
        }

        // Draw the static layer (labels, axis, gridlines) — these reflect
        // the current viewport and so are redrawn on every zoom. They're
        // cheap: O(rowCount) labels + ~O(width/120) axis ticks.
        DrawTimeAxis(_timelineLeftPx, _timelineWidthPx);
        DrawGridlines(_timelineLeftPx, _timelineWidthPx, rowsToRender.Count);
        DrawRowLabels(rowsToRender);

        // Reposition the timestamp highlight marker (if any) for the new
        // viewport. Lives in the static layer because zoom changes the
        // viewport range; we want a sharp 1px line regardless of scale.
        UpdateHighlightMarker();
    }

    private static string FormatMs(double ms)
    {
        var ts = System.TimeSpan.FromMilliseconds(ms);
        return $"{ts.TotalSeconds:0.000}s";
    }

    private static string FormatMsRange(double startMs, double endMs)
    {
        double duration = endMs - startMs;
        return $"{FormatMs(startMs)} → {FormatMs(endMs)}  ({duration:0.#} ms)";
    }

    private void EmitExceptionBlock(
        double startMs,
        double endMs,
        string type,
        int count,
        double top,
        double height,
        double sessionScale)
    {
        double xStart = startMs * sessionScale;
        double xEnd = endMs * sessionScale;
        double naturalWidth = xEnd - xStart;

        // Three render modes:
        //  • Single occurrence (count == 1) and zero/near-zero duration →
        //    a thin tick that counter-scales with zoom so it stays ~2px
        //    wide. Represents a single instant.
        //  • A real burst (count > 1) → a solid block. Width is the
        //    larger of (a) the actual duration in pixels at session-full
        //    scale and (b) a count-scaled minimum so the block stays
        //    visible at session-full zoom. The block scales naturally with
        //    zoom (no counter-scale) so the user can see when the burst
        //    started/ended once they zoom in.
        //  • A wide single-occurrence run (count == 1, large duration) →
        //    also a duration block.
        bool isPoint = count == 1 && naturalWidth < 1.0;

        double width;
        if (isPoint)
        {
            width = 2;
        }
        else if (count > 1)
        {
            // Count-scaled minimum so denser bursts read as bigger blocks
            // even when their actual duration is sub-pixel at session-full.
            // log2(1)=0, log2(10)≈3.3, log2(100)≈6.6, log2(1000)≈10.
            double countMin = 3.0 + Math.Min(12.0, Math.Log2(count) * 1.4);
            width = Math.Max(naturalWidth, countMin);
        }
        else
        {
            width = Math.Max(1.0, naturalWidth);
        }

        var rect = new Rectangle
        {
            Width = width,
            Height = height,
            Fill = BrushForType(type),
        };

        // Tooltip so a user can hover a colored block and identify what
        // exception type and how many occurrences it represents — answers
        // the "what do these colors mean?" question without needing a
        // separate legend, and folds in the time range too.
        string tipText = count > 1
            ? $"{type}\n{count:N0} exceptions\n{FormatMsRange(startMs, endMs)}"
            : $"{type}\n{FormatMs(startMs)}";
        ToolTipService.SetToolTip(rect, tipText);

        if (isPoint)
        {
            // Keep ticks visually 2px regardless of zoom.
            rect.Tag = "tick";
            rect.RenderTransform = new CompositeTransform { ScaleX = 1.0 };
            rect.RenderTransformOrigin = new Point(0, 0);
        }

        Canvas.SetLeft(rect, xStart);
        Canvas.SetTop(rect, top);
        ZoomLayer.Children.Add(rect);
    }

    private void ClearStaticElements()
    {
        foreach (var el in _staticElements)
        {
            DrawCanvas.Children.Remove(el);
        }
        _staticElements.Clear();
    }

    private void AddStatic(UIElement el)
    {
        DrawCanvas.Children.Add(el);
        _staticElements.Add(el);
    }

    /// <summary>
    /// Raised when the user picks "Show only this process" from a timeline
    /// row label's context menu. Carries the row so the host can both
    /// (a) populate the process search filter and (b) drill into that
    /// specific process (updating timeline scope and exception list).
    /// </summary>
    public event System.EventHandler<TimelineRowViewModel>? LabelFilterRequested;

    private void DrawRowLabels(IReadOnlyList<TimelineRowViewModel> rows)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            double y = HeaderHeight + i * RowHeight;
            string fullLabel = row.Label;
            // Label = "image.exe pid". Pull image off for the filter use case
            // — the pid changes per session so it's a poor filter token.
            int lastSpace = fullLabel.LastIndexOf(' ');
            string imageName = lastSpace > 0 ? fullLabel.Substring(0, lastSpace) : fullLabel;

            var label = new TextBlock
            {
                Text = fullLabel,
                FontSize = 11,
                Foreground = LabelBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Width = LabelWidth - LeftPadding - 8,
                IsHitTestVisible = true,
            };
            ToolTipService.SetToolTip(label, fullLabel);

            // Right-click → "Copy name" / "Filter by this process".
            // We hand-build the flyout per label so each entry captures the
            // right row in its click handler.
            var flyout = new MenuFlyout();
            var copyItem = new MenuFlyoutItem { Text = "Copy name" };
            copyItem.Click += (_, _) =>
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(imageName);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            };
            var copyFullItem = new MenuFlyoutItem { Text = "Copy name + pid" };
            copyFullItem.Click += (_, _) =>
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(fullLabel);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            };
            var filterItem = new MenuFlyoutItem { Text = "Show only this process" };
            filterItem.Click += (_, _) => LabelFilterRequested?.Invoke(this, row);
            flyout.Items.Add(copyItem);
            flyout.Items.Add(copyFullItem);
            flyout.Items.Add(filterItem);
            label.ContextFlyout = flyout;

            Canvas.SetLeft(label, LeftPadding);
            Canvas.SetTop(label, y + 4);
            AddStatic(label);
        }
    }

    private void BuildZoomLayer(IReadOnlyList<TimelineRowViewModel> rows)
    {
        ZoomLayer.Children.Clear();
        double width = _timelineWidthPx;
        double sessionScale = width / _maxMSec;

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            double y = HeaderHeight + i * RowHeight;
            double midY = y + RowHeight / 2;

            double xStart = row.StartMSec * sessionScale;
            double xStop = row.StopMSec * sessionScale;
            // Min 3px (un-zoomed) so a short-lived process is visible.
            // Anchored at xStart so the bar grows rightward; under heavy
            // zoom-out this can briefly clip past the row, but it's better
            // than an invisible row that looks like a render bug.
            double barWidth = Math.Max(3.0, xStop - xStart);
            var bar = new Rectangle
            {
                Width = barWidth,
                Height = 3,
                Fill = BarBrush,
            };
            Canvas.SetLeft(bar, xStart);
            Canvas.SetTop(bar, midY + 4);
            ZoomLayer.Children.Add(bar);

            // Exception runs are pre-computed at load time (sorted +
            // grouped by type with a small gap tolerance), so rendering is
            // just a straight emit per run — no sort, no group, no merge.
            double markerTop = y + 2;
            double markerHeight = RowHeight - 4;
            foreach (var run in row.ExceptionRuns)
            {
                EmitExceptionBlock(run.StartMSec, run.EndMSec, run.ExceptionType, run.Count, markerTop, markerHeight, sessionScale);
            }
        }
    }

    private void DrawTimeAxis(double left, double width)
    {
        AddStatic(new Line
        {
            X1 = LeftPadding,
            X2 = left + width,
            Y1 = HeaderHeight - 0.5,
            Y2 = HeaderHeight - 0.5,
            Stroke = AxisBrush,
            StrokeThickness = 1,
        });

        double span = ViewSpan;
        int targetTicks = Math.Max(2, (int)(width / 120.0));
        double step = NiceStep(span / targetTicks);
        if (step <= 0) step = span;

        double firstTick = Math.Ceiling(_viewStartMSec / step) * step;
        double scale = width / span;
        for (double t = firstTick; t <= _viewEndMSec + 0.5; t += step)
        {
            double x = left + (t - _viewStartMSec) * scale;
            AddStatic(new Line
            {
                X1 = x, X2 = x,
                Y1 = HeaderHeight - 6,
                Y2 = HeaderHeight,
                Stroke = AxisBrush,
                StrokeThickness = 1,
            });

            var label = new TextBlock
            {
                Text = FormatMSec(t),
                FontSize = 10,
                Foreground = LabelBrush,
            };
            Canvas.SetLeft(label, x + 2);
            Canvas.SetTop(label, 2);
            AddStatic(label);
        }
    }

    private void DrawGridlines(double left, double width, int rowCount)
    {
        double span = ViewSpan;
        int targetTicks = Math.Max(2, (int)(width / 120.0));
        double step = NiceStep(span / targetTicks);
        if (step <= 0) return;

        double scale = width / span;
        double firstTick = Math.Ceiling(_viewStartMSec / step) * step;
        double bottom = HeaderHeight + rowCount * RowHeight;
        for (double t = firstTick; t < _viewEndMSec; t += step)
        {
            double x = left + (t - _viewStartMSec) * scale;
            AddStatic(new Line
            {
                X1 = x, X2 = x,
                Y1 = HeaderHeight,
                Y2 = bottom,
                Stroke = GridBrush,
                StrokeThickness = 1,
            });
        }
    }

    // -------------------- Drag-to-select time range --------------------

    private void DrawCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(DrawCanvas).Position;
        if (pt.X < _timelineLeftPx) return; // ignore clicks in the label column
        if (_timelineWidthPx <= 0) return;

        _isDragging = true;
        _dragOriginX = pt.X;
        _hasDragMoved = false;
        DrawCanvas.CapturePointer(e.Pointer);
        // Deliberately don't mutate the overlay yet — if the user just
        // clicks without dragging (or the gesture is cancelled by a system
        // event before they drag), the previously-committed range stays
        // visible. The overlay only changes once we see actual movement.
    }

    private void DrawCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        var pt = e.GetCurrentPoint(DrawCanvas).Position;
        double x = ClampToTimeline(pt.X);
        double xLeft = Math.Min(_dragOriginX, x);
        double xRight = Math.Max(_dragOriginX, x);
        double width = Math.Max(1, xRight - xLeft);

        // Don't start drawing the in-flight rectangle until the user has
        // actually moved past the click-vs-drag threshold. This stops a
        // single click from briefly clobbering an existing selection.
        if (!_hasDragMoved && width < MinDragWidthPx) return;
        _hasDragMoved = true;

        EnsureSelectionOverlay();
        Canvas.SetLeft(_selectionOverlay!, xLeft);
        Canvas.SetTop(_selectionOverlay!, 0);
        _selectionOverlay!.Width = width;
        _selectionOverlay!.Height = DrawCanvas.Height;
        _selectionOverlay!.Visibility = Visibility.Visible;
    }

    private void DrawCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        FinishDrag(e.GetCurrentPoint(DrawCanvas).Position);
        DrawCanvas.ReleasePointerCapture(e.Pointer);
    }

    private void DrawCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        _hasDragMoved = false;
        // Capture was yanked mid-drag — don't commit a half-formed range.
        // Restore the overlay to whatever was previously committed (or hide
        // it entirely if there was no prior selection) so the user doesn't
        // end up with a leftover half-drawn rectangle.
        if (SelectionStartMSec is double s && SelectionEndMSec is double e2)
        {
            EnsureSelectionOverlay();
            PositionOverlayFromMSec(s, e2);
        }
        else if (_selectionOverlay is not null)
        {
            _selectionOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void FinishDrag(Point endPoint)
    {
        bool wasDrag = _hasDragMoved;
        _isDragging = false;
        _hasDragMoved = false;

        double x = ClampToTimeline(endPoint.X);
        double xLeft = Math.Min(_dragOriginX, x);
        double xRight = Math.Max(_dragOriginX, x);

        if (!wasDrag || xRight - xLeft < MinDragWidthPx)
        {
            // Plain click — reset the zoom to the full session and clear
            // the time-range filter for downstream consumers.
            if (_viewStartMSec > 0 || _viewEndMSec < _maxMSec)
            {
                ResetViewport();
                ScheduleRender();
            }
            ClearSelectionInternal(notify: true);
            return;
        }

        double startMs = PxToMSec(xLeft);
        double endMs = PxToMSec(xRight);

        // Cap zoom-in so the smallest window matches the precision of the
        // exception timestamps themselves (1 ms). Going tighter than this
        // produces a runaway ScaleX (huge canvas dimensions, transform
        // overflow) and crashes the renderer. Re-center the requested range
        // around the drag midpoint so the user sees what they aimed at.
        const double MinViewSpanMs = 1.0;
        if (endMs - startMs < MinViewSpanMs)
        {
            double mid = (startMs + endMs) * 0.5;
            startMs = mid - MinViewSpanMs * 0.5;
            endMs = mid + MinViewSpanMs * 0.5;
        }
        // Don't let the window escape the session bounds.
        if (startMs < 0) { endMs -= startMs; startMs = 0; }
        if (endMs > _maxMSec) { startMs -= (endMs - _maxMSec); endMs = _maxMSec; if (startMs < 0) startMs = 0; }

        SelectionStartMSec = startMs;
        SelectionEndMSec = endMs;

        // Zoom the viewport to the dragged range. Rows that fall entirely
        // outside this window will be dropped by the next render so the
        // user effectively sees "only processes active in this time range".
        _viewStartMSec = startMs;
        _viewEndMSec = endMs;

        // Discard the temporary in-flight selection rectangle — once the
        // viewport equals the selection there's no need to render a separate
        // overlay (the whole timeline IS the selection now).
        if (_selectionOverlay is not null)
        {
            _selectionOverlay.Visibility = Visibility.Collapsed;
        }

        ScheduleRender();
        UpdateStatusBar();
        TimeRangeChanged?.Invoke(startMs, endMs);
    }

    private void EnsureSelectionOverlay()
    {
        if (_selectionOverlay is not null)
        {
            if (!DrawCanvas.Children.Contains(_selectionOverlay))
            {
                DrawCanvas.Children.Add(_selectionOverlay);
            }
            return;
        }

        _selectionOverlay = new Rectangle
        {
            Fill = SelectionFill,
            Stroke = SelectionStroke,
            StrokeThickness = 1,
            IsHitTestVisible = false, // let pointer events fall through to the canvas
        };
        DrawCanvas.Children.Add(_selectionOverlay);
    }

    private void PositionOverlayFromMSec(double startMs, double endMs)
    {
        if (_selectionOverlay is null) return;
        double scale = _timelineWidthPx / _maxMSec;
        double xLeft = _timelineLeftPx + startMs * scale;
        double xRight = _timelineLeftPx + endMs * scale;
        xLeft = Math.Clamp(xLeft, _timelineLeftPx, _timelineLeftPx + _timelineWidthPx);
        xRight = Math.Clamp(xRight, _timelineLeftPx, _timelineLeftPx + _timelineWidthPx);
        Canvas.SetLeft(_selectionOverlay, xLeft);
        Canvas.SetTop(_selectionOverlay, 0);
        _selectionOverlay.Width = Math.Max(1, xRight - xLeft);
        _selectionOverlay.Height = DrawCanvas.Height;
        _selectionOverlay.Visibility = Visibility.Visible;
    }

    private double ClampToTimeline(double x) =>
        Math.Clamp(x, _timelineLeftPx, _timelineLeftPx + _timelineWidthPx);

    private double PxToMSec(double x) =>
        _viewStartMSec + (x - _timelineLeftPx) / _timelineWidthPx * ViewSpan;

    // -------------------- Status bar --------------------

    private void UpdateStatusBar()
    {
        var parts = new List<string>(3);

        if (SelectionStartMSec is double s && SelectionEndMSec is double e)
        {
            parts.Add($"Zoomed: {FormatMSec(s)} – {FormatMSec(e)} (click to reset)");
        }
        if (_truncated)
        {
            parts.Add($"Showing top {MaxRowsRendered:N0} processes by exception count (of {_rows.Count:N0})");
        }
        else if (_rows.Count > 0 && _renderedRowCount > 0)
        {
            parts.Add($"{_renderedRowCount:N0} processes in view  •  Drag to zoom, click to reset");
        }

        if (parts.Count == 0)
        {
            StatusBar.Visibility = Visibility.Collapsed;
            return;
        }

        StatusText.Text = string.Join("    ", parts);
        StatusBar.Visibility = Visibility.Visible;
    }

    // -------------------- Helpers --------------------

    private SolidColorBrush BrushForType(string type)
    {
        type ??= "";
        if (_typeBrushCache.TryGetValue(type, out var brush)) return brush;
        uint hash = 2166136261u;
        foreach (char c in type)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        var color = Palette[(int)(hash % (uint)Palette.Length)];
        brush = new SolidColorBrush(color);
        _typeBrushCache[type] = brush;
        return brush;
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double snap = norm < 1.5 ? 1 : norm < 3.5 ? 2 : norm < 7.5 ? 5 : 10;
        return snap * mag;
    }

    private static string FormatMSec(double ms)
    {
        if (ms < 1000) return $"{ms:0}ms";
        double s = ms / 1000.0;
        if (s < 60) return $"{s:0.##}s";
        int mins = (int)(s / 60);
        double rem = s - mins * 60;
        return $"{mins}m {rem:0}s";
    }
}
