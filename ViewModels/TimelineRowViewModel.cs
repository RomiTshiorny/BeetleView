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
    /// The underlying recorder process this row represents. Used to cross-
    /// reference the row with its <see cref="ProcessViewModel"/> in the tree
    /// (e.g. when applying the checkbox-driven inclusion filter).
    /// </summary>
    public RecorderProcess? Process { get; init; }
}

public sealed record ExceptionMarker(double TimestampMSec, string ExceptionType);
