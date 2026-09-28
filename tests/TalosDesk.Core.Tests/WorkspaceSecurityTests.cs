using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using TalosDesk.Core.Configuration;

namespace TalosDesk.Core.Tests;

[TestClass]
public sealed class WorkspaceSecurityTests
{
    [TestMethod]
    public void CurrentUserProtectionRoundTripsUnicodeAndExplicitEmptyValue()
    {
        const string secret = "合成令牌-For-Test-Only";
        var protectedValue = SensitiveValueProtector.Protect(secret);

        Assert.AreNotEqual(secret, protectedValue);
        Assert.AreEqual(secret, SensitiveValueProtector.Unprotect(protectedValue));
        Assert.AreEqual(string.Empty, SensitiveValueProtector.Unprotect(SensitiveValueProtector.Protect(string.Empty)));
        Assert.AreEqual(new string('测', SensitiveValueProtector.MaximumValueLength),
            SensitiveValueProtector.Unprotect(SensitiveValueProtector.Protect(new string('测', SensitiveValueProtector.MaximumValueLength))));
        Assert.ThrowsExactly<ArgumentException>(() => SensitiveValueProtector.Protect(new string('x', SensitiveValueProtector.MaximumValueLength + 1)));
        Assert.ThrowsExactly<ArgumentException>(() => SensitiveValueProtector.Protect("bad\0value"));
    }

    [TestMethod]
    public async Task LocalSaveKeepsProtectedValueAndExportOmitsItWithoutMutatingWorkspace()
    {
        using var sandbox = new TemporaryDirectory();
        const string secret = "synthetic-secret-for-export-test";
        var protectedValue = SensitiveValueProtector.Protect(secret);
        var workspace = CreateWorkspace(sandbox.Path,
        [
            new CommandEnvironmentVariable { Name = "APP_MODE", Value = "" },
            new CommandEnvironmentVariable { Name = "API_TOKEN", IsSensitive = true, ValueState = "Protected", ProtectedValue = protectedValue },
            new CommandEnvironmentVariable { Name = "SECOND_TOKEN", IsSensitive = true, ValueState = "Required" }
        ]);
        var localPath = Path.Combine(sandbox.Path, "workspace.json");
        var exportPath = Path.Combine(sandbox.Path, "export.json");
        var store = new WorkspaceStore(localPath);

        await store.SaveAsync(workspace);
        var localText = await File.ReadAllTextAsync(localPath);
        Assert.IsFalse(localText.Contains(secret, StringComparison.Ordinal));
        Assert.AreEqual(protectedValue, (string?)JsonNode.Parse(localText)!["Projects"]![0]!["Commands"]![0]!["EnvironmentVariables"]![1]!["ProtectedValue"]);
        var loaded = await store.LoadAsync();
        var loadedVariables = loaded.Projects[0].Commands[0].EnvironmentVariables;
        Assert.AreEqual("", loadedVariables[0].Value);
        Assert.AreEqual("Protected", loadedVariables[1].ValueState);
        Assert.AreEqual(secret, SensitiveValueProtector.Unprotect(loadedVariables[1].ProtectedValue!));

        var projection = WorkspaceStore.CreateExportProjection(loaded);
        Assert.AreNotSame(loaded.Projects[0], projection.Projects[0]);
        Assert.AreNotSame(loadedVariables[1], projection.Projects[0].Commands[0].EnvironmentVariables[1]);
        Assert.AreEqual("Required", projection.Projects[0].Commands[0].EnvironmentVariables[1].ValueState);
        Assert.IsNull(projection.Projects[0].Commands[0].EnvironmentVariables[1].ProtectedValue);
        Assert.AreEqual(protectedValue, loadedVariables[1].ProtectedValue);
        Assert.AreEqual(loaded.Projects[0].Groups[0].CommandIds[0], projection.Projects[0].Groups[0].CommandIds[0]);

        await WorkspaceStore.WriteExportFileAsync(exportPath, loaded);
        var exportText = await File.ReadAllTextAsync(exportPath);
        Assert.IsFalse(exportText.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(exportText.Contains(protectedValue, StringComparison.Ordinal));
        Assert.AreEqual(protectedValue, loadedVariables[1].ProtectedValue);
        Assert.AreEqual("Required", (await WorkspaceStore.ReadFileAsync(exportPath)).Projects[0].Commands[0].EnvironmentVariables[1].ValueState);

        var legacyVersion = loaded.SchemaVersion;
        loaded.SchemaVersion = 2;
        _ = WorkspaceStore.CreateExportProjection(loaded);
        Assert.AreEqual(2, loaded.SchemaVersion);
        loaded.SchemaVersion = legacyVersion;
    }

    [TestMethod]
    public async Task LegacySchemaTwoReadDoesNotRewriteAndSaveMigratesToThree()
    {
        using var sandbox = new TemporaryDirectory();
        var path = Path.Combine(sandbox.Path, "legacy.json");
        var old = CreateWorkspace(sandbox.Path, []);
        old.SchemaVersion = 2;
        var node = JsonSerializer.SerializeToNode(old)!.AsObject();
        node["Projects"]![0]!["Commands"]![0]!.AsObject().Remove("EnvironmentVariables");
        await File.WriteAllTextAsync(path, node.ToJsonString());
        var originalBytes = await File.ReadAllBytesAsync(path);
        var store = new WorkspaceStore(path);

        var loaded = await store.LoadAsync();
        Assert.AreEqual(3, loaded.SchemaVersion);
        Assert.HasCount(0, loaded.Projects[0].Commands[0].EnvironmentVariables);
        CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(path));
        await store.SaveAsync(loaded);
        var savedNode = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.AreEqual(3, (int)savedNode["SchemaVersion"]!);
        Assert.IsNotNull(savedNode["Projects"]![0]!["Commands"]![0]!["EnvironmentVariables"]);
        Assert.AreEqual(old.Projects[0].Groups[0].CommandIds[0], loaded.Projects[0].Groups[0].CommandIds[0]);
    }

