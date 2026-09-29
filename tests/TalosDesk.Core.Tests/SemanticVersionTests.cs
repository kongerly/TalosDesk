using TalosDesk.Core.Updates;

namespace TalosDesk.Core.Tests;

[TestClass]
public sealed class SemanticVersionTests
{
    [TestMethod]
    public void ComparesNumericAndPreReleaseIdentifiersAccordingToSemVer()
    {
        var ordered = new[]
        {
            "0.2.0-preview.2",
            "0.2.0-preview.10",
            "0.2.0",
            "0.10.0",
            "999999999999999999999999999999.0.0"
        }.Select(Parse).ToArray();

        for (var index = 1; index < ordered.Length; index++)
            Assert.IsLessThan(0, ordered[index - 1].CompareTo(ordered[index]));
    }

    [TestMethod]
    public void AcceptsOnlyStrictSemVerAndIgnoresBuildMetadataForPrecedence()
    {
        Assert.IsTrue(SemanticVersion.TryParse("v0.2.0+build.1", true, out var first));
        Assert.IsTrue(SemanticVersion.TryParse("0.2.0+build.2", false, out var second));
        Assert.AreEqual(0, first!.CompareTo(second));
        Assert.AreEqual("0.2.0", first.Identity);

        foreach (var invalid in new[] { "V0.2.0", "vv0.2.0", "0.2", "01.2.3", "1.0.0-01", "1.0.0+", "1.0.0_1" })
            Assert.IsFalse(SemanticVersion.TryParse(invalid, true, out _), invalid);
    }

    [TestMethod]
    public void SelectsHighestReleaseForResolvedChannel()
    {
        var releases = new[]
        {
            Release("0.2.0", prerelease: false),
            Release("v0.3.0-preview.10", prerelease: true),
            Release("0.3.0-preview.2", prerelease: true),
            Release("0.4.0", prerelease: false, draft: true),
            new UpdateRelease("not-semver", false, false, "https://example.invalid/ignored")
        };

        var stable = UpdateReleaseSelector.Select(Parse("0.1.1"), ReleaseChannel.Preview, UpdateChannelPreference.StableOnly, releases);
        var preview = UpdateReleaseSelector.Select(Parse("0.1.1"), ReleaseChannel.Preview, UpdateChannelPreference.FollowCurrent, releases);

        Assert.AreEqual(UpdateSelectionStatus.UpdateAvailable, stable.Status);
        Assert.AreEqual("0.2.0", stable.Candidate!.Release.TagName);
        Assert.AreEqual("v0.3.0-preview.10", preview.Candidate!.Release.TagName);
    }

    [TestMethod]
    public void DistinguishesNoReleaseNoUpdateAndInvalidEligibleLink()
    {
        var current = Parse("0.2.0");
        Assert.AreEqual(UpdateSelectionStatus.NoRelease,
            UpdateReleaseSelector.Select(current, ReleaseChannel.Stable, UpdateChannelPreference.StableOnly,
                [Release("0.3.0-preview.1", true)]).Status);
        Assert.AreEqual(UpdateSelectionStatus.NoUpdate,
            UpdateReleaseSelector.Select(current, ReleaseChannel.Stable, UpdateChannelPreference.StableOnly,
                [Release("0.2.0", false)]).Status);
        Assert.AreEqual(UpdateSelectionStatus.InvalidRelease,
            UpdateReleaseSelector.Select(current, ReleaseChannel.Stable, UpdateChannelPreference.StableOnly,
                [new UpdateRelease("0.3.0", false, false, "https://github.com.evil.example/kongerly/TalosDesk/releases/tag/0.3.0")]).Status);
    }

    private static SemanticVersion Parse(string value)
    {
        Assert.IsTrue(SemanticVersion.TryParse(value, false, out var parsed));
        return parsed!;
    }

    private static UpdateRelease Release(string tag, bool prerelease, bool draft = false) =>
        new(tag, draft, prerelease, $"https://github.com/kongerly/TalosDesk/releases/tag/{tag}");
}
