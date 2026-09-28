using System.Security.Cryptography;
using System.Text;
using TalosDesk.Core.Configuration;
using TalosDesk.Core.Processes;

namespace TalosDesk.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CommandRunEnvironmentTests
{
    private const string VariableName = "TALOSDESK_SYNTHETIC_SECRET_7B2E";

    [TestMethod]
    public void ResolvesPlainEmptyAndProtectedValuesWithoutChangingParentEnvironment()
    {
        const string secret = "合成凭据-Only-For-Tests";
        var original = Environment.GetEnvironmentVariable(VariableName);
        var command = CreateCommand([
            new CommandEnvironmentVariable { Name = "APP_MODE", Value = "" },
            new CommandEnvironmentVariable { Name = VariableName, IsSensitive = true, ValueState = "Protected", ProtectedValue = SensitiveValueProtector.Protect(secret) }
        ]);

        var resolved = CommandRunEnvironmentResolver.Resolve(command);

        Assert.AreEqual("", resolved.Overrides["app_mode"]);
        Assert.AreEqual(secret, resolved.Overrides[VariableName]);
        CollectionAssert.AreEqual(new[] { secret }, resolved.SensitiveValues.ToArray());
        Assert.AreEqual(original, Environment.GetEnvironmentVariable(VariableName));
    }

