using RecorderExceptionEvent = GuiLabs.Dotnet.Recorder.ExceptionEvent;
using RecorderProcess = GuiLabs.Dotnet.Recorder.Process;

namespace BeetleView.ViewModels;

/// <summary>
/// Row in the Exceptions list. Wraps a recorder <see cref="RecorderExceptionEvent"/>
/// together with its owning <see cref="RecorderProcess"/> so the page can
/// resolve a stack trace later without a back-pointer lookup.
/// </summary>
public sealed class ExceptionViewModel
{
    public ExceptionViewModel(RecorderProcess process, RecorderExceptionEvent exception)
    {
        Process = process;
        Exception = exception;
    }

    public RecorderProcess Process { get; }
    public RecorderExceptionEvent Exception { get; }

    public string TimestampText => Exception.Timestamp.ToString("HH:mm:ss.fff");
    public string ExceptionType => Exception.ExceptionType ?? "";
    public string ExceptionMessage => Exception.ExceptionMessage ?? "";
}
