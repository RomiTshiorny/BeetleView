using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RecorderProcess = GuiLabs.Dotnet.Recorder.Process;

namespace BeetleView.ViewModels;

/// <summary>
/// Node in the Processes tree. A null <see cref="Process"/> represents the
/// synthetic root sentinel ("(All processes)") that owns every other process.
///
/// The view-model is notifying so checkbox toggles (<see cref="IsIncluded"/>)
/// can be data-bound TwoWay, and the <c>Children</c> collection is observable
/// so the search filter can mutate visibility in place without rebuilding the
/// tree (which would otherwise lose checkbox state).
/// </summary>
public sealed class ProcessViewModel : INotifyPropertyChanged
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

    /// <summary>
    /// Unfiltered list of children for this node. The view-bound
    /// <see cref="Children"/> collection is a (potentially filtered) subset
    /// of this and is mutated in place by the search filter.
    /// </summary>
    public IReadOnlyList<ProcessViewModel> AllChildren { get; init; } = Array.Empty<ProcessViewModel>();

    /// <summary>
    /// Children currently visible in the tree. Backed by an
    /// <see cref="ObservableCollection{T}"/> so the search filter can mutate
    /// visibility in place — preserving each node's identity (and therefore
    /// its checkbox state) across filter changes.
    /// </summary>
    public ObservableCollection<ProcessViewModel> Children { get; } = new();

    /// <summary>
    /// Initial expansion state for the TreeView container. We expand every
    /// node by default so the hierarchy is visible without manual clicking.
    /// </summary>
    public bool IsExpanded { get; init; }

    private bool _isIncluded = true;
    /// <summary>
    /// True when this process should participate in the timeline and the
    /// exception list. Unchecking the checkbox sets this false; the change
    /// propagates to descendants (handled in code-behind so we can suppress
    /// re-entrant Toggled callbacks).
    /// </summary>
    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (_isIncluded == value) return;
            _isIncluded = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public bool MatchesFilter(string filter)
    {
        if (filter.Length == 0) return true;
        return DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (Process is not null && Pid.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Sets <see cref="IsIncluded"/> on this node and every descendant
    /// (walking <see cref="AllChildren"/>, not the filtered view).
    /// </summary>
    public void SetIncludedRecursive(bool included)
    {
        IsIncluded = included;
        foreach (var c in AllChildren) c.SetIncludedRecursive(included);
    }
}
