namespace TalosDesk.Core.Processes;

internal interface ICommandObservationOperations
{
    Task WaitForProcessAsync(SafeProcessHandle process);
    Task WaitForGroupAsync(SafeJobHandle job);
    int GetExitCode(SafeProcessHandle process);
}

internal sealed class WindowsCommandObservationOperations : ICommandObservationOperations
{
    internal static WindowsCommandObservationOperations Instance { get; } = new();
    public async Task WaitForProcessAsync(SafeProcessHandle process) =>
        await WindowsNative.WaitForProcessAsync(process, Timeout.InfiniteTimeSpan, CancellationToken.None).ConfigureAwait(false);
    public async Task WaitForGroupAsync(SafeJobHandle job) =>
        await WindowsNative.WaitForCommandGroupExitAsync(job, Timeout.InfiniteTimeSpan, CancellationToken.None).ConfigureAwait(false);
    public int GetExitCode(SafeProcessHandle process) => WindowsNative.GetExitCode(process);
}
