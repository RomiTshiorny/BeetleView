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
    private readonly Dictionary<string, SolidColorBrush> _typeBrushCache = new();

    // Cancels any in-flight render when data changes or the size changes —
    // without this two renders could race and double-add elements.
    private CancellationTokenSource? _renderCts;

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

    public double? SelectionStartMSec { get; private set; }
    public double? SelectionEndMSec { get; private set; }

    /// <summary>
    /// Raised when the user finishes a drag selection or clears it. Both
    /// arguments are null when the selection is cleared; otherwise they are
    /// in milliseconds relative to session start, with start &lt;= end.
    /// </summary>
    public event Action<double?, double?>? TimeRangeChanged;

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
        ClearSelectionInternal(notify: false);
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
        ScheduleRender();
    }

    public void Clear()
    {
        _rows = Array.Empty<TimelineRowViewModel>();
        _maxMSec = 1;
        ClearSelectionInternal(notify: false);
        ScheduleRender();
    }

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

        DrawCanvas.Children.Clear();
        _selectionOverlay = null; // gets re-created below if a selection exists

        if (_rows.Count == 0)
        {
            DrawCanvas.Width = 0;
            DrawCanvas.Height = 0;
            _renderedRowCount = 0;
            EmptyText.Visibility = Visibility.Visible;
            UpdateStatusBar();
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;

        double availableWidth = VScroll.ViewportWidth > 0 ? VScroll.ViewportWidth : ActualWidth;
        if (availableWidth <= 0) return;

        // Cap rows: keep the most-exception-having processes, sorted by start
        // time so they still read chronologically.
        IReadOnlyList<TimelineRowViewModel> rowsToRender;
        if (_rows.Count > MaxRowsRendered)
        {
            var ordered = new List<TimelineRowViewModel>(_rows);
            ordered.Sort((a, b) => b.Exceptions.Count.CompareTo(a.Exceptions.Count));
            ordered.RemoveRange(MaxRowsRendered, ordered.Count - MaxRowsRendered);
            ordered.Sort((a, b) => a.StartMSec.CompareTo(b.StartMSec));
            rowsToRender = ordered;
            _truncated = true;
        }
        else
        {
            rowsToRender = _rows;
            _truncated = false;
        }

        _renderedRowCount = rowsToRender.Count;
        UpdateStatusBar();

        DrawCanvas.Width = availableWidth;
        DrawCanvas.Height = HeaderHeight + rowsToRender.Count * RowHeight;

        _timelineLeftPx = LabelWidth;
        _timelineWidthPx = availableWidth - LabelWidth - RightPadding;
        if (_timelineWidthPx < 50) _timelineWidthPx = 50;

        DrawTimeAxis(_timelineLeftPx, _timelineWidthPx);
        DrawGridlines(_timelineLeftPx, _timelineWidthPx, rowsToRender.Count);
        await Task.Yield();
        if (ct.IsCancellationRequested) return;

        for (int i = 0; i < rowsToRender.Count; i++)
        {
            if (ct.IsCancellationRequested) return;
            DrawRow(rowsToRender[i], i, _timelineLeftPx, _timelineWidthPx);

            if ((i + 1) % RenderYieldEvery == 0)
            {
                await Task.Yield();
                if (ct.IsCancellationRequested) return;
            }
        }

        // Re-overlay the selection (if any) so it sits on top of the lanes
        // and survives a resize-driven re-render.
        if (SelectionStartMSec is double s && SelectionEndMSec is double e)
        {
            EnsureSelectionOverlay();
            PositionOverlayFromMSec(s, e);
        }
    }

    private void DrawTimeAxis(double left, double width)
    {
        DrawCanvas.Children.Add(new Line
        {
            X1 = LeftPadding,
            X2 = left + width,
            Y1 = HeaderHeight - 0.5,
            Y2 = HeaderHeight - 0.5,
            Stroke = AxisBrush,
            StrokeThickness = 1,
        });

        int targetTicks = Math.Max(2, (int)(width / 120.0));
        double step = NiceStep(_maxMSec / targetTicks);
        if (step <= 0) step = _maxMSec;

        for (double t = 0; t <= _maxMSec + 0.5; t += step)
        {
            double x = left + (t / _maxMSec) * width;
            DrawCanvas.Children.Add(new Line
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
            DrawCanvas.Children.Add(label);
        }
    }

    private void DrawGridlines(double left, double width, int rowCount)
    {
        int targetTicks = Math.Max(2, (int)(width / 120.0));
        double step = NiceStep(_maxMSec / targetTicks);
        if (step <= 0) return;

        double bottom = HeaderHeight + rowCount * RowHeight;
        for (double t = step; t < _maxMSec; t += step)
        {
            double x = left + (t / _maxMSec) * width;
            DrawCanvas.Children.Add(new Line
            {
                X1 = x, X2 = x,
                Y1 = HeaderHeight,
                Y2 = bottom,
                Stroke = GridBrush,
                StrokeThickness = 1,
            });
        }
    }

    private void DrawRow(TimelineRowViewModel row, int index, double left, double width)
    {
        double y = HeaderHeight + index * RowHeight;
        double midY = y + RowHeight / 2;

        var label = new TextBlock
        {
            Text = row.Label,
            FontSize = 11,
            Foreground = LabelBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = LabelWidth - LeftPadding - 8,
        };
        Canvas.SetLeft(label, LeftPadding);
        Canvas.SetTop(label, y + 4);
        DrawCanvas.Children.Add(label);

        double xStart = left + (row.StartMSec / _maxMSec) * width;
        double xStop = left + (row.StopMSec / _maxMSec) * width;
        if (xStop < xStart) xStop = xStart;

        double barWidth = Math.Max(2, xStop - xStart);
        var bar = new Rectangle
        {
            Width = barWidth,
            Height = 3,
            Fill = BarBrush,
        };
        Canvas.SetLeft(bar, xStart);
        Canvas.SetTop(bar, midY + 4);
        DrawCanvas.Children.Add(bar);

        // Exception markers — pixel-binned per column. Within a single pixel
        // column we keep only one tick (first exception type seen wins). This
        // collapses tens of thousands of overlapping ticks down to at most
        // ~timelineWidth elements per row.
        double markerTop = y + 2;
        double markerHeight = RowHeight - 4;
        double scale = width / _maxMSec;
        int minCol = (int)left - 1;
        int maxCol = (int)(left + width) + 1;
        var seen = new HashSet<int>();
        foreach (var ex in row.Exceptions)
        {
            int col = (int)(left + ex.TimestampMSec * scale);
            if (col < minCol || col > maxCol) continue;
            if (!seen.Add(col)) continue;

            var tick = new Rectangle
            {
                Width = 2,
                Height = markerHeight,
                Fill = BrushForType(ex.ExceptionType),
            };
            Canvas.SetLeft(tick, col);
            Canvas.SetTop(tick, markerTop);
            DrawCanvas.Children.Add(tick);
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
            // Plain click — clear any existing selection.
            ClearSelectionInternal(notify: true);
            return;
        }

        double startMs = PxToMSec(xLeft);
        double endMs = PxToMSec(xRight);
        SelectionStartMSec = startMs;
        SelectionEndMSec = endMs;

        EnsureSelectionOverlay();
        PositionOverlayFromMSec(startMs, endMs);

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
        (x - _timelineLeftPx) / _timelineWidthPx * _maxMSec;

    // -------------------- Status bar --------------------

    private void UpdateStatusBar()
    {
        var parts = new List<string>(3);

        if (SelectionStartMSec is double s && SelectionEndMSec is double e)
        {
            parts.Add($"Time range: {FormatMSec(s)} – {FormatMSec(e)} (drag again or click to clear)");
        }
        if (_truncated)
        {
            parts.Add($"Showing top {MaxRowsRendered:N0} processes by exception count (of {_rows.Count:N0})");
        }
        else if (_rows.Count > 0 && _renderedRowCount > 0)
        {
            parts.Add($"{_rows.Count:N0} processes  •  Click and drag on the timeline to filter by time");
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
