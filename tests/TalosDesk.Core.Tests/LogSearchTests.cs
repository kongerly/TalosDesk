using TalosDesk.Core.Processes;

namespace TalosDesk.Core.Tests;

[TestClass]
public sealed class LogSearchTests
{
    [TestMethod]
    public void FindsChineseLiteralAndCaseInsensitiveNonOverlappingMatches()
    {
        CollectionAssert.AreEqual(new[] { 0, 3, 6 }, LogSearch.FindAll("中文 中文 中文", "中文"));
        CollectionAssert.AreEqual(new[] { 0, 4 }, LogSearch.FindAll("AaA aAa", "aa"));
        CollectionAssert.AreEqual(new[] { 0 }, LogSearch.FindAll("[a].* plain", "[a].*"));
        Assert.HasCount(0, LogSearch.FindAll("内容", ""));
        Assert.HasCount(0, LogSearch.FindAll("内容", "不存在"));
    }

    [TestMethod]
    public void FiltersDisplayedChannelsAndKeepsOffsetsForHighlighting()
    {
        const string text = "标准输出  中文\r\n诊断输出  中文\n标准输出  中文";
        var matches = LogSearch.FindAll(text, "中文", "标准输出  ");
        Assert.HasCount(2, matches);
        Assert.AreEqual(text.IndexOf("中文", StringComparison.Ordinal), matches[0]);
        Assert.AreEqual(text.LastIndexOf("中文", StringComparison.Ordinal), matches[1]);
        Assert.HasCount(1, LogSearch.FindAll(text, "中文", "诊断输出  "));
    }

    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    [DataRow("\r")]
    public void SearchesOnlyLastTenThousandDisplayedLines(string separator)
    {
        var text = "excluded" + separator + string.Join(separator, Enumerable.Repeat("保留", 10_000));
        Assert.HasCount(0, LogSearch.FindAll(text, "excluded"));
        Assert.HasCount(10_000, LogSearch.FindAll(text, "保留"));
    }

    [TestMethod]
    public void CancelsLargeSnapshotsWithoutReturningPartialMatches()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => LogSearch.FindAll(new string('a', 200_000), "a", cancellationToken: cancellation.Token));
    }
}
