using TalosDesk.Core.Configuration;

namespace TalosDesk.Core.Tests;

[TestClass]
public sealed class WorkspaceStoreTests
{
    [TestMethod]
    public async Task SavesAndReloadsWorkspaceWithoutChangingIds()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TalosDesk-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "workspace.json");
        var project = new ProjectDefinition
        {
            Name = "Sample",
            Directory = directory,
            Commands = [new CommandDefinition { Name = "Build", Command = "dotnet build", WorkingDirectory = directory }]
        };
        var store = new WorkspaceStore(path);

        await store.SaveAsync(new WorkspaceConfiguration { Projects = [project] });
        var reloaded = await store.LoadAsync();

        Assert.HasCount(1, reloaded.Projects);
        Assert.AreEqual(project.Id, reloaded.Projects[0].Id);
        Assert.AreEqual(project.Commands[0].Id, reloaded.Projects[0].Commands[0].Id);
        Assert.AreEqual("dotnet build", reloaded.Projects[0].Commands[0].Command);

        Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public async Task RejectsUnsupportedSchemaAndDuplicateCommandNames()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TalosDesk-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var invalidPath = Path.Combine(directory, "unsupported.json");
            await File.WriteAllTextAsync(invalidPath, "{\"SchemaVersion\":99,\"Projects\":[]}");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WorkspaceStore.ReadFileAsync(invalidPath));

            var projectDirectory = Path.Combine(directory, "project");
            var duplicate = new WorkspaceConfiguration
            {
                Projects = [new ProjectDefinition
                {
                    Name = "Sample",
                    Directory = projectDirectory,
                    Commands =
                    [
                        new CommandDefinition { Name = "Build", Command = "dotnet build", WorkingDirectory = projectDirectory },
                        new CommandDefinition { Name = "build", Command = "dotnet test", WorkingDirectory = projectDirectory }
                    ]
                }]
            };

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WorkspaceStore.WriteFileAsync(Path.Combine(directory, "duplicate.json"), duplicate));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ConcurrentSavesLeaveACompleteWorkspaceAndNoTemporaryFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TalosDesk-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new WorkspaceStore(Path.Combine(directory, "workspace.json"));
            var first = new WorkspaceConfiguration { Projects = [new ProjectDefinition { Name = "First", Directory = Path.Combine(directory, "first") }] };
            var second = new WorkspaceConfiguration { Projects = [new ProjectDefinition { Name = "Second", Directory = Path.Combine(directory, "second") }] };

            for (var attempt = 0; attempt < 5; attempt++)
            {
                await Task.WhenAll(store.SaveAsync(first), store.SaveAsync(second));
                var loaded = await store.LoadAsync();
                Assert.HasCount(1, loaded.Projects);
                Assert.IsTrue(loaded.Projects[0].Name is "First" or "Second");
            }

            Assert.HasCount(0, Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
