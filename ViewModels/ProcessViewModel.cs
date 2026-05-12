using RecorderProcess = GuiLabs.Dotnet.Recorder.Process;

namespace BeetleView.ViewModels;

/// <summary>
/// Row in the Processes list. A null <see cref="Process"/> represents the
/// synthetic "(All processes)" sentinel.
/// </summary>
public sealed class ProcessViewModel
{
    public RecorderProcess? Process { get; init; }
    public string DisplayName { get; init; } = "";
    public int Pid { get; init; }
    public int ExceptionCount { get; init; }
}
