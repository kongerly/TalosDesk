namespace TalosDesk.Core.Processes;

// Completion 抛错不代表归属退出；协调器和 Runner 必须同时检查归属确认。
public interface ICommandRunSession : IAsyncDisposable
{
    Task<CommandRunResult> Completion { get; }
    bool IsOwnershipReleased { get; }
    bool IsStopRequested { get; }
    Task<CommandStopResult> StopAsync(CancellationToken cancellationToken = default);
}

public sealed class CommandCreationException(ICommandRunSession ownership, Exception innerException)
    : Exception("命令创建失败，归属退出尚未确认。", innerException)
{
    public ICommandRunSession Ownership { get; } = ownership;
}
