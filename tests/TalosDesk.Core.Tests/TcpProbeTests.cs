using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Processes;

namespace TalosDesk.Core.Tests;

[TestClass]
public sealed class TcpProbeTests
{
    private static TcpProbeConfiguration Config() => new() { Port = 12345 };

    [TestMethod]
    public async Task StartupDeadlineIsIndependentAndLateSuccessRecovers()
    {
        var time = new ManualTime();
        var connector = new ControlledConnector();
        var config = Config();
        config.ConnectTimeoutSeconds = 10;
        config.StartupTimeoutSeconds = 10;
        await using var probe = new TcpProbeMonitor(config, connector, time);
        var first = connector.Attempts.Single();
        Assert.AreEqual(TcpProbeState.Waiting, probe.Snapshot.State);
        time.Advance(9);
        Assert.AreEqual(TcpProbeState.Waiting, probe.Snapshot.State);
        time.Advance(1);
        await Until(() => probe.Snapshot.State == TcpProbeState.TimedOut && probe.Snapshot.ConsecutiveFailures == 1 && time.Pending == 1);
        Assert.IsTrue(first.Token.IsCancellationRequested);
        time.Advance(2);
        await Until(() => connector.Attempts.Count == 2);
        connector.Attempts.Last().Result.SetResult();
        await Until(() => probe.Snapshot.State == TcpProbeState.Passed);
        Assert.AreEqual(0, probe.Snapshot.ConsecutiveFailures);
        Assert.IsNotNull(probe.Snapshot.LastCheckedAt);
    }

    [TestMethod]
    public async Task ThresholdKeepsPassedUntilThirdFailureAndSuccessResetsCounter()
    {
        var time = new ManualTime();
        var connector = new ControlledConnector();
        var config = Config();
        await using var probe = new TcpProbeMonitor(config, connector, time);
        config.FailureThreshold = 1; // 本次运行必须使用独立快照。
        connector.Attempts.Single().Result.SetResult();
        await Until(() => probe.Snapshot.State == TcpProbeState.Passed && time.Pending == 1);
        for (var i = 1; i <= 3; i++)
        {
            time.Advance(2);
            await Until(() => connector.Attempts.Count == i + 1);
            connector.Attempts.Last().Result.SetException(new SocketException());
            await Until(() => probe.Snapshot.ConsecutiveFailures == i && time.Pending == 1);
            Assert.AreEqual(i == 3 ? TcpProbeState.Unreachable : TcpProbeState.Passed, probe.Snapshot.State);
        }
        time.Advance(2);
        await Until(() => connector.Attempts.Count == 5);
        connector.Attempts.Last().Result.SetResult();
        await Until(() => probe.Snapshot.ConsecutiveFailures == 0 && probe.Snapshot.State == TcpProbeState.Passed && time.Pending == 1);
        time.Advance(120);
        Assert.AreEqual(TcpProbeState.Passed, probe.Snapshot.State);
    }

    [TestMethod]
    public async Task FailuresBeforeFirstSuccessStayWaitingAndChecksAreSerial()
    {
        var time = new ManualTime();
        var connector = new ControlledConnector();
        var config = Config();
        config.ConnectTimeoutSeconds = 5;
        config.FailureThreshold = 1;
        await using var probe = new TcpProbeMonitor(config, connector, time);
        time.Advance(4);
        Assert.HasCount(1, connector.Attempts);
        time.Advance(1);
        await Until(() => probe.Snapshot.ConsecutiveFailures == 1 && time.Pending == 2);
        Assert.AreEqual(TcpProbeState.Waiting, probe.Snapshot.State);
        time.Advance(1);
        Assert.HasCount(1, connector.Attempts);
        time.Advance(1);
        await Until(() => connector.Attempts.Count == 2);
        connector.Attempts.Last().Result.SetResult();
        await Until(() => probe.Snapshot.State == TcpProbeState.Passed);
    }

