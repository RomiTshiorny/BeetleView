using System.Collections.Generic;
using RecorderProcess = GuiLabs.Dotnet.Recorder.Process;

namespace BeetleView.ViewModels;

/// <summary>
/// One lane in the process timeline visualizer: a single process's lifetime
/// bar plus a vertical tick per exception it threw.
/// </summary>
public sealed class TimelineRowViewModel
{
    public required string Label { get; init; }
    public required int Pid { get; init; }
    public required double StartMSec { get; init; }
    public required double StopMSec { get; init; }
    public required IReadOnlyList<ExceptionMarker> Exceptions { get; init; }

    /// <summary>
    /// Pre-grouped exception runs: consecutive same-type exceptions are
    /// merged into a single span. Computed once at load time so the
    /// timeline doesn't re-sort/re-group on every render. Stable across
    /// zoom and resize so blocks don't visually merge/split as the user
    /// pans around.
    /// </summary>
    public required IReadOnlyList<ExceptionRun> ExceptionRuns { get; init; }

    /// <summary>
    /// The underlying recorder process this row represents. Used to cross-
    /// reference the row with its <see cref="ProcessViewModel"/> in the tree
    /// (e.g. when applying the checkbox-driven inclusion filter).
    /// </summary>
    public RecorderProcess? Process { get; init; }
}

public sealed record ExceptionMarker(double TimestampMSec, string ExceptionType);

/// <summary>
/// A contiguous run of same-type exceptions from <see cref="StartMSec"/>
/// through <see cref="EndMSec"/> (inclusive). A single one-off exception
/// is represented as a run with StartMSec == EndMSec.
/// </summary>
public sealed record ExceptionRun(
    double StartMSec,
    double EndMSec,
    string ExceptionType,
    int Count);
