using System.ComponentModel;
using System.IO;

namespace TalosDesk.App.Tests;

[TestClass]
public sealed class CommandStartErrorTextTests
{
    [TestMethod]
    public void KnownStartFailuresUseFixedChineseMessages()
    {
        const string workingDirectory = @"C:\Path\To\SampleProject";

        Assert.AreEqual($"运行目录不存在：{workingDirectory}",
            MainWindow.FormatCommandStartError(new DirectoryNotFoundException("Working directory does not exist"), workingDirectory));
        Assert.AreEqual("未在应用启动时继承的 PATH 中找到 PowerShell 7（pwsh.exe）。",
            MainWindow.FormatCommandStartError(new FileNotFoundException("pwsh not found"), workingDirectory));
        Assert.AreEqual($"无法访问运行目录或启动 PowerShell 7，请检查权限后重试：{workingDirectory}",
            MainWindow.FormatCommandStartError(new UnauthorizedAccessException("Access denied"), workingDirectory));
        Assert.AreEqual("无法启动 PowerShell 7（Windows 错误代码 5）。",
            MainWindow.FormatCommandStartError(new Win32Exception(5, "Access denied"), workingDirectory));
        Assert.AreEqual("命令当前无法启动，可能已在运行。",
            MainWindow.FormatCommandStartError(new InvalidOperationException("Already running"), workingDirectory));
        Assert.AreEqual("命令或运行环境无效，未能启动。",
            MainWindow.FormatCommandStartError(new ArgumentException("Invalid command"), workingDirectory));
        Assert.AreEqual("启动命令时发生文件系统错误。",
            MainWindow.FormatCommandStartError(new IOException("I/O failure"), workingDirectory));
    }
}