    [TestMethod]
    public async Task CancellationRejectsLateSuccessFailureAndDeadlineAcrossNewCycle()
    {
        foreach (var fail in new[] { false, true })
        {
            var time = new ManualTime();
            var oldConnector = new ControlledConnector();
            await using var old = new TcpProbeMonitor(Config(), oldConnector, time);
            var oldAttempt = oldConnector.Attempts.Single();
            old.Stop();
            var changes = 0;
            old.Changed += (_, _) => changes++;
            var nextConnector = new ControlledConnector();
            await using var next = new TcpProbeMonitor(Config(), nextConnector, time);
            if (fail) oldAttempt.Result.SetException(new SocketException()); else oldAttempt.Result.SetResult();
            nextConnector.Attempts.Single().Result.SetResult();
            await Until(() => next.Snapshot.State == TcpProbeState.Passed && time.Pending == 1);
            time.Advance(120);
            Assert.AreEqual(TcpProbeState.Inactive, old.Snapshot.State);
            Assert.AreEqual(0, old.Snapshot.ConsecutiveFailures);
            Assert.AreEqual(0, changes);
            Assert.AreEqual(TcpProbeState.Passed, next.Snapshot.State);
            Assert.IsTrue(oldAttempt.Token.IsCancellationRequested);
        }
    }

