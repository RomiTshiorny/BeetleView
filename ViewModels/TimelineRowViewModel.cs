using System.Collections.Generic;

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
}

public sealed record ExceptionMarker(double TimestampMSec, string ExceptionType);
