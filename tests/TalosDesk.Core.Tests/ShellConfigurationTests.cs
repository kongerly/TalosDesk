using System.Text.Json;
using System.Text.Json.Nodes;
using TalosDesk.Core.Configuration;

namespace TalosDesk.Core.Tests;

[TestClass]
public sealed class ShellConfigurationTests
{
    [TestMethod]
    [DataRow(1)] [DataRow(2)] [DataRow(3)] [DataRow(4)] [DataRow(5)] [DataRow(6)]
    public async Task MissingShellDefaultsToPowerShellWithoutRewritingAndSaveUpgrades(int schema)
    {
        using var sandbox = new Sandbox();
        var configuration = sandbox.Configuration;
        configuration.SchemaVersion = schema;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(configuration);
        await File.WriteAllBytesAsync(sandbox.Store.FilePath, bytes);
        var loaded = await sandbox.Store.LoadAsync();
        Assert.AreEqual(CommandShell.PowerShell, loaded.Projects[0].Commands[0].Shell);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(sandbox.Store.FilePath));
        await sandbox.Store.SaveAsync(loaded);
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(sandbox.Store.FilePath))!;
        Assert.AreEqual(6, saved["SchemaVersion"]!.GetValue<int>());
        Assert.IsFalse(saved["Projects"]![0]!["Commands"]![0]!.AsObject().ContainsKey("Shell"));
    }

    [TestMethod]
    [DataRow("null")] [DataRow("true")] [DataRow("\"Cmd\"")] [DataRow("2")] [DataRow("-1")]
    [DataRow("1.5")] [DataRow("{}")] [DataRow("[]")] [DataRow("1,\"Shell\":0")]
    public async Task InvalidShellLoadAndImportPreserveBytes(string field)
    {
        using var sandbox = new Sandbox();
        var json = JsonSerializer.Serialize(sandbox.Configuration).Replace("\"Kind\":0", "\"Kind\":0,\"Shell\":" + field);
        await File.WriteAllTextAsync(sandbox.Store.FilePath, json);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => sandbox.Store.LoadAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WorkspaceStore.ReadFileAsync(sandbox.Store.FilePath));
        Assert.AreEqual(json, await File.ReadAllTextAsync(sandbox.Store.FilePath));
    }

    [TestMethod]
    [DataRow(1)] [DataRow(2)] [DataRow(3)] [DataRow(4)] [DataRow(5)]
    public async Task OldSchemasRejectEvenDefaultShellField(int schema)
    {
        using var sandbox = new Sandbox();
        var configuration = sandbox.Configuration;
        configuration.SchemaVersion = schema;
        var json = JsonSerializer.Serialize(configuration).Replace("\"Kind\":0", "\"Kind\":0,\"Shell\":0");
        await File.WriteAllTextAsync(sandbox.Store.FilePath, json);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => sandbox.Store.LoadAsync());
        Assert.AreEqual(json, await File.ReadAllTextAsync(sandbox.Store.FilePath));
    }

    [TestMethod]
    public async Task CmdSurvivesExportProjectionAndInvalidSaveDoesNotReplaceWorkspace()
    {
        using var sandbox = new Sandbox();
        var configuration = sandbox.Configuration;
        var command = configuration.Projects[0].Commands[0];
        command.Shell = CommandShell.Cmd;
        command.Command = "echo 合成& exit /b 0";
        await sandbox.Store.SaveAsync(configuration);
        var bytes = await File.ReadAllBytesAsync(sandbox.Store.FilePath);
        var projection = WorkspaceStore.CreateExportProjection(configuration);
        Assert.AreEqual(CommandShell.Cmd, projection.Projects[0].Commands[0].Shell);
        projection.Projects[0].Commands[0].Shell = CommandShell.PowerShell;
        Assert.AreEqual(CommandShell.Cmd, command.Shell);
        await sandbox.Store.WriteExportFileAsync(Path.Combine(sandbox.Root, "export.json"), configuration);
        Assert.AreEqual(CommandShell.Cmd, (await WorkspaceStore.ReadFileAsync(Path.Combine(sandbox.Root, "export.json"))).Projects[0].Commands[0].Shell);
        command.Command = "echo one\necho two";
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => sandbox.Store.SaveAsync(configuration));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(sandbox.Store.FilePath));
    }

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "TalosDesk-Shell-Config-" + Guid.NewGuid().ToString("N"));
        public WorkspaceStore Store { get; }
        public WorkspaceConfiguration Configuration => new() { Projects = [new() { Name = "合成项目", Directory = Root, Commands = [new() { Name = "合成命令", Command = "exit 0", WorkingDirectory = Root }] }] };
        public Sandbox() { Directory.CreateDirectory(Root); Store = new(Path.Combine(Root, "workspace.json")); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