    [TestMethod]
    public void MissingOrUnavailableSensitiveValueFailsWithoutExposingUnderlyingError()
    {
        const string secret = "synthetic-error-secret";
        var missing = CreateCommand([
            new CommandEnvironmentVariable { Name = VariableName, IsSensitive = true, ValueState = "Required" }
        ]);
        var missingError = Assert.ThrowsExactly<CommandEnvironmentException>(() => CommandRunEnvironmentResolver.Resolve(missing));
        StringAssert.Contains(missingError.Message, VariableName);

        var protectedCommand = CreateCommand([
            new CommandEnvironmentVariable { Name = VariableName, IsSensitive = true, ValueState = "Protected", ProtectedValue = "damaged" }
        ]);
        var unavailable = Assert.ThrowsExactly<CommandEnvironmentException>(() =>
            CommandRunEnvironmentResolver.Resolve(protectedCommand, _ => throw new InvalidOperationException(secret)));
        StringAssert.Contains(unavailable.Message, "不可解密");
        Assert.IsFalse(unavailable.Message.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(unavailable.Message.Contains("damaged", StringComparison.Ordinal));

        var duplicate = CreateCommand([
            new CommandEnvironmentVariable { Name = "APP_MODE", Value = "one" },
            new CommandEnvironmentVariable { Name = "app_mode", Value = "two" }
        ]);
        Assert.ThrowsExactly<CommandEnvironmentException>(() => CommandRunEnvironmentResolver.Resolve(duplicate));

        var runner = new CommandRunner();
        var commandId = Guid.NewGuid();
        Assert.ThrowsExactly<ArgumentException>(() => runner.Start(commandId, "exit 0", Path.GetTempPath(),
            runEnvironment: new CommandRunEnvironment(new Dictionary<string, string>(), [new string('x', 4097)])));
        Assert.IsFalse(runner.IsRunning(commandId));
    }

    [TestMethod]
    public async Task InjectedValueIsRedactedBeforeMemoryEventsAndDiskLogs()
    {
        using var sandbox = new TemporaryDirectory();
        const string secret = "synthetic-run-secret-α";
        var command = CreateCommand([
            new CommandEnvironmentVariable { Name = "TALOSDESK_SYNTHETIC_EMPTY_7B2E", Value = "" },
            new CommandEnvironmentVariable { Name = VariableName, IsSensitive = true, ValueState = "Protected", ProtectedValue = SensitiveValueProtector.Protect(secret) }
        ]);
        var environment = CommandRunEnvironmentResolver.Resolve(command);
        var store = new RunLogStore(Path.Combine(sandbox.Path, "workspace.json"));
        store.Initialize();
        var writer = store.Begin(Guid.NewGuid(), command.Id, DateTimeOffset.Now);
        var events = new List<CommandOutput>();
        var original = Environment.GetEnvironmentVariable(VariableName);
        var script = $"Write-Output ('out:' + $env:{VariableName}); [Console]::Error.WriteLine('err:' + $env:{VariableName}); Write-Output ('empty:' + $env:TALOSDESK_SYNTHETIC_EMPTY_7B2E); Write-Output ('has-path:' + [bool]$env:PATH); Write-Output 'Bearer synthetic-pattern-token'";
        var runner = new CommandRunner();

        await using var session = runner.Start(command.Id, script, sandbox.Path, (_, output) =>
        {
            lock (events) events.Add(output);
            store.Append(writer, output);
        }, environment);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        store.Complete(writer, result);

        Assert.AreEqual(CommandRunState.Succeeded, result.State);
        Assert.AreEqual(original, Environment.GetEnvironmentVariable(VariableName));
        var memory = string.Join("\n", session.GetRecentOutput().Select(entry => entry.Text));
        Assert.IsTrue(memory.Contains("out:[已隐藏]", StringComparison.Ordinal));
        Assert.IsTrue(memory.Contains("err:[已隐藏]", StringComparison.Ordinal));
        Assert.IsTrue(memory.Contains("empty:", StringComparison.Ordinal));
        Assert.IsTrue(memory.Contains("has-path:True", StringComparison.Ordinal));
        Assert.IsTrue(memory.Contains("Bearer [已隐藏]", StringComparison.Ordinal));
        Assert.IsFalse(memory.Contains(secret, StringComparison.Ordinal));
        lock (events) Assert.IsFalse(string.Join("\n", events.Select(entry => entry.Text)).Contains(secret, StringComparison.Ordinal));
        foreach (var file in Directory.GetFiles(store.RootPath, "*", SearchOption.AllDirectories))
            Assert.IsFalse((await File.ReadAllTextAsync(file)).Contains(secret, StringComparison.Ordinal), $"Secret reached {Path.GetFileName(file)}.");
    }

    [TestMethod]
    public async Task ConcurrentCommandsUseIndependentEnvironmentSnapshots()
    {
        using var sandbox = new TemporaryDirectory();
        const string firstSecret = "synthetic-first-value";
        const string secondSecret = "synthetic-second-value";
        var first = CreateCommand([new CommandEnvironmentVariable { Name = VariableName, IsSensitive = true, ValueState = "Protected", ProtectedValue = SensitiveValueProtector.Protect(firstSecret) }]);
        var second = CreateCommand([new CommandEnvironmentVariable { Name = VariableName, IsSensitive = true, ValueState = "Protected", ProtectedValue = SensitiveValueProtector.Protect(secondSecret) }]);
        var original = Environment.GetEnvironmentVariable(VariableName);
        var script = $"$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($env:{VariableName}))); Write-Output $hash; Start-Sleep -Milliseconds 150";
        var runner = new CommandRunner();

        await using var firstSession = runner.Start(first.Id, script, sandbox.Path,
            runEnvironment: CommandRunEnvironmentResolver.Resolve(first));
        await using var secondSession = runner.Start(second.Id, script, sandbox.Path,
            runEnvironment: CommandRunEnvironmentResolver.Resolve(second));
        var results = await Task.WhenAll(firstSession.Completion, secondSession.Completion).WaitAsync(TimeSpan.FromSeconds(25));

        Assert.IsTrue(results.All(result => result.State == CommandRunState.Succeeded));
        Assert.IsTrue(firstSession.GetRecentOutput().Any(output => output.Text == Sha256(firstSecret)));
        Assert.IsTrue(secondSession.GetRecentOutput().Any(output => output.Text == Sha256(secondSecret)));
        Assert.AreEqual(original, Environment.GetEnvironmentVariable(VariableName));
    }

    [TestMethod]
    public async Task PowerShellFailureDoesNotExposeSensitiveValueInDiagnostics()
    {
        using var sandbox = new TemporaryDirectory();
        const string secret = "synthetic-diagnostic-secret";
        var command = CreateCommand([
            new CommandEnvironmentVariable { Name = VariableName, IsSensitive = true, ValueState = "Protected", ProtectedValue = SensitiveValueProtector.Protect(secret) }
        ]);
        var runner = new CommandRunner();
        await using var session = runner.Start(command.Id, $"throw ('failure:' + $env:{VariableName})", sandbox.Path,
            runEnvironment: CommandRunEnvironmentResolver.Resolve(command));

        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        var output = string.Join("\n", session.GetRecentOutput().Select(entry => entry.Text));

        Assert.AreEqual(CommandRunState.Failed, result.State);
        Assert.IsTrue(output.Contains("failure:[已隐藏]", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains(secret, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task StoppingAConfiguredCommandDoesNotExposePendingSecretOutput()
    {
        using var sandbox = new TemporaryDirectory();
        const string secret = "synthetic-stop-secret";
        var command = CreateCommand([
            new CommandEnvironmentVariable { Name = VariableName, IsSensitive = true, ValueState = "Protected", ProtectedValue = SensitiveValueProtector.Protect(secret) }
        ]);
        var script = $"[Console]::Out.Write('partial:' + $env:{VariableName}); [Console]::Out.Flush(); [Console]::Error.WriteLine('pending-written'); while ($true) {{ Start-Sleep -Seconds 1 }}";
        var runner = new CommandRunner(new ForceStopOperations());
        await using var session = runner.Start(command.Id, script, sandbox.Path,
            runEnvironment: CommandRunEnvironmentResolver.Resolve(command));

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!session.GetRecentOutput().Any(output => output.Text == "pending-written") && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
        Assert.IsTrue(session.GetRecentOutput().Any(output => output.Text == "pending-written"));
        Assert.AreEqual(CommandStopResult.ForceTerminated, await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(12)));
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(12));

        Assert.AreEqual(CommandRunState.Stopped, result.State);
        var text = string.Join("\n", session.GetRecentOutput().Select(output => output.Text));
        Assert.IsFalse(text.Contains(secret, StringComparison.Ordinal));
        if (text.Contains("partial:", StringComparison.Ordinal))
            Assert.IsTrue(text.Contains("partial:[已隐藏]", StringComparison.Ordinal));
    }

    private static CommandDefinition CreateCommand(List<CommandEnvironmentVariable> variables) => new()
    {
        Name = "Synthetic",
        Command = "Write-Output 'synthetic'",
        WorkingDirectory = Path.GetTempPath(),
        EnvironmentVariables = variables
    };

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class ForceStopOperations : ICommandStopOperations
    {
        private int _waitCount;

        public bool TrySendCtrlC(int processId) => false;

        public Task<bool> WaitForCommandGroupExitAsync(SafeJobHandle job, TimeSpan timeout, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _waitCount) == 1
                ? Task.FromResult(false)
                : WindowsNative.WaitForCommandGroupExitAsync(job, timeout, cancellationToken);

        public void TerminateJob(SafeJobHandle job) => WindowsNative.TerminateJob(job);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"TalosDesk-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
