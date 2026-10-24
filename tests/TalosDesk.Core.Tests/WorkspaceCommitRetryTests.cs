using System.Security.Cryptography;
using System.Text.Json;
using TalosDesk.Core.Configuration;

namespace TalosDesk.Core.Tests;

[TestClass]
public sealed class WorkspaceCommitRetryTests
{
    [TestMethod]
    [DataRow(5, false, false)]
    [DataRow(32, false, false)]
    [DataRow(5, true, false)]
    [DataRow(32, true, false)]
    [DataRow(5, true, true)]
    [DataRow(32, true, true)]
    public async Task TransientMoveFailureRetriesSameBytesAndReturnsTheirRevision(int code, bool guarded, bool firstCreation)
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Path, "workspace.json");
        var expected = firstCreation ? WorkspaceRevision.Missing : await new WorkspaceStore(path).SaveAsync(Configuration("Original"));
        var candidate = Configuration("Candidate");
        candidate.SchemaVersion = 1;
        var callerBytes = JsonSerializer.SerializeToUtf8Bytes(candidate);
        var attempts = 0;
        var before = 0;
        var after = 0;
        var sources = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var store = new WorkspaceStore(path, () => before++, () => after++, (source, destination, overwrite) =>
        {
            attempts++;
            sources.Add(source);
            hashes.Add(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))));
            Assert.AreEqual(!firstCreation, overwrite);
            if (attempts <= 2) throw Failure(code);
            File.Move(source, destination, overwrite);
        });

        var saved = guarded ? await store.SaveAsync(candidate, expected) : await store.SaveAsync(candidate);

        Assert.IsGreaterThanOrEqualTo(3, attempts);
        Assert.IsLessThanOrEqualTo(6, attempts);
        Assert.AreEqual(1, before);
        Assert.AreEqual(1, after);
        Assert.HasCount(1, sources);
        Assert.HasCount(1, hashes);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.AreEqual(new WorkspaceRevision(true, Convert.ToHexString(SHA256.HashData(bytes))), saved);
        Assert.AreEqual("Candidate", (await store.LoadAsync()).Projects.Single().Name);
        CollectionAssert.AreEqual(callerBytes, JsonSerializer.SerializeToUtf8Bytes(candidate));
        Assert.HasCount(0, Directory.GetFiles(sandbox.Path, "*.tmp"));
    }

    [TestMethod]
    [DataRow(5)]
    [DataRow(32)]
    [DataRow(112)]
    public async Task PersistentOrNonRetryableFailureKeepsOriginalAndCleansTemporary(int code)
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Path, "workspace.json");
        var expected = await new WorkspaceStore(path).SaveAsync(Configuration("Original"));
        var original = await File.ReadAllBytesAsync(path);
        var candidate = Configuration("Candidate");
        candidate.SchemaVersion = 1;
        var callerBytes = JsonSerializer.SerializeToUtf8Bytes(candidate);
        var attempts = 0;
        var after = 0;
        var store = new WorkspaceStore(path, null, () => after++, (_, _, _) =>
        {
            attempts++;
            throw Failure(code);
        });

        if (code == 5) await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => store.SaveAsync(candidate, expected));
        else await Assert.ThrowsExactlyAsync<IOException>(() => store.SaveAsync(candidate, expected));

        Assert.AreEqual(code == 112 ? 1 : 6, attempts, "持续故障必须有界，其他错误不得重试。");
        Assert.AreEqual(0, after);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
        CollectionAssert.AreEqual(callerBytes, JsonSerializer.SerializeToUtf8Bytes(candidate));
        Assert.HasCount(0, Directory.GetFiles(sandbox.Path, "*.tmp"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExternalChangeBeforeRetryStopsCommitWithoutOverwriting(bool firstCreation)
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Path, "workspace.json");
        var expected = firstCreation ? WorkspaceRevision.Missing : await new WorkspaceStore(path).SaveAsync(Configuration("Original"));
        var external = JsonSerializer.SerializeToUtf8Bytes(Configuration("External"));
        var attempts = 0;
        var after = 0;
        var store = new WorkspaceStore(path, null, () => after++, (_, destination, overwrite) =>
        {
            attempts++;
            Assert.AreEqual(!firstCreation, overwrite);
            File.WriteAllBytes(destination, external);
            throw Failure(5);
        });

        await Assert.ThrowsExactlyAsync<WorkspaceChangedException>(() => store.SaveAsync(Configuration("Candidate"), expected));

        Assert.AreEqual(1, attempts, "重试替换前必须重新校验外部版本。");
        Assert.AreEqual(0, after);
        CollectionAssert.AreEqual(external, await File.ReadAllBytesAsync(path));
        Assert.HasCount(0, Directory.GetFiles(sandbox.Path, "*.tmp"));
    }

    [TestMethod]
    public async Task CancellationAfterMoveFailureStopsRetryAndKeepsOriginal()
    {
        using var sandbox = new Sandbox();
        using var cancellation = new CancellationTokenSource();
        var path = Path.Combine(sandbox.Path, "workspace.json");
        var expected = await new WorkspaceStore(path).SaveAsync(Configuration("Original"));
        var original = await File.ReadAllBytesAsync(path);
        var attempts = 0;
        var store = new WorkspaceStore(path, null, null, (_, _, _) =>
        {
            attempts++;
            cancellation.Cancel();
            throw Failure(32);
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.SaveAsync(Configuration("Candidate"), expected, cancellation.Token));

        Assert.AreEqual(1, attempts);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
        Assert.HasCount(0, Directory.GetFiles(sandbox.Path, "*.tmp"));
    }

    private static Exception Failure(int code) => code == 5
        ? new UnauthorizedAccessException("Synthetic commit denied.")
        : new IOException("Synthetic commit failure.", unchecked((int)(0x80070000u | (uint)code)));

    [TestMethod]
    public async Task MissingTemporaryAfterFailureIsNotRetried()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Path, "workspace.json");
        var expected = await new WorkspaceStore(path).SaveAsync(Configuration("Original"));
        var original = await File.ReadAllBytesAsync(path);
        var attempts = 0;
        var store = new WorkspaceStore(path, null, null, (source, _, _) =>
        {
            attempts++;
            File.Delete(source);
            throw Failure(5);
        });

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => store.SaveAsync(Configuration("Candidate"), expected));

        Assert.AreEqual(1, attempts);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
        Assert.HasCount(0, Directory.GetFiles(sandbox.Path, "*.tmp"));
    }

    private static WorkspaceConfiguration Configuration(string name) => new()
    {
        Projects = [new ProjectDefinition { Name = name, Directory = @"X:\TalosDesk-Synthetic\Project" }]
    };

    private sealed class Sandbox : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"TalosDesk-commit-tests-{Guid.NewGuid():N}");
        internal Sandbox() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
