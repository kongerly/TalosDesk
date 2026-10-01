using TalosDesk.Core.Diagnostics;

namespace TalosDesk.App.Tests;

[TestClass]
public sealed class CrashTimeDisplayTests
{
    private static readonly TimeZoneInfo TestTimeZone = TimeZoneInfo.CreateCustomTimeZone(
        "Synthetic UTC+08",
        TimeSpan.FromHours(8),
        "Synthetic UTC+08",
        "Synthetic UTC+08");

    [TestMethod]
    public void LocalFormatterConvertsInstantWithoutTechnicalOffset()
    {
        var timestamp = new DateTimeOffset(2026, 10, 1, 9, 44, 46, TimeSpan.Zero);

        var result = CrashTimestampFormatter.FormatLocal(timestamp, TestTimeZone);

        Assert.AreEqual("2026-10-01 17:44:46", result);
        Assert.DoesNotContain("+08:00", result);
    }

    [TestMethod]
    public void CrashDetailLabelsConvertedTimeAsLocal()
    {
        var timestamp = new DateTimeOffset(2026, 10, 1, 9, 44, 46, 996, TimeSpan.Zero);
        var record = new CrashRecord(
            1,
            Guid.Parse("3e72e5d5-e93c-4889-83f9-5fa820dd3828"),
            timestamp,
            "0.2.0",
            "Preview",
            "Microsoft Windows",
            ".NET 10.0.12",
            "x64",
            CrashSource.Dispatcher,
            true,
            new CrashExceptionNode(
                "System.InvalidOperationException",
                unchecked((int)0x80131509),
                [],
                [],
                false),
            false);

        var result = MainWindow.FormatCrashRecord(record, TestTimeZone);

        StringAssert.Contains(result, "时间（本地）：2026-10-01 17:44:46.996");
        Assert.DoesNotContain("时间（UTC）", result);
        Assert.DoesNotContain("+08:00", result);
    }
}
