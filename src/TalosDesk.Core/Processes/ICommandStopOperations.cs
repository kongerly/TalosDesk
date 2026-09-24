namespace TalosDesk.Core.Processes;

internal interface ICommandStopOperations
{
    bool TrySendCtrlC(int processId);
    Task<bool> WaitForCommandGroupExitAsync(SafeJobHandle job, TimeSpan timeout, CancellationToken cancellationToken);
    void TerminateJob(SafeJobHandle job);
}

internal sealed class WindowsCommandStopOperations : ICommandStopOperations
{
    public static WindowsCommandStopOperations Instance { get; } = new();

    private WindowsCommandStopOperations() { }

    public bool TrySendCtrlC(int processId) => WindowsConsole.TrySendCtrlC(processId);

    public Task<bool> WaitForCommandGroupExitAsync(SafeJobHandle job, TimeSpan timeout, CancellationToken cancellationToken) =>
        WindowsNative.WaitForCommandGroupExitAsync(job, timeout, cancellationToken);

    public void TerminateJob(SafeJobHandle job) => WindowsNative.TerminateJob(job);
}
