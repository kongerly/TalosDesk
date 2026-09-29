using System.ComponentModel;
using TalosDesk.Core.Updates;

namespace TalosDesk.App.Tests;

[TestClass]
public sealed class ReleasePageNavigatorTests
{
    [TestMethod]
    public void ValidOfficialCandidateOpensOnlyWhenInvoked()
    {
        var launcher = new RecordingLauncher();
        var navigator = new ReleasePageNavigator(launcher);
        var candidate = Candidate("0.2.0", "https://github.com/kongerly/TalosDesk/releases/tag/v0.2.0", "v0.2.0");

        Assert.IsNull(launcher.Address);
        Assert.IsTrue(navigator.TryOpen(candidate, out var error));
        Assert.IsNull(error);
        Assert.AreEqual(candidate.Release.HtmlUrl, launcher.Address);
    }

    [TestMethod]
    public void InvalidAddressIsRejectedBeforeLauncherRuns()
    {
        var launcher = new RecordingLauncher();
        var navigator = new ReleasePageNavigator(launcher);

        Assert.IsFalse(navigator.TryOpen(
            Candidate("0.2.0", "https://github.com.example/kongerly/TalosDesk/releases/tag/v0.2.0", "v0.2.0"),
            out var error));

        Assert.IsNotNull(error);
        Assert.IsNull(launcher.Address);
    }

    [TestMethod]
    public void LauncherFailureReturnsFixedMessage()
    {
        var navigator = new ReleasePageNavigator(new ThrowingLauncher());

        Assert.IsFalse(navigator.TryOpen(
            Candidate("0.2.0", "https://github.com/kongerly/TalosDesk/releases/tag/v0.2.0", "v0.2.0"),
            out var error));

        Assert.AreEqual("无法打开系统默认浏览器，请稍后重试。", error);
    }

    private static UpdateCandidate Candidate(string version, string address, string tag)
    {
        Assert.IsTrue(SemanticVersion.TryParse(version, false, out var parsed));
        return new UpdateCandidate(parsed!, new UpdateRelease(tag, false, false, address));
    }

    private sealed class RecordingLauncher : IReleasePageLauncher
    {
        public string? Address { get; private set; }
        public void Open(string address) => Address = address;
    }

    private sealed class ThrowingLauncher : IReleasePageLauncher
    {
        public void Open(string address) => throw new Win32Exception("synthetic detail must not escape");
    }
}
