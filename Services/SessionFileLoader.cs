using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeetleView.ViewModels;
using GuiLabs.Dotnet.Recorder;
using RecorderProcess = GuiLabs.Dotnet.Recorder.Process;

namespace BeetleView.Services;

/// <summary>
/// Result of loading a .beetle file: the parsed <see cref="Session"/>, the
/// root nodes for the Processes <c>TreeView</c> (a single synthetic
/// "(All processes)" sentinel that owns every other process), and a
/// human-readable summary line for the session-info banner.
/// </summary>
public sealed record LoadedSession(
    Session Session,
    IReadOnlyList<ProcessViewModel> ProcessRoots,
    string SummaryText);

/// <summary>
/// Loads a .beetle file off the UI thread and projects it into a parent/child
/// tree of <see cref="ProcessViewModel"/>. Parent/child relationships come
/// from <see cref="RecorderProcess.ParentId"/> plus
/// <see cref="RecorderProcess.ParentStartTimeRelativeMSec"/> — the latter
/// disambiguates PID reuse during the session's lifetime.
/// </summary>
internal static class SessionFileLoader
{
    // Tolerance when matching a child's recorded parent start time against a
    // candidate parent's actual start time. The recorder emits both in the
    // same units (milliseconds relative to session start), so a tight window
    // is fine; we use 1ms to absorb floating-point round-trip noise.
    private const double ParentStartMatchToleranceMSec = 1.0;

    public static Task<LoadedSession> LoadAsync(string path) => Task.Run(() => Load(path));

    private static LoadedSession Load(string path)
    {
        var fileInfo = new FileInfo(path);
        var session = SessionSerializer.Load(path);
        int totalExceptions = session.Processes.Sum(p => p.Exceptions.Count);

        var roots = BuildTree(session);

        var sentinel = new ProcessViewModel
        {
            Process = null,
            DisplayName = "(All processes)",
            Pid = 0,
            ExceptionCount = totalExceptions,
            PidPrefix = "",
            Children = roots,
            IsExpanded = true,
        };

        string summary =
            $"Start: {session.StartTime:yyyy-MM-dd HH:mm:ss} UTC   •   " +
            $"Duration: {session.Duration:hh\\:mm\\:ss}   •   " +
            $"Processes: {session.Processes.Count}   •   " +
            $"Exceptions: {totalExceptions:N0}   •   " +
            $"Events lost: {session.EventsLost}   •   " +
            $"File: {fileInfo.Length / (1024.0 * 1024.0):N1} MB";

        return new LoadedSession(session, new[] { sentinel }, summary);
    }

    private static IReadOnlyList<ProcessViewModel> BuildTree(Session session)
    {
        // PIDs are reused across a single session, so the lookup must be by
        // group, not by single value.
        var byPid = session.Processes
            .GroupBy(p => p.Id)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Resolve each process's real parent (or null = orphan/root).
        var parentOf = new Dictionary<RecorderProcess, RecorderProcess?>(session.Processes.Count);
        foreach (var p in session.Processes)
        {
            parentOf[p] = ResolveParent(p, byPid);
        }

        // Group children by their resolved parent.
        var childrenOf = session.Processes
            .Where(p => parentOf[p] is not null)
            .GroupBy(p => parentOf[p]!)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(p => p.StartTimeRelativeMSec).ToList());

        var rootProcesses = session.Processes
            .Where(p => parentOf[p] is null)
            .OrderBy(p => p.StartTimeRelativeMSec)
            .ToList();

        // DFS-build the VM tree, guarding against cycles (defensive — a
        // well-formed session shouldn't produce them, but PID reuse + ParentId
        // edge cases mean it's cheap insurance).
        var visited = new HashSet<RecorderProcess>();
        return rootProcesses.Select(r => BuildNode(r, childrenOf, visited)).ToList();
    }

    private static RecorderProcess? ResolveParent(
        RecorderProcess child,
        Dictionary<int, List<RecorderProcess>> byPid)
    {
        if (!byPid.TryGetValue(child.ParentId, out var candidates))
        {
            // Parent isn't in this session (started before recording began,
            // or it's a system process like PID 0/4 we didn't capture).
            return null;
        }

        // Prefer the candidate whose actual start time matches the child's
        // recorded parent-start-time — this is how we tell a real parent from
        // a PID-reused unrelated process.
        var match = candidates.FirstOrDefault(c =>
            Math.Abs(c.StartTimeRelativeMSec - child.ParentStartTimeRelativeMSec) < ParentStartMatchToleranceMSec);

        if (match is null)
        {
            // No exact start-time match. If there's only one candidate, accept
            // it (the recorder may not have captured the parent's start);
            // otherwise treat as orphan rather than guess.
            match = candidates.Count == 1 ? candidates[0] : null;
        }

        return match == child ? null : match;
    }

    private static ProcessViewModel BuildNode(
        RecorderProcess p,
        Dictionary<RecorderProcess, List<RecorderProcess>> childrenOf,
        HashSet<RecorderProcess> visited)
    {
        if (!visited.Add(p))
        {
            // Cycle: emit this node as a leaf to break the loop.
            return MakeVm(p, Array.Empty<ProcessViewModel>());
        }

        IReadOnlyList<ProcessViewModel> kids = childrenOf.TryGetValue(p, out var list)
            ? list.Select(c => BuildNode(c, childrenOf, visited)).ToList()
            : Array.Empty<ProcessViewModel>();

        visited.Remove(p);
        return MakeVm(p, kids);
    }

    private static ProcessViewModel MakeVm(RecorderProcess p, IReadOnlyList<ProcessViewModel> kids) =>
        new()
        {
            Process = p,
            DisplayName = string.IsNullOrEmpty(p.ImageFileName) ? $"(pid {p.Id})" : p.ImageFileName,
            Pid = p.Id,
            ExceptionCount = p.Exceptions.Count,
            PidPrefix = $"pid={p.Id}  •  ",
            Children = kids,
            IsExpanded = true,
        };
}