    [TestMethod]
    public async Task DamagedCiphertextLoadsUnchangedAndReportsUnavailable()
    {
        using var sandbox = new TemporaryDirectory();
        var path = Path.Combine(sandbox.Path, "workspace.json");
        var store = new WorkspaceStore(path);
        await store.SaveAsync(CreateWorkspace(sandbox.Path,
        [new CommandEnvironmentVariable { Name = "API_TOKEN", IsSensitive = true, ValueState = "Protected", ProtectedValue = "not-base64!" }]));
        var originalBytes = await File.ReadAllBytesAsync(path);

        var loaded = await store.LoadAsync();
        Assert.AreEqual("not-base64!", loaded.Projects[0].Commands[0].EnvironmentVariables[0].ProtectedValue);
        var error = Assert.ThrowsExactly<SensitiveValueUnavailableException>(() => SensitiveValueProtector.Unprotect("not-base64!"));
        Assert.IsFalse(error.Message.Contains("not-base64!", StringComparison.Ordinal));
        var foreignCiphertext = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        Assert.ThrowsExactly<SensitiveValueUnavailableException>(() => SensitiveValueProtector.Unprotect(foreignCiphertext));
        Assert.ThrowsExactly<SensitiveValueUnavailableException>(() => SensitiveValueProtector.Unprotect(new string('A', 65_537)));
        CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(path));
    }

    [TestMethod]
    public async Task RejectsMissingOrAmbiguousSchemaThreeVariableFields()
    {
        using var sandbox = new TemporaryDirectory();
        var workspace = CreateWorkspace(sandbox.Path,
            [new CommandEnvironmentVariable { Name = "API_TOKEN", IsSensitive = true, ValueState = "Required" }]);
        var baseNode = JsonSerializer.SerializeToNode(workspace)!;
        var invalidCases = new Action<JsonNode>[]
        {
            root => root["Projects"]![0]!["Commands"]![0]!.AsObject().Remove("EnvironmentVariables"),
            root => root["Projects"]![0]!["Commands"]![0]!["EnvironmentVariables"] = null,
            root => root["Projects"]![0]!["Commands"]![0]!["EnvironmentVariables"]![0]!.AsObject().Remove("IsSensitive"),
            root => root["Projects"]![0]!["Commands"]![0]!["EnvironmentVariables"]![0]!["Value"] = "plain-leak",
            root => root["Projects"]![0]!["Commands"]![0]!["EnvironmentVariables"]![0]!["ValueState"] = "Unknown",
            root => root["Projects"]![0]!["Commands"]![0]!["EnvironmentVariables"]![0]!["Name"] = "api=token",
            root => root["Projects"]![0]!["Commands"]![0]!["EnvironmentVariables"]![0]!["Name"] = " API_TOKEN",
            root => root["Projects"]![0]!["Commands"]![0]!["EnvironmentVariables"]![0]!["Name"] = "api\0token",
            root => root["Projects"]![0]!["Commands"]![0]!["EnvironmentVariables"]!.AsArray().Add(
                JsonSerializer.SerializeToNode(new CommandEnvironmentVariable { Name = "api_token", IsSensitive = true, ValueState = "Required" })),
            root => root["Projects"]![0]!["Commands"]![0]!["EnvironmentVariables"]![0] =
                JsonSerializer.SerializeToNode(new CommandEnvironmentVariable { Name = "PLAIN", Value = "bad\0value" })
        };
        for (var index = 0; index < invalidCases.Length; index++)
        {
            var node = baseNode.DeepClone();
            invalidCases[index](node);
            var path = Path.Combine(sandbox.Path, $"invalid-{index}.json");
            await File.WriteAllTextAsync(path, node.ToJsonString());
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WorkspaceStore.ReadFileAsync(path));
        }
    }

    [TestMethod]
    public async Task FailedExportPreservesExistingFileAndDoesNotLeaveTemporaryFile()
    {
        using var sandbox = new TemporaryDirectory();
        var path = Path.Combine(sandbox.Path, "export.json");
        await File.WriteAllTextAsync(path, "original export");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Exception? failure = null;
            try { await WorkspaceStore.WriteExportFileAsync(path, CreateWorkspace(sandbox.Path, [])); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failure = exception; }
            Assert.IsNotNull(failure, "Replacing a locked export must fail.");
        }
        Assert.AreEqual("original export", await File.ReadAllTextAsync(path));
        Assert.HasCount(0, Directory.GetFiles(sandbox.Path, "export.json.*.tmp"));
    }

    [TestMethod]
    public async Task FailedProtectedSaveKeepsOldCiphertextAndRejectsSensitivePlaintext()
    {
        using var sandbox = new TemporaryDirectory();
        var path = Path.Combine(sandbox.Path, "workspace.json");
        var store = new WorkspaceStore(path);
        var originalCiphertext = SensitiveValueProtector.Protect("synthetic-original-secret");
        await store.SaveAsync(CreateWorkspace(sandbox.Path,
            [new CommandEnvironmentVariable { Name = "API_TOKEN", IsSensitive = true, ValueState = "Protected", ProtectedValue = originalCiphertext }]));
        var originalBytes = await File.ReadAllBytesAsync(path);

        var invalid = CreateWorkspace(sandbox.Path,
            [new CommandEnvironmentVariable { Name = "API_TOKEN", IsSensitive = true, ValueState = "Protected", ProtectedValue = originalCiphertext, Value = "synthetic-plaintext-leak" }]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.SaveAsync(invalid));
        CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(path));

        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Exception? failure = null;
            try
            {
                await store.SaveAsync(CreateWorkspace(sandbox.Path,
                    [new CommandEnvironmentVariable { Name = "API_TOKEN", IsSensitive = true, ValueState = "Protected", ProtectedValue = SensitiveValueProtector.Protect("synthetic-replacement-secret") }]));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failure = exception; }
            Assert.IsNotNull(failure, "Replacing a locked local workspace must fail.");
        }
        CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(originalCiphertext, (await store.LoadAsync()).Projects[0].Commands[0].EnvironmentVariables[0].ProtectedValue);
        Assert.HasCount(0, Directory.GetFiles(sandbox.Path, "workspace.json.*.tmp"));
    }

    private static WorkspaceConfiguration CreateWorkspace(string directory, List<CommandEnvironmentVariable> variables)
    {
        var command = new CommandDefinition
        {
            Name = "Check",
            Command = "Write-Output 'ok'",
            WorkingDirectory = directory,
            EnvironmentVariables = variables
        };
        return new WorkspaceConfiguration
        {
            Projects =
            [
                new ProjectDefinition
                {
                    Name = "Synthetic",
                    Directory = directory,
                    Commands = [command],
                    Groups = [new CommandGroupDefinition { Name = "Checks", CommandIds = [command.Id] }]
                }
            ]
        };
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
