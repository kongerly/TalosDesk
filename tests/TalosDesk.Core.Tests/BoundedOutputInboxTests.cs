using TalosDesk.Core.Processes;

namespace TalosDesk.Core.Tests;

[TestClass]
public sealed class BoundedOutputInboxTests
{
    [TestMethod]
    public void KeepsLatestLinesForEachCommandAndDrainsFairly()
    {
        var inbox = new BoundedOutputInbox(3);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        for (var index = 1; index <= 5; index++) inbox.Enqueue(first, 1, Output(index));
        inbox.Enqueue(second, 2, Output(100));

        var batch = inbox.Take(2);
        Assert.HasCount(2, batch);
        Assert.AreEqual(first, batch[0].CommandId);
        Assert.AreEqual("3", batch[0].Output.Text);
        Assert.AreEqual(second, batch[1].CommandId);
        Assert.AreEqual("100", batch[1].Output.Text);

        var remaining = inbox.Take(10);
        CollectionAssert.AreEqual(new[] { "4", "5" }, remaining.Select(item => item.Output.Text).ToArray());
        Assert.IsFalse(inbox.HasPending);
    }

    [TestMethod]
    public void ClearingOneCommandDoesNotDiscardAnotherCommandsOutput()
    {
        var inbox = new BoundedOutputInbox(2);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        inbox.Enqueue(first, 1, Output(1));
        inbox.Enqueue(second, 1, Output(2));
        inbox.Clear(first);
        inbox.Enqueue(first, 2, Output(3));

        var remaining = inbox.Take(10);
        Assert.HasCount(2, remaining);
        Assert.IsTrue(remaining.Any(item => item.CommandId == first && item.RunVersion == 2 && item.Output.Text == "3"));
        Assert.IsTrue(remaining.Any(item => item.CommandId == second && item.Output.Text == "2"));
        Assert.IsFalse(inbox.HasPending);
    }

    [TestMethod]
    public void HighVolumeConcurrentOutputRemainsBoundedPerCommand()
    {
        var inbox = new BoundedOutputInbox(10_000);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Parallel.Invoke(
            () => { for (var index = 1; index <= 20_500; index++) inbox.Enqueue(first, 1, Output(index)); },
            () => { for (var index = 1; index <= 20_500; index++) inbox.Enqueue(second, 1, Output(index)); });

        var drained = new List<PendingCommandOutput>();
        while (inbox.HasPending) drained.AddRange(inbox.Take(500));
        Assert.HasCount(20_000, drained);
        foreach (var commandId in new[] { first, second })
        {
            var lines = drained.Where(item => item.CommandId == commandId).Select(item => item.Output.Text).ToArray();
            Assert.HasCount(10_000, lines);
            Assert.AreEqual("10501", lines[0]);
            Assert.AreEqual("20500", lines[^1]);
        }
    }

    private static CommandOutput Output(int value) =>
        new(DateTimeOffset.UtcNow, "stdout", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
