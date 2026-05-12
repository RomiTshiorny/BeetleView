using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeetleView.ViewModels;
using GuiLabs.Dotnet.Recorder;
using RecorderProcess = GuiLabs.Dotnet.Recorder.Process;

namespace BeetleView.Services;

/// <summary>
/// Materializes the per-selection list of <see cref="ExceptionViewModel"/>
/// rows off the UI thread. When <paramref name="process"/> is null the rows
/// span every process in the session, sorted by timestamp so the UI shows a
/// stable chronological view.
/// </summary>
internal static class ExceptionViewModelBuilder
{
    public static Task<List<ExceptionViewModel>> BuildAsync(
        Session session,
        RecorderProcess? process,
        CancellationToken ct)
    {
        return Task.Run(() => Build(session, process, ct), ct);
    }

    private static List<ExceptionViewModel> Build(Session session, RecorderProcess? process, CancellationToken ct)
    {
        var rows = new List<ExceptionViewModel>();

        if (process is null)
        {
            foreach (var p in session.Processes)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var ex in p.Exceptions)
                {
                    rows.Add(new ExceptionViewModel(p, ex));
                }
            }

            ct.ThrowIfCancellationRequested();
            rows.Sort((a, b) => a.Exception.Timestamp.CompareTo(b.Exception.Timestamp));
        }
        else
        {
            foreach (var ex in process.Exceptions)
            {
                ct.ThrowIfCancellationRequested();
                rows.Add(new ExceptionViewModel(process, ex));
            }
        }

        return rows;
    }
}
