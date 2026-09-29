using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using TalosDesk.Core.Updates;

namespace TalosDesk.App;

internal interface IReleasePageLauncher
{
    void Open(string address);
}

internal sealed class SystemReleasePageLauncher : IReleasePageLauncher
{
    public void Open(string address) => Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
}

internal sealed class ReleasePageNavigator(IReleasePageLauncher launcher)
{
    public bool TryOpen(UpdateCandidate? candidate, out string? error)
    {
        if (candidate is null ||
            !SemanticVersion.TryParse(candidate.Release.TagName, true, out var tagVersion) ||
            tagVersion!.CompareTo(candidate.Version) != 0 ||
            !ReleaseUriValidator.IsValid(candidate.Release.HtmlUrl, candidate.Release.TagName))
        {
            error = "发布页面地址无效，未打开浏览器。";
            return false;
        }

        try
        {
            launcher.Open(candidate.Release.HtmlUrl);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException or IOException)
        {
            error = "无法打开系统默认浏览器，请稍后重试。";
            return false;
        }
    }
}