    [TestMethod]
    public async Task ConfigurationMigrationAndExportAreIndependentAndDoNotRewriteOnRead()
    {
        using var sandbox = new Sandbox();
        var workspace = Workspace(sandbox.Path);
        var store = new WorkspaceStore(Path.Combine(sandbox.Path, "workspace.json"));
        foreach (var schema in new[] { 1, 2, 3 })
        {
            var node = JsonSerializer.SerializeToNode(workspace)!;
            node["SchemaVersion"] = schema;
            node["Projects"]![0]!["Commands"]![0]!.AsObject().Remove("TcpProbe");
            var json = node.ToJsonString();
            await File.WriteAllTextAsync(store.FilePath, json);
            var loaded = await store.LoadAsync();
            Assert.AreEqual(json, await File.ReadAllTextAsync(store.FilePath));
            Assert.IsNull(loaded.Projects[0].Commands[0].TcpProbe);
            await store.SaveAsync(loaded);
            Assert.AreEqual(4, JsonNode.Parse(await File.ReadAllTextAsync(store.FilePath))!["SchemaVersion"]!.GetValue<int>());
        }
        await store.SaveAsync(workspace);
        var reloaded = await store.LoadAsync();
        Assert.AreEqual(12345, reloaded.Projects[0].Commands[0].TcpProbe!.Port);
        var export = WorkspaceStore.CreateExportProjection(workspace);
        export.Projects[0].Commands[0].TcpProbe!.Port = 12346;
        Assert.AreEqual(12345, workspace.Projects[0].Commands[0].TcpProbe!.Port);
        var bytes = await File.ReadAllBytesAsync(store.FilePath);
        workspace.Projects[0].Commands[0].TcpProbe!.Port = 0;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.SaveAsync(workspace));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(store.FilePath));
        workspace.Projects[0].Commands[0].TcpProbe = null;
        await store.SaveAsync(workspace);
        Assert.DoesNotContain("TcpProbe", await File.ReadAllTextAsync(store.FilePath));
    }

    [TestMethod]
    public async Task RejectsMalformedProbeAndPreservesStrictEnvironmentValidation()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Path, "invalid.json");
        var valid = JsonSerializer.Serialize(Workspace(sandbox.Path));
        var cases = new List<string>
        {
            valid.Replace("\"SchemaVersion\":4", "\"SchemaVersion\":3"),
            valid.Replace("\"Kind\":1", "\"Kind\":0"),
            valid.Replace("\"Port\":12345", "\"Port\":0"),
            valid.Replace("\"Port\":12345", "\"Port\":65536"),
            valid.Replace("\"Port\":12345", "\"Port\":12345,\"Port\":12345"),
            valid.Replace("\"Port\":12345", "\"Port\":12345,\"Unknown\":1"),
            valid.Replace("\"Address\":\"127.0.0.1\"", "\"Address\":\"localhost\""),
            valid.Replace("\"Address\":\"127.0.0.1\"", "\"Address\":\"192.0.2.1\""),
            valid.Replace("\"ConnectTimeoutSeconds\":1", "\"ConnectTimeoutSeconds\":0"),
            valid.Replace("\"IntervalSeconds\":2", "\"IntervalSeconds\":61"),
            valid.Replace("\"FailureThreshold\":3", "\"FailureThreshold\":101"),
            valid.Replace("\"StartupTimeoutSeconds\":120", "\"StartupTimeoutSeconds\":3601"),
            valid.Replace("\"ConnectTimeoutSeconds\":1", "\"ConnectTimeoutSeconds\":2").Replace("\"StartupTimeoutSeconds\":120", "\"StartupTimeoutSeconds\":1"),
            valid.Replace("\"EnvironmentVariables\":[]", "\"EnvironmentVariables\":null")
        };
        foreach (var field in new[] { "Address", "Port", "IntervalSeconds", "ConnectTimeoutSeconds", "StartupTimeoutSeconds", "FailureThreshold" })
        {
            var node = JsonNode.Parse(valid)!;
            node["Projects"]![0]!["Commands"]![0]!["TcpProbe"]!.AsObject().Remove(field);
            cases.Add(node.ToJsonString());
        }
        foreach (var json in cases)
        {
            await File.WriteAllTextAsync(path, json);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WorkspaceStore.ReadFileAsync(path));
            Assert.AreEqual(json, await File.ReadAllTextAsync(path));
        }
        foreach (var schema in new[] { 3, 4 })
        {
            var node = JsonNode.Parse(valid)!;
            node["SchemaVersion"] = schema;
            var command = node["Projects"]![0]!["Commands"]![0]!.AsObject();
            command.Remove("TcpProbe"); command.Remove("EnvironmentVariables");
            await File.WriteAllTextAsync(path, node.ToJsonString());
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WorkspaceStore.ReadFileAsync(path));
        }
    }

    [TestMethod]
    public async Task RealIpv4AndIpv6ListenersCanDisconnectAndRecover()
    {
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            using var listener = new TcpListener(address, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var configuration = new TcpProbeConfiguration { Address = address.ToString(), Port = port, IntervalSeconds = 1, FailureThreshold = 1 };
            await using var probe = new TcpProbeMonitor(configuration, new TcpProbeConnector(), TimeProvider.System);
            await Until(() => probe.Snapshot.State == TcpProbeState.Passed);
            using (var accepted = await listener.AcceptTcpClientAsync())
            {
                var buffer = new byte[1];
                Assert.AreEqual(0, await accepted.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            }
            listener.Stop();
            await Until(() => probe.Snapshot.State == TcpProbeState.Unreachable);
            using var recovered = new TcpListener(address, port);
            recovered.Start();
            await Until(() => probe.Snapshot.State == TcpProbeState.Passed);
        }
    }

    private static WorkspaceConfiguration Workspace(string root) => new()
    {
        Projects = [new() { Name = "合成项目", Directory = root, Commands = [new() { Name = "合成服务", Command = "exit 0", WorkingDirectory = root, Kind = CommandKind.Service, TcpProbe = Config() }] }]
    };

    internal static async Task Until(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(1, cancellation.Token);
    }

    private sealed class ControlledConnector : ITcpProbeConnector
    {
        internal ConcurrentQueue<Attempt> Attempts { get; } = new();
        public Task ConnectAsync(string address, int port, CancellationToken cancellationToken)
        {
            var attempt = new Attempt(cancellationToken);
            Attempts.Enqueue(attempt);
            return attempt.Result.Task; // 故意忽略取消，用于证明迟到回调无效。
        }
    }

    private sealed record Attempt(CancellationToken Token)
    {
        internal TaskCompletionSource Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Sandbox : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TalosDesk.ProbeTests", Guid.NewGuid().ToString("N"));
        internal Sandbox() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }

    private sealed class ManualTime : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<Timer> _timers = [];
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_sync) return _ticks; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        internal int Pending { get { lock (_sync) return _timers.Count(timer => timer.Due != long.MaxValue); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                var timer = new Timer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }
        internal void Advance(int seconds)
        {
            Timer[] due;
            lock (_sync)
            {
                _ticks += TimeSpan.FromSeconds(seconds).Ticks;
                due = _timers.Where(timer => timer.Due <= _ticks).ToArray();
                foreach (var timer in due) timer.Due = long.MaxValue;
            }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        private sealed class Timer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            internal long Due = long.MaxValue;
            internal TimerCallback Callback => callback;
            internal object? State => state;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync) Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                return true;
            }
            public void Dispose() { lock (owner._sync) { Due = long.MaxValue; owner._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
