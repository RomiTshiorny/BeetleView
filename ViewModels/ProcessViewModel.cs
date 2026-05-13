using System;
using System.Collections.Generic;
using RecorderProcess = GuiLabs.Dotnet.Recorder.Process;

namespace BeetleView.ViewModels;

/// <summary>
/// Node in the Processes tree. A null <see cref="Process"/> represents the
/// synthetic root sentinel ("(All processes)") that owns every other process.
/// </summary>
public sealed class ProcessViewModel
{
    public RecorderProcess? Process { get; init; }
    public string DisplayName { get; init; } = "";
    public int Pid { get; init; }
    public int ExceptionCount { get; init; }

    /// <summary>
    /// Pre-formatted "pid=N  •  " prefix so the data template can keep a
    /// single line layout for both real rows and the sentinel (which renders
    /// just the exception count without a pid).
    /// </summary>
    public string PidPrefix { get; init; } = "";

    public IReadOnlyList<ProcessViewModel> Children { get; init; } = Array.Empty<ProcessViewModel>();

    /// <summary>
    /// Initial expansion state for the TreeView container. We expand the
    /// sentinel so the user sees the top-level processes immediately; deeper
    /// nodes stay collapsed and are expanded on demand.
    /// </summary>
    public bool IsExpanded { get; init; }

    public bool MatchesFilter(string filter)
    {
        if (filter.Length == 0) return true;
        return DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (Process is not null && Pid.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase));
    }
}
