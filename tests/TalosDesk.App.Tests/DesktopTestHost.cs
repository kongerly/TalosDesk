using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace TalosDesk.App.Tests;

internal sealed class DesktopTestHost : IDisposable
{
    [ThreadStatic] private static DesktopTestHost? _current;
    private readonly Thread _thread;
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _unavailable;
    private App? _application;
    private bool _disposed;

    // 宿主自身的回归只需要 Dispatcher，不能再创建第二个 WPF Application。
    internal DesktopTestHost(bool initializeApplication = true)
    {
        _thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                if (initializeApplication)
                {
                    _application = new App(launchWorkspace: false) { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    _application.InitializeComponent();
                }
                _ready.TrySetResult(dispatcher);
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                MarkUnavailable($"STA 宿主异常退出：{exception}");
                _ready.TrySetException(exception);
            }
            finally { MarkUnavailable("STA 宿主已退出。"); }
        }) { IsBackground = true, Name = "TalosDesk 桌面回归" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
    }

    internal void Run(string name, Action scenario, Func<string> stage, TimeSpan? timeout = null)
    {
        var clock = Stopwatch.StartNew();
        if (Volatile.Read(ref _unavailable) is { } reason)
            throw Failure($"宿主不可用：{reason}");

        var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = _ready.Task.GetAwaiter().GetResult();
        var operation = dispatcher.BeginInvoke(new Action(() =>
        {
            Exception? failure = null;
            Exception? cleanupFailure = null;
            var previousContext = SynchronizationContext.Current;
            _current = this;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                scenario();
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                try
                {
                    // 清理失败不从 finally 抛出：先收集，再连同正文失败一起交给 completed。
                    // RunWithCleanup 已自行聚合并按原堆栈重抛，这里不再二次包装。
                    if (_application is not null && _application.Windows.Count != 0)
                        cleanupFailure = new InvalidOperationException($"回归结束后仍有 {_application.Windows.Count} 个窗口未关闭。");
                    if (dispatcher.HasShutdownStarted)
                        cleanupFailure = Combine(cleanupFailure, new InvalidOperationException("回归关闭了共享 Dispatcher。"));
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                    _current = null;
                }
                // 只有清理失败才判定宿主不可用；普通断言失败后宿主仍可执行下一套。
                if (cleanupFailure is not null) MarkUnavailable($"{name} 清理失败：{cleanupFailure}");
                completed.TrySetResult(failure ?? cleanupFailure);
            }
        }));

        Exception? result;
        try { result = completed.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(120)).GetAwaiter().GetResult(); }
        catch (TimeoutException exception)
        {
            MarkUnavailable($"{name} 超时；阶段：{stage()}。");
            operation.Abort(); // 仅撤销尚未开始的调用；不强制终止运行中的 STA 线程。
            throw Failure("等待回归完成超时。", exception);
        }
        if (result is not null) throw Failure("桌面回归失败。", result);

        AssertFailedException Failure(string message, Exception? exception = null)
        {
            var detail = $"{message} 用例：{name}；阶段：{stage()}；耗时：{clock.Elapsed.TotalSeconds:F2} 秒。";
            return exception is null ? new AssertFailedException(detail) : new AssertFailedException($"{detail}\n{exception}", exception);
        }
    }

    // 保留正文和清理两处失败；清理失败后禁止向此宿主投递下一套回归。
    internal static void RunWithCleanup(Action scenario, Action cleanup)
    {
        Exception? failure = null;
        try { scenario(); }
        catch (Exception exception) { failure = exception; }
        try { cleanup(); }
        catch (Exception exception)
        {
            _current?.MarkUnavailable($"回归清理失败：{exception}");
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void MarkUnavailable(string reason) => Interlocked.CompareExchange(ref _unavailable, reason, null);

    // 合并正文与清理两处失败；保留已有失败时按聚合异常附加，不再从 finally 抛出。
    private static Exception? Combine(Exception? existing, Exception additional) =>
        existing is null ? additional : new AggregateException(existing, additional);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        MarkUnavailable("STA 宿主已释放。");
        var dispatcher = _ready.Task.GetAwaiter().GetResult();
        if (!dispatcher.HasShutdownStarted)
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (_application is not null) _application.Shutdown();
                // 消息循环由宿主的 Dispatcher.Run 启动，Application.Shutdown 不会代为退出。
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }));
        Assert.IsTrue(_thread.Join(TimeSpan.FromSeconds(10)),
            $"STA 宿主未能完成关闭；{Volatile.Read(ref _unavailable)}");
    }
}
