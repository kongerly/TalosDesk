using System.Windows;
using TalosDesk.Core.Processes;

namespace TalosDesk.App;

internal interface IMainWindowExitInteraction
{
    bool ConfirmExit(Window owner, int commandCount, int sequenceCount);
    Task StopAsync(CommandRunSession session);
    void ReportFailure(Window owner, Exception exception);
}

internal sealed class MainWindowExitInteraction : IMainWindowExitInteraction
{
    public bool ConfirmExit(Window owner, int commandCount, int sequenceCount) =>
        MessageBox.Show(owner,
            sequenceCount > 0
                ? $"还有 {commandCount} 条命令正在运行，{sequenceCount} 个顺序分组尚未完成。取消后续步骤、停止运行中的命令并退出吗？"
                : $"还有 {commandCount} 条命令正在运行。停止这些命令并退出吗？",
            "命令仍在运行", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public Task StopAsync(CommandRunSession session) => session.StopAsync();

    public void ReportFailure(Window owner, Exception exception) =>
        MessageBox.Show(owner, $"TalosDesk 无法确认所有命令均已停止，因此窗口保持打开。\n\n{exception.Message}",
            "仍有命令未停止", MessageBoxButton.OK, MessageBoxImage.Error);
}
