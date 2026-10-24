using System.Text;
using TalosDesk.Core.Processes;

namespace TalosDesk.Core.Tests;

[TestClass]
public sealed class CommandOutputPumpTests
{
    [TestMethod]
    [DataRow("stdout", 16_384, "\n")]
    [DataRow("stderr", 16_384, "\n")]
    [DataRow("stdout", 32_768, "\n")]
    [DataRow("stdout", 16_383, "\r\n")]
    [DataRow("stderr", 16_383, "\r\n")]
    [DataRow("stderr", 16_384, "\r\n")]
    [DataRow("stderr", 32_767, "\r\n")]
    [DataRow("stdout", 1, "\n")]
    [DataRow("stderr", 1, "\r\n")]
    [DataRow("stdout", 16_382, "\r\n")]
    public async Task LongLineTerminatorsDoNotAddEmptyRecordsButKeepRealBlankLines(string stream, int length, string terminator)
    {
        var output = new List<(string Text, string Stream)>();
        var content = new string('x', length) + terminator + terminator + "tail" + terminator;
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(content));

        await CommandRunSession.PumpAsync(new StreamReader(input), stream, [],
            (text, channel) => output.Add((text, channel))).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, output.Count(line => line.Text.Length == 0), "只保留原始文本中的一个空行，分段行尾不能多记空行。");
        Assert.AreEqual(("", stream), output[^2]);
        Assert.AreEqual(("tail", stream), output[^1]);
        var segments = output.Take(output.Count - 2).ToArray();
        Assert.AreEqual(length, segments.Sum(line => line.Text.Length));
        foreach (var segment in segments)
        {
            Assert.AreEqual(stream, segment.Stream);
            Assert.IsLessThanOrEqualTo(16_384, segment.Text.Length);
            Assert.IsTrue(segment.Text.All(character => character == 'x'), "CRLF中的回车不能残留在长行分段里。");
        }
    }

    [TestMethod]
    [DataRow("stdout")]
    [DataRow("stderr")]
    public async Task TrailingCarriageReturnWithoutLineFeedIsPreservedAtEnd(string stream)
    {
        var content = new string('x', 16_383) + '\r';
        var output = new List<(string Text, string Stream)>();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(content));

        await CommandRunSession.PumpAsync(new StreamReader(input), stream, [],
            (text, channel) => output.Add((text, channel))).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(content, string.Concat(output.Select(line => line.Text)), "单独的末尾回车不能当作CRLF丢弃。");
        Assert.IsTrue(output.All(line => line.Stream == stream && line.Text.Length <= 16_384));
    }

    [TestMethod]
    [DataRow("stdout", 16_383, false)]
    [DataRow("stderr", 16_383, false)]
    [DataRow("stdout", 16_383, true)]
    [DataRow("stderr", 16_383, true)]
    [DataRow("stdout", 16_382, false)]
    [DataRow("stderr", 16_384, false)]
    public async Task LongOutputKeepsSupplementaryCharactersWholeInEventsAndHistory(string stream, int prefixLength, bool redact)
    {
        const string secret = "synthetic-unicode-secret";
        var visiblePrefix = new string('x', prefixLength - (redact ? StreamingOutputRedactor.HiddenMarker.Length : 0));
        var content = (redact ? secret : string.Empty) + visiblePrefix + "😀尾部𠮷";
        var expected = (redact ? StreamingOutputRedactor.HiddenMarker : string.Empty) + visiblePrefix + "😀尾部𠮷";
        var directory = Path.Combine(Path.GetTempPath(), $"TalosDesk-pump-unicode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new RunLogStore(Path.Combine(directory, "workspace.json"));
            store.Initialize();
            var writer = store.Begin(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.Now);
            var output = new List<(string Text, string Stream)>();
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(content));
            try
            {
                await CommandRunSession.PumpAsync(new StreamReader(input), stream, redact ? [secret] : [], (text, channel) =>
                {
                    output.Add((text, channel));
                    store.Append(writer, new CommandOutput(DateTimeOffset.Now, channel, text));
                }).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { store.Complete(writer, new CommandRunResult(CommandRunState.Succeeded, 0, false)); }

            Assert.AreEqual(expected, string.Concat(output.Select(line => line.Text)), "输出记录不得丢失或改变字符。");
            Assert.IsFalse(writer.Info.WriteFailed);
            var reopened = new RunLogStore(Path.Combine(directory, "workspace.json"));
            reopened.Initialize();
            var history = reopened.GetRuns(writer.Info.ProjectId, writer.Info.CommandId).Single();
            var persisted = string.Concat(reopened.ReadTail(history, stream));
            Assert.DoesNotContain('\uFFFD', persisted, "有效的UTF-8输入不应在落盘时变成替代字符。");
            Assert.AreEqual(expected, persisted, "磁盘重开后仍须保留完整字符。");
            Assert.HasCount(0, reopened.ReadTail(history, stream == "stdout" ? "stderr" : "stdout"));
            var strictUtf8 = new UTF8Encoding(false, true);
            foreach (var line in output)
            {
                Assert.AreEqual(stream, line.Stream);
                Assert.IsLessThanOrEqualTo(16_384, line.Text.Length, "长行分段仍需保持长度上限。");
                _ = strictUtf8.GetBytes(line.Text); // 每条记录须可单独编码，不能把代理对分到相邻记录。
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow("stdout")]
    [DataRow("stderr")]
    public async Task RedactionFailureIsPublishedBeforeStreamEnds(string stream)
    {
        const string secret = "synthetic-pump-secret";
        using var input = new HeldOpenStream(Encoding.UTF8.GetBytes(secret));
        var output = new List<(string Text, string Stream)>();
        var pump = CommandRunSession.PumpAsync(new StreamReader(input), stream, [secret],
            (text, channel) => output.Add((text, channel)), TimeSpan.Zero);
        try
        {
            await input.EndReadRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(pump.IsCompleted, "输入仍保持打开，不能靠EOF触发提示。");
            Assert.HasCount(1, output, "脱敏失败后应在继续等待输入之前发布固定提示。");
            Assert.AreEqual((StreamingOutputRedactor.OmittedMarker, stream), output[0]);
            Assert.IsFalse(output[0].Text.Contains(secret, StringComparison.Ordinal));
        }
        finally
        {
            input.AllowEnd();
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.HasCount(1, output, "EOF不能重复发布同一失败提示。");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(16_383)]
    public async Task ReadFailureKeepsOmissionMarkerWholeAfterPartialLine(int prefixLength)
    {
        using var input = new FailingAfterContentStream(Encoding.UTF8.GetBytes(new string('x', prefixLength)));
        var output = new List<(string Text, string Stream)>();

        await CommandRunSession.PumpAsync(new StreamReader(input), "stderr", [],
            (text, stream) => output.Add((text, stream))).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.HasCount(prefixLength == 0 ? 1 : 2, output);
        Assert.AreEqual((StreamingOutputRedactor.OmittedMarker, "stderr"), output[^1]);
        if (prefixLength > 0)
        {
            Assert.AreEqual("stderr", output[0].Stream);
            Assert.AreEqual(prefixLength, output[0].Text.Length);
            Assert.IsTrue(output[0].Text.All(character => character == 'x'), "已脱敏半行应完整保留。");
        }
        Assert.IsFalse(output.Any(line => line.Text.Contains("synthetic-read-error-secret", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ReadFailureAfterRedactionFailureDoesNotRepeatOmission()
    {
        const string secret = "synthetic-pump-secret";
        using var input = new FailingAfterContentStream(Encoding.UTF8.GetBytes(secret));
        var output = new List<(string Text, string Stream)>();

        await CommandRunSession.PumpAsync(new StreamReader(input), "stdout", [secret],
            (text, stream) => output.Add((text, stream)), TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(new[] { (StreamingOutputRedactor.OmittedMarker, "stdout") }, output.ToArray());
    }

    [TestMethod]
    public async Task NormalPartialLineStillWaitsForEndAfterSanitization()
    {
        const string secret = "synthetic-pump-secret";
        using var input = new HeldOpenStream(Encoding.UTF8.GetBytes("before " + secret));
        var output = new List<(string Text, string Stream)>();
        var pump = CommandRunSession.PumpAsync(new StreamReader(input), "stdout", [secret],
            (text, stream) => output.Add((text, stream)));
        try
        {
            await input.EndReadRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(pump.IsCompleted);
            Assert.HasCount(0, output, "正常的不完整行仍按原有换行/EOF规则发布。");
        }
        finally
        {
            input.AllowEnd();
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
        }
        CollectionAssert.AreEqual(new[] { ("before " + StreamingOutputRedactor.HiddenMarker, "stdout") }, output.ToArray());
    }

    private sealed class FailingAfterContentStream(byte[] content) : MemoryStream(content, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position == Length ? throw new IOException("synthetic-read-error-secret") : base.ReadAsync(buffer, cancellationToken);
    }

    private sealed class HeldOpenStream(byte[] content) : MemoryStream(content, writable: false)
    {
        private readonly TaskCompletionSource _allowEnd = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource EndReadRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void AllowEnd() => _allowEnd.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            if (read != 0) return read;
            EndReadRequested.TrySetResult();
            await _allowEnd.Task.WaitAsync(cancellationToken);
            return 0;
        }
    }
}
