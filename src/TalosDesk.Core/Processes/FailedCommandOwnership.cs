namespace TalosDesk.Core.Processes;

// 仅持有本次 CreateProcess 返回的句柄；包括尚未成功加入 Job 的挂起进程。
internal sealed class FailedCommandOwnership(SafeJobHandle job, SafeProcessHandle process, bool assigned) : ICommandRunSession
{
    private readonly SemaphoreSlim _stopLock = new(1, 1);
    private int _released;
    public Task<CommandRunResult> Completion { get; } = Task.FromException<CommandRunResult>(new InvalidOperationException("命令创建未完成。"));
    public bool IsOwnershipReleased => Volatile.Read(ref _released) != 0;
    public bool IsStopRequested => true;

    public async Task<CommandStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        await _stopLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsOwnershipReleased) return CommandStopResult.AlreadyExited;
            var exited = assigned
                ? WindowsNative.GetActiveProcessCount(job) == 0
                : await WindowsNative.WaitForProcessAsync(process, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            if (!exited)
            {
                if (assigned) WindowsNative.TerminateJob(job);
                else WindowsNative.TerminateCreatedProcess(process);
                exited = assigned
                    ? await WindowsNative.WaitForCommandGroupExitAsync(job, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false)
                    : await WindowsNative.WaitForProcessAsync(process, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            if (exited && assigned)
                exited = await WindowsNative.WaitForProcessAsync(process, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (!exited) throw new TimeoutException("无法确认创建失败的命令已退出。");
            Interlocked.Exchange(ref _released, 1);
            process.Dispose();
            job.Dispose();
            return CommandStopResult.ForceTerminated;
        }
        finally { _stopLock.Release(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
