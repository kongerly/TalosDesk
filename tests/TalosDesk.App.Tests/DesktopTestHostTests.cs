using System.Threading;
using System.Windows.Threading;

namespace TalosDesk.App.Tests;

[TestClass]
public sealed class DesktopTestHostTests
{
    [TestMethod]
    public void AssertionFailureKeepsDispatcherAvailableAndPreservesOriginalStack()
    {
        using var host = new DesktopTestHost(initializeApplication: false);
        var cleaned = false;
        var failure = Assert.Throws<AssertFailedException>(() => host.Run("合成失败", () =>
            DesktopTestHost.RunWithCleanup(ThrowSyntheticFailure, () => cleaned = true), () => "断言阶段"));
        Assert.IsTrue(cleaned);
        StringAssert.Contains(failure.Message, "合成失败");
        StringAssert.Contains(failure.Message, "断言阶段");
        StringAssert.Contains(failure.Message, "耗时");
        StringAssert.Contains(failure.InnerException!.StackTrace!, nameof(ThrowSyntheticFailure));
        host.Run("失败后继续", () =>
        {
            Assert.AreEqual(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            Assert.IsInstanceOfType<DispatcherSynchronizationContext>(SynchronizationContext.Current);
            Assert.IsFalse(Dispatcher.CurrentDispatcher.HasShutdownStarted);
        }, () => "继续执行");
    }

    [TestMethod]
    public void LocalFrameCompletionAllowsAnotherScenarioOnSameDispatcher()
    {
        using var host = new DesktopTestHost(initializeApplication: false);
        Dispatcher? first = null;
        host.Run("局部消息循环", () =>
        {
            first = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            first.BeginInvoke(new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }, () => "局部循环完成");
        host.Run("后续场景", () =>
        {
            Assert.AreSame(first, Dispatcher.CurrentDispatcher);
            Assert.IsFalse(first!.HasShutdownStarted);
        }, () => "继续执行");
    }

    [TestMethod]
    public void TimeoutRejectsLaterCallsEvenAfterOriginalScenarioFinishes()
    {
        using var host = new DesktopTestHost(initializeApplication: false);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var invocation = Task.Run(() => Assert.Throws<AssertFailedException>(() => host.Run("合成挂起", () =>
        {
            started.Set();
            try { release.Wait(); }
            finally { finished.Set(); }
        }, () => "等待释放", TimeSpan.FromSeconds(1))));
        try
        {
            Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
            var failure = invocation.GetAwaiter().GetResult();
            StringAssert.Contains(failure.Message, "超时");
        }
        finally
        {
            release.Set();
            Assert.IsTrue(finished.Wait(TimeSpan.FromSeconds(5)));
        }
        var ran = false;
        var rejected = Assert.Throws<AssertFailedException>(() => host.Run("禁止投递", () => ran = true, () => "未启动"));
        StringAssert.Contains(rejected.Message, "宿主不可用");
        StringAssert.Contains(rejected.Message, "合成挂起");
        Assert.IsFalse(ran);
    }

    [TestMethod]
    public void CleanupFailurePreservesBothFailuresAndRejectsLaterCalls()
    {
        using var host = new DesktopTestHost(initializeApplication: false);
        var failure = Assert.Throws<AssertFailedException>(() => host.Run("清理失败", () =>
            DesktopTestHost.RunWithCleanup(ThrowSyntheticFailure, () => throw new InvalidOperationException("合成清理失败")),
            () => "清理"));
        Assert.IsInstanceOfType<AggregateException>(failure.InnerException);
        Assert.HasCount(2, ((AggregateException)failure.InnerException!).InnerExceptions);
        var ran = false;
        var rejected = Assert.Throws<AssertFailedException>(() => host.Run("禁止投递", () => ran = true, () => "未启动"));
        StringAssert.Contains(rejected.Message, "清理失败");
        Assert.IsFalse(ran);
    }

    private static void ThrowSyntheticFailure() => Assert.Fail("合成断言失败");
}
