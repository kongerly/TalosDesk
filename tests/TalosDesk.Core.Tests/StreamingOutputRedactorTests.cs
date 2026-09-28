using TalosDesk.Core.Processes;

namespace TalosDesk.Core.Tests;

[TestClass]
public sealed class StreamingOutputRedactorTests
{
    [TestMethod]
    public void MasksKnownValuesAcrossChunksLinesAndAtEndOfStream()
    {
        const string secret = "合成\n令牌-α";
        var redactor = new StreamingOutputRedactor([secret]);
        var output = redactor.Append("before 合成\n令") + redactor.Append("牌-α after\nlast 合成") +
                     redactor.Append("\n令牌-α") + redactor.Finish();

        Assert.AreEqual("before [已隐藏] after\nlast [已隐藏]", output);
        Assert.IsFalse(output.Contains(secret, StringComparison.Ordinal));
    }

    [TestMethod]
    public void MasksShortAndMaximumLengthValuesWithoutMaskingExplicitEmpty()
    {
        var longSecret = new string('测', 4096);
        var redactor = new StreamingOutputRedactor(["x", string.Empty, longSecret]);
        var output = redactor.Append("x " + longSecret[..2000]) + redactor.Append(longSecret[2000..] + " end") + redactor.Finish();

        Assert.AreEqual("[已隐藏] [已隐藏] end", output);
        Assert.IsFalse(output.Contains(longSecret, StringComparison.Ordinal));
    }

    [TestMethod]
    public void MasksTokenPatternsAcrossChunksAndOmitsOverlongCandidate()
    {
        var redactor = new StreamingOutputRedactor([]);
        var output = redactor.Append("Authorization: Bea") + redactor.Append("rer abc123\nToKeN=xy") +
                     redactor.Append("z api_") + redactor.Append("key=" + new string('q', 300)) +
                     redactor.Append("\npublic") + redactor.Finish();

        Assert.AreEqual("Authorization: Bearer [已隐藏]\nToKeN=[已隐藏] api_key=[已隐藏]\npublic", output);
        Assert.IsFalse(output.Contains("abc123", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains(new string('q', 300), StringComparison.Ordinal));
    }

    [TestMethod]
    public void TimeoutOmitsCurrentAndLaterRawText()
    {
        var redactor = new StreamingOutputRedactor(["synthetic-secret"], TimeSpan.Zero);

        Assert.AreEqual(StreamingOutputRedactor.OmittedMarker, redactor.Append("synthetic-secret"));
        Assert.AreEqual(string.Empty, redactor.Append("more synthetic-secret"));
        Assert.AreEqual(string.Empty, redactor.Finish());
    }
}
