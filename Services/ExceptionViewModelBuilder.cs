using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeetleView.ViewModels;
using GuiLabs.Dotnet.Recorder;
using RecorderProcess = GuiLabs.Dotnet.Recorder.Process;

namespace BeetleView.Services;

/// <summary>
/// Materializes the per-selection list of <see cref="ExceptionViewModel"/>
/// rows off the UI thread. When the selected node is the "(All processes)"
/// sentinel (<c>node.Process is null</c>) the rows span every process in
/// the session; otherwise the rows span the selected process AND every
/// descendant of it in the Processes tree — drilling into a parent should
/// show exceptions from its subtree, not just the (often empty) set of
/// exceptions thrown directly by that exact pid.
/// Output is sorted by timestamp so the UI shows a stable chronological view.
/// </summary>
internal static class ExceptionViewModelBuilder
{
    public static Task<List<ExceptionViewModel>> BuildAsync(
        Session session,
        ProcessViewModel node,
        CancellationToken ct)
    {
        return Task.Run(() => Build(session, node, ct), ct);
    }

    private static List<ExceptionViewModel> Build(Session session, ProcessViewModel node, CancellationToken ct)
    {
        var rows = new List<ExceptionViewModel>();

        if (node.Process is null)
        {
            // Sentinel: every process in the session.
            foreach (var p in session.Processes)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var ex in p.Exceptions)
                {
                    rows.Add(new ExceptionViewModel(p, ex));
                }
            }
        }
        else
        {
            // Specific node: this process plus every descendant.
            AddSubtree(node, rows, ct);
        }

        ct.ThrowIfCancellationRequested();
        rows.Sort((a, b) => a.Exception.Timestamp.CompareTo(b.Exception.Timestamp));
        return rows;
    }

    private static void AddSubtree(ProcessViewModel node, List<ExceptionViewModel> rows, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (node.Process is RecorderProcess p)
        {
            foreach (var ex in p.Exceptions)
            {
                rows.Add(new ExceptionViewModel(p, ex));
            }
        }
        foreach (var child in node.AllChildren)
        {
            AddSubtree(child, rows, ct);
        }
    }
}
