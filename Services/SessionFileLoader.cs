using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeetleView.ViewModels;
using GuiLabs.Dotnet.Recorder;

namespace BeetleView.Services;

/// <summary>
/// Result of loading a .beetle file: the parsed <see cref="Session"/>,
/// pre-built process view models (with the "(All processes)" sentinel first),
/// and a human-readable summary line for the session-info banner.
/// </summary>
public sealed record LoadedSession(
    Session Session,
    IReadOnlyList<ProcessViewModel> Processes,
    string SummaryText);

/// <summary>
/// Loads a .beetle file off the UI thread and projects it into the shape the
/// page wants to render. Returning view models here is a deliberate pragmatic
/// choice for this small app — it keeps the page's <c>LoadFileAsync</c>
/// declarative.
/// </summary>
internal static class SessionFileLoader
{
    public static Task<LoadedSession> LoadAsync(string path) => Task.Run(() => Load(path));

    private static LoadedSession Load(string path)
    {
        var fileInfo = new FileInfo(path);
        var session = SessionSerializer.Load(path);

        int totalExceptions = session.Processes.Sum(p => p.Exceptions.Count);

        var processes = new List<ProcessViewModel>(session.Processes.Count + 1)
        {
            new ProcessViewModel
            {
                Process = null,
                DisplayName = "(All processes)",
                Pid = 0,
                ExceptionCount = totalExceptions,
            },
        };

        foreach (var p in session.Processes
                     .OrderByDescending(p => p.Exceptions.Count)
                     .ThenBy(p => p.ImageFileName))
        {
            processes.Add(new ProcessViewModel
            {
                Process = p,
                DisplayName = string.IsNullOrEmpty(p.ImageFileName) ? $"(pid {p.Id})" : p.ImageFileName,
                Pid = p.Id,
                ExceptionCount = p.Exceptions.Count,
            });
        }

        string summary =
            $"Start: {session.StartTime:yyyy-MM-dd HH:mm:ss} UTC   •   " +
            $"Duration: {session.Duration:hh\\:mm\\:ss}   •   " +
            $"Processes: {session.Processes.Count}   •   " +
            $"Exceptions: {totalExceptions:N0}   •   " +
            $"Events lost: {session.EventsLost}   •   " +
            $"File: {fileInfo.Length / (1024.0 * 1024.0):N1} MB";

        return new LoadedSession(session, processes, summary);
    }
}
