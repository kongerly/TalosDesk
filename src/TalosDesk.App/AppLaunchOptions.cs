using System.IO;
using TalosDesk.Core.Configuration;

namespace TalosDesk.App;

internal sealed record AppLaunchOptions(string WorkspacePath, string? ProfileLabel)
{
    public static AppLaunchOptions Parse(IReadOnlyList<string> arguments)
    {
        string? workspacePath = null;
        string? profileLabel = null;

        for (var index = 0; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--workspace":
                    workspacePath = ReadValue(arguments, ref index, "--workspace");
                    break;
                case "--profile-label":
                    profileLabel = ReadValue(arguments, ref index, "--profile-label").Trim();
                    if (profileLabel.Length is 0 or > 40 || profileLabel.IndexOfAny(['\r', '\n']) >= 0)
                    {
                        throw new ArgumentException("--profile-label 必须是 1 到 40 个字符的单行文本。");
                    }
                    break;
                default:
                    throw new ArgumentException($"不支持的启动参数：{arguments[index]}");
            }
        }

        workspacePath ??= new WorkspaceStore().FilePath;
        try
        {
            workspacePath = Path.GetFullPath(workspacePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("--workspace 必须指向有效的工作区文件路径。", exception);
        }

        return new AppLaunchOptions(workspacePath, profileLabel);
    }

    private static string ReadValue(IReadOnlyList<string> arguments, ref int index, string option)
    {
        if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
        {
            throw new ArgumentException($"{option} 后必须提供值。");
        }

        return arguments[index];
    }
}
