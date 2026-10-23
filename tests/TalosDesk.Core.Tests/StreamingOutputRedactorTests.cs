using System.Text;
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
    [DataRow("token=", " ")]
    [DataRow("token=", "\n")]
    [DataRow("token=", "\r\n")]
    [DataRow("token=", "\t")]
    [DataRow("Bearer ", " ")]
    [DataRow("Bearer ", "\n")]
    [DataRow("Bearer ", "\r\n")]
    [DataRow("Bearer ", "\t")]
    [DataRow("api_key=", " ")]
    [DataRow("api_key=", "\n")]
    [DataRow("api_key=", "\r\n")]
    [DataRow("api_key=", "\t")]
    public void TokenSuppressionDoesNotExposeMultilineKnownValueSuffix(string prefix, string separator)
    {
        var secret = "synthetic-first" + separator + "synthetic-second";
        foreach (var leadingValue in new[] { string.Empty, "other-" })
        {
            var input = prefix + leadingValue + secret + " public";
            for (var split = 0; split <= input.Length; split++)
            {
                var redactor = new StreamingOutputRedactor([secret]);
                var output = redactor.Append(input[..split]) + redactor.Append(input[split..]) + redactor.Finish();
                Assert.AreEqual(prefix + "[已隐藏] public", output, $"split={split}, leading={leadingValue}");
            }
        }
    }

    [TestMethod]
    [DataRow("shared")]
    [DataRow("共通")]
    [DataRow(" ")]
    [DataRow("\n")]
    [DataRow("\r\n")]
    public void MasksTheEntireUnionOfOverlappingKnownValues(string overlap)
    {
        var firstSecret = "synthetic-left" + overlap;
        var secondSecret = overlap + "synthetic-right";
        foreach (var prefix in new[] { string.Empty, "token=", "Bearer ", "api_key=" })
        {
            var input = "before:" + prefix + "synthetic-left" + overlap + "synthetic-right public";
            for (var split = 0; split <= input.Length; split++)
            {
                var redactor = new StreamingOutputRedactor([firstSecret, secondSecret]);
                var output = redactor.Append(input[..split]) + redactor.Append(input[split..]) + redactor.Finish();

                Assert.AreEqual("before:" + prefix + "[已隐藏] public", output, $"split={split}, prefix={prefix}");
            }
        }
    }

    [TestMethod]
    public void MasksOverlappingChainsAcrossInternalBlocksWithoutBufferingTheWholeChain()
    {
        var input = string.Concat(Enumerable.Repeat("ab", 10_000));
        foreach (var chunkLength in new[] { 1, 4095, 16_384, input.Length })
        foreach (var suffix in new[] { string.Empty, " public" })
        {
            var redactor = new StreamingOutputRedactor(["aba", "bab"], TimeSpan.FromSeconds(5));
            var output = new StringBuilder();
            for (var offset = 0; offset < input.Length; offset += chunkLength)
                output.Append(redactor.Append(input.Substring(offset, Math.Min(chunkLength, input.Length - offset))));
            output.Append(redactor.Append(suffix)).Append(redactor.Finish());

            Assert.AreEqual("[已隐藏]" + suffix, output.ToString(), $"chunkLength={chunkLength}, suffix={suffix}");
        }
    }

    [TestMethod]
    [DataRow("token=", "token", "[已隐藏]=")]
    [DataRow("token=", "ken", "to[已隐藏]=")]
    [DataRow("api_key=", "key", "api_[已隐藏]=")]
    [DataRow("api_key=", "api_key=", "[已隐藏]")]
    [DataRow("Bearer ", "arer", "Be[已隐藏] ")]
    [DataRow("Bearer ", "Bearer ", "[已隐藏]")]
    [DataRow("ToKeN=", "Ke", "To[已隐藏]N=")]
    public void KnownValuesInsidePatternPrefixesDoNotExposeEitherValue(string prefix, string secret, string redactedPrefix)
    {
        AssertAtEverySplit(prefix + "synthetic-pattern-value public", [secret], redactedPrefix + "[已隐藏] public");
    }

    [TestMethod]
    [DataRow("token=")]
    [DataRow("api_key=")]
    [DataRow("Bearer ")]
    public void PatternStartingInsideKnownValueStillMasksTheRemainingToken(string prefix)
    {
        var secret = "synthetic-known-" + prefix + "synthetic-";
        AssertAtEverySplit(secret + "pattern-tail public", [secret], "[已隐藏] public");
    }

    [TestMethod]
    [DataRow("token=")]
    [DataRow("api_key=")]
    [DataRow("Bearer ")]
    public void KnownValueStartingInsidePatternPrefixStillMasksItsMultilineSuffix(string prefix)
    {
        var secret = prefix[2..] + "synthetic-first\nsynthetic-last";
        AssertAtEverySplit(prefix[..2] + secret + " public", [secret], prefix[..2] + "[已隐藏] public");
    }

    [TestMethod]
    [DataRow("token=")]
    [DataRow("api_key=")]
    public void BearerPatternInsideSuppressedTokenStillMasksItsFollowingValue(string prefix)
    {
        AssertAtEverySplit(prefix + "Bearer synthetic-pattern-value public", [], prefix + "[已隐藏] [已隐藏] public");
    }

    [TestMethod]
    public void StreamingMasksMatchIndependentWholeTextCoverageForMixedOverlaps()
    {
        var random = new Random(0x7B2E);
        string[] atoms = ["token=", "ToKeN=", "api_key=", "Bearer ", "abc", "xyz", " ", "\n"];
        string[] prefixes = ["token=", "api_key=", "Bearer "];
        for (var sample = 0; sample < 100; sample++)
        {
            var text = string.Concat(Enumerable.Range(0, 8).Select(_ => atoms[random.Next(atoms.Length)])) + " public";
            var secrets = Enumerable.Range(0, 3).Select(_ =>
            {
                var start = random.Next(text.Length - 7);
                return text.Substring(start, random.Next(1, Math.Min(12, text.Length - 7 - start) + 1));
            }).ToArray();
            // 用完整文本独立标记所有已知值及模式值的覆盖区间，不复用流式状态或跳过命中。
            var masked = new bool[text.Length];
            for (var offset = 0; offset < text.Length; offset++)
            {
                foreach (var secret in secrets)
                    if (text.AsSpan(offset).StartsWith(secret, StringComparison.Ordinal))
                        Array.Fill(masked, true, offset, secret.Length);
                foreach (var prefix in prefixes)
                {
                    if (!text.AsSpan(offset).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    var start = offset + prefix.Length;
                    var end = start;
                    while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
                    Array.Fill(masked, true, start, end - start);
                }
            }
            var expected = new StringBuilder();
            for (var offset = 0; offset < text.Length; offset++)
            {
                if (!masked[offset]) expected.Append(text[offset]);
                else if (offset == 0 || !masked[offset - 1]) expected.Append(StreamingOutputRedactor.HiddenMarker);
            }
            foreach (var chunkLength in new[] { 1, text.Length })
            {
                var redactor = new StreamingOutputRedactor(secrets);
                var output = new StringBuilder();
                for (var offset = 0; offset < text.Length; offset += chunkLength)
                    output.Append(redactor.Append(text.Substring(offset, Math.Min(chunkLength, text.Length - offset))));
                output.Append(redactor.Finish());
                var actual = output.ToString();
                var adjacentMarkers = StreamingOutputRedactor.HiddenMarker + StreamingOutputRedactor.HiddenMarker;
                while (actual.Contains(adjacentMarkers, StringComparison.Ordinal))
                    actual = actual.Replace(adjacentMarkers, StreamingOutputRedactor.HiddenMarker, StringComparison.Ordinal);
                Assert.AreEqual(expected.ToString(), actual, $"sample={sample}, chunkLength={chunkLength}");
            }
        }
    }

    [TestMethod]
    public void TimeoutOmitsCurrentAndLaterRawText()
    {
        var redactor = new StreamingOutputRedactor(["synthetic-secret"], TimeSpan.Zero);

        Assert.AreEqual(StreamingOutputRedactor.OmittedMarker, redactor.Append("synthetic-secret"));
        Assert.AreEqual(string.Empty, redactor.Append("more synthetic-secret"));
        Assert.AreEqual(string.Empty, redactor.Finish());
    }

    private static void AssertAtEverySplit(string input, string[] secrets, string expected)
    {
        for (var split = 0; split <= input.Length; split++)
        {
            var redactor = new StreamingOutputRedactor(secrets);
            var output = redactor.Append(input[..split]) + redactor.Append(input[split..]) + redactor.Finish();
            Assert.AreEqual(expected, output, $"split={split}");
        }
        var incremental = new StreamingOutputRedactor(secrets);
        var incrementalOutput = new StringBuilder();
        foreach (var character in input) incrementalOutput.Append(incremental.Append(character.ToString()));
        incrementalOutput.Append(incremental.Finish());
        Assert.AreEqual(expected, incrementalOutput.ToString(), "single-character chunks");
    }
}
