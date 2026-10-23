using System.Text.Json;
using System.Text.Json.Nodes;
using TalosDesk.Core.Configuration;

namespace TalosDesk.Core.Tests;

[TestClass]
public sealed class RecoveryConfigurationTests
{
    [TestMethod]
    [DataRow(1)] [DataRow(2)] [DataRow(3)] [DataRow(4)] [DataRow(5)]
    public async Task ReadDoesNotRewriteAndSaveUpgradesWithoutEnablingRecovery(int schema)
    {
        using var sandbox = new Sandbox();
        var node = JsonSerializer.SerializeToNode(Configuration(sandbox.Root))!;
        node["SchemaVersion"] = schema;
        if (schema < 4) node["Projects"]![0]!["Commands"]![0]!.AsObject().Remove("TcpProbe");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(node);
        await File.WriteAllBytesAsync(sandbox.Store.FilePath, bytes);
        var loaded = await sandbox.Store.LoadAsync();
        Assert.IsFalse(loaded.Projects[0].Commands[0].AutoRestartEnabled);
        Assert.AreEqual(schema >= 4, loaded.Projects[0].Commands[0].TcpProbe is not null);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(sandbox.Store.FilePath));
        await sandbox.Store.SaveAsync(loaded);
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(sandbox.Store.FilePath))!;
        Assert.AreEqual(WorkspaceStore.CurrentSchemaVersion, saved["SchemaVersion"]!.GetValue<int>());
        Assert.IsFalse(saved["Projects"]![0]!["Commands"]![0]!.AsObject().ContainsKey("AutoRestartEnabled"));
        Assert.HasCount(1, Directory.GetFiles(sandbox.Root));
    }

    [TestMethod]
    [DataRow(1, "true")] [DataRow(1, "false")] [DataRow(2, "true")] [DataRow(2, "false")]
    [DataRow(3, "true")] [DataRow(3, "false")] [DataRow(4, "true")] [DataRow(4, "false")]
    [DataRow(5, "null")] [DataRow(5, "1")] [DataRow(5, "\"true\"")] [DataRow(5, "{}")] [DataRow(5, "[]")]
    public async Task InvalidFieldDoesNotModifyLoadOrImportSource(int schema, string value)
    {
        using var sandbox = new Sandbox();
        var node = JsonSerializer.SerializeToNode(Configuration(sandbox.Root))!;
        node["SchemaVersion"] = schema;
        node["Projects"]![0]!["Commands"]![0]!["AutoRestartEnabled"] = JsonNode.Parse(value);
        if (schema < 4) node["Projects"]![0]!["Commands"]![0]!.AsObject().Remove("TcpProbe");
        await RejectUnchanged(sandbox, node.ToJsonString());
    }

    [TestMethod]
    [DataRow("duplicate")] [DataRow("unknown")] [DataRow("task")]
    public async Task DuplicateUnknownAndTaskPolicyAreRejected(string scenario)
    {
        using var sandbox = new Sandbox();
        var configuration = Configuration(sandbox.Root);
        configuration.Projects[0].Commands[0].AutoRestartEnabled = true;
        var json = JsonSerializer.Serialize(configuration);
        json = scenario switch
        {
            "duplicate" => json.Replace("\"AutoRestartEnabled\":true", "\"AutoRestartEnabled\":true,\"AutoRestartEnabled\":false"),
            "unknown" => json.Replace("AutoRestartEnabled", "AutoRestartEnabledd"),
            _ => json.Replace("\"Kind\":1", "\"Kind\":0")
        };
        if (scenario == "task") { var node = JsonNode.Parse(json)!; node["Projects"]![0]!["Commands"]![0]!.AsObject().Remove("TcpProbe"); json = node.ToJsonString(); }
        await RejectUnchanged(sandbox, json);
    }

    [TestMethod]
    public async Task EnabledPolicySurvivesSaveExportAndFailedSaveWithoutSharingSecrets()
    {
        using var sandbox = new Sandbox();
        var configuration = Configuration(sandbox.Root);
        var command = configuration.Projects[0].Commands[0];
        command.AutoRestartEnabled = true;
        command.EnvironmentVariables = [new() { Name = "SYNTHETIC_SECRET", IsSensitive = true, ValueState = "Protected", ProtectedValue = SensitiveValueProtector.Protect("synthetic-only-value") }];
        await sandbox.Store.SaveAsync(configuration);
        Assert.IsTrue((await sandbox.Store.LoadAsync()).Projects[0].Commands[0].AutoRestartEnabled);
        var export = WorkspaceStore.CreateExportProjection(configuration);
        Assert.IsTrue(export.Projects[0].Commands[0].AutoRestartEnabled);
        Assert.IsNull(export.Projects[0].Commands[0].EnvironmentVariables[0].ProtectedValue);
        export.Projects[0].Commands[0].AutoRestartEnabled = false;
        Assert.IsTrue(command.AutoRestartEnabled);
        var before = await File.ReadAllBytesAsync(sandbox.Store.FilePath);
        command.Kind = CommandKind.Task; command.TcpProbe = null;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => sandbox.Store.SaveAsync(configuration));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(sandbox.Store.FilePath));
        Assert.IsTrue(command.AutoRestartEnabled);
        Assert.HasCount(1, Directory.GetFiles(sandbox.Root));
    }

    [TestMethod]
    [DataRow(CommandKind.Task)] [DataRow(CommandKind.Service)]
    public async Task ExplicitFalseIsAcceptedAndOmittedWhenSaved(CommandKind kind)
    {
        using var sandbox = new Sandbox();
        var configuration = Configuration(sandbox.Root);
        var command = configuration.Projects[0].Commands[0];
        command.Kind = kind; command.TcpProbe = null;
        var node = JsonSerializer.SerializeToNode(configuration)!;
        node["Projects"]![0]!["Commands"]![0]!["AutoRestartEnabled"] = false;
        await File.WriteAllTextAsync(sandbox.Store.FilePath, node.ToJsonString());
        var loaded = await sandbox.Store.LoadAsync();
        Assert.IsFalse(loaded.Projects[0].Commands[0].AutoRestartEnabled);
        await sandbox.Store.SaveAsync(loaded);
        Assert.DoesNotContain("AutoRestartEnabled", await File.ReadAllTextAsync(sandbox.Store.FilePath));
    }

    private static WorkspaceConfiguration Configuration(string root) => new()
    {
        Projects = [new() { Name = "合成项目", Directory = root, Commands = [new() {
            Name = "合成服务", Command = "exit 7", WorkingDirectory = root, Kind = CommandKind.Service,
            TcpProbe = new() { Port = 12345 } }] }]
    };

    private static async Task RejectUnchanged(Sandbox sandbox, string json)
    {
        await File.WriteAllTextAsync(sandbox.Store.FilePath, json);
        var bytes = await File.ReadAllBytesAsync(sandbox.Store.FilePath);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => sandbox.Store.LoadAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WorkspaceStore.ReadFileAsync(sandbox.Store.FilePath));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(sandbox.Store.FilePath));
        Assert.HasCount(1, Directory.GetFiles(sandbox.Root));
    }

    private sealed class Sandbox : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "TalosDesk.RecoveryConfiguration", Guid.NewGuid().ToString("N"));
        internal WorkspaceStore Store { get; }
        internal Sandbox() { Directory.CreateDirectory(Root); Store = new(Path.Combine(Root, "workspace.json")); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
