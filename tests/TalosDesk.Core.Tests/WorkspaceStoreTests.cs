using System.Security.Cryptography;
using System.Text.Json;
using TalosDesk.Core.Configuration;

namespace TalosDesk.Core.Tests;

[TestClass]
public sealed class WorkspaceStoreTests
{
    [TestMethod]
    [DataRow(1, true)]
    [DataRow(1, false)]
    [DataRow(2, true)]
    [DataRow(2, false)]
    [DataRow(3, true)]
    [DataRow(3, false)]
    [DataRow(4, true)]
    [DataRow(4, false)]
    public async Task RejectsEmptyIdsDuringLoadAndImportWithoutChangingFile(int schemaVersion, bool emptyProjectId)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TalosDesk-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "invalid-id.json");
            var command = new CommandDefinition { Name = "Check", Command = "Write-Output 'ok'", WorkingDirectory = directory };
            var project = new ProjectDefinition { Name = "Sample", Directory = directory, Commands = [command] };
            if (emptyProjectId) project.Id = Guid.Empty;
            else command.Id = Guid.Empty;
            var configuration = new WorkspaceConfiguration { SchemaVersion = schemaVersion, Projects = [project] };
            var originalBytes = JsonSerializer.SerializeToUtf8Bytes(configuration);
            await File.WriteAllBytesAsync(path, originalBytes);
            var store = new WorkspaceStore(path);
            var expectedMessage = emptyProjectId ? "项目 ID" : "命令 ID";

            var loadError = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.LoadAsync());
            StringAssert.Contains(loadError.Message, expectedMessage);
            var snapshotError = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.LoadSnapshotAsync());
            StringAssert.Contains(snapshotError.Message, expectedMessage);
            var importError = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WorkspaceStore.ReadFileAsync(path));
            StringAssert.Contains(importError.Message, expectedMessage);

            CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(path));
            Assert.HasCount(1, Directory.GetFiles(directory));
            Assert.HasCount(0, Directory.GetDirectories(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RejectsEmptyIdsDuringSaveAndExportWithoutReplacingExistingFiles(bool emptyProjectId)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TalosDesk-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new WorkspaceStore(Path.Combine(directory, "workspace.json"));
            var exportPath = Path.Combine(directory, "export.json");
            var command = new CommandDefinition { Name = "Check", Command = "Write-Output 'ok'", WorkingDirectory = directory };
            var project = new ProjectDefinition { Name = "Sample", Directory = directory, Commands = [command] };
            var configuration = new WorkspaceConfiguration { Projects = [project] };
            await store.SaveAsync(configuration);
            await WorkspaceStore.WriteExportFileAsync(exportPath, configuration);
            var workspaceBytes = await File.ReadAllBytesAsync(store.FilePath);
            var exportBytes = await File.ReadAllBytesAsync(exportPath);
            if (emptyProjectId) project.Id = Guid.Empty;
            else command.Id = Guid.Empty;

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.SaveAsync(configuration));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WorkspaceStore.WriteExportFileAsync(exportPath, configuration));

            CollectionAssert.AreEqual(workspaceBytes, await File.ReadAllBytesAsync(store.FilePath));
            CollectionAssert.AreEqual(exportBytes, await File.ReadAllBytesAsync(exportPath));
            Assert.HasCount(0, Directory.GetFiles(directory, "*.tmp"));
            var loaded = await store.LoadAsync();
            Assert.AreNotEqual(Guid.Empty, loaded.Projects[0].Id);
            Assert.AreNotEqual(Guid.Empty, loaded.Projects[0].Commands[0].Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

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
    public async Task MigratesSchemaOneWorkspaceToSchemaFourWithEmptyGroupsAndVariables()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TalosDesk-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "legacy.json");
            await File.WriteAllTextAsync(path, $$"""
                {
                  "SchemaVersion": 1,
                  "Projects": [
                    {
                      "Id": "{{Guid.NewGuid()}}",
                      "Name": "Legacy",
                      "Directory": "{{directory.Replace("\\", "\\\\")}}",
                      "Commands": [
                        {
                          "Id": "{{Guid.NewGuid()}}",
                          "Name": "Check",
                          "Command": "Write-Output 'ok'",
                          "WorkingDirectory": "{{directory.Replace("\\", "\\\\")}}"
                        }
                      ]
                    }
                  ]
                }
                """);

            var originalBytes = await File.ReadAllBytesAsync(path);
            var loaded = await WorkspaceStore.ReadFileAsync(path);

            Assert.AreEqual(WorkspaceStore.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.HasCount(0, loaded.Projects[0].Groups);
            Assert.HasCount(0, loaded.Projects[0].Commands[0].EnvironmentVariables);
            CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(path));
            await new WorkspaceStore(path).SaveAsync(loaded);
            Assert.IsTrue((await File.ReadAllTextAsync(path)).Contains("\"SchemaVersion\": 4", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task SavesGroupMembershipAndOrderAcrossReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TalosDesk-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var first = new CommandDefinition { Name = "First", Command = "exit 0", WorkingDirectory = directory };
            var second = new CommandDefinition { Name = "Second", Command = "exit 0", WorkingDirectory = directory };
            var shared = new CommandDefinition { Name = "Shared", Command = "exit 0", WorkingDirectory = directory };
            var parallel = new CommandGroupDefinition
            {
                Name = "Parallel",
                ExecutionMode = CommandGroupExecutionMode.Parallel,
                CommandIds = [shared.Id, first.Id]
            };
            var sequential = new CommandGroupDefinition
            {
                Name = "Sequential",
                ExecutionMode = CommandGroupExecutionMode.Sequential,
                CommandIds = [second.Id, shared.Id]
            };
            var project = new ProjectDefinition
            {
                Name = "Sample",
                Directory = directory,
                Commands = [first, second, shared],
                Groups = [parallel, sequential]
            };
            var store = new WorkspaceStore(Path.Combine(directory, "workspace.json"));

            await store.SaveAsync(new WorkspaceConfiguration { Projects = [project] });
            var reloaded = await store.LoadAsync();

            Assert.AreEqual(WorkspaceStore.CurrentSchemaVersion, reloaded.SchemaVersion);
            CollectionAssert.AreEqual(new[] { parallel.Id, sequential.Id }, reloaded.Projects[0].Groups.Select(group => group.Id).ToArray());
            CollectionAssert.AreEqual(new[] { shared.Id, first.Id }, reloaded.Projects[0].Groups[0].CommandIds);
            CollectionAssert.AreEqual(new[] { second.Id, shared.Id }, reloaded.Projects[0].Groups[1].CommandIds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task RejectsInvalidGroupDefinitions()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TalosDesk-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var service = new CommandDefinition { Name = "Server", Command = "exit 0", WorkingDirectory = directory, Kind = CommandKind.Service };
            var project = new ProjectDefinition { Name = "Sample", Directory = directory, Commands = [service] };
            var path = Path.Combine(directory, "invalid-group.json");

            project.Groups = [new CommandGroupDefinition { Name = "Broken", CommandIds = [Guid.NewGuid()] }];
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WorkspaceStore.WriteExportFileAsync(path, new WorkspaceConfiguration { Projects = [project] }));

            project.Groups = [new CommandGroupDefinition { Name = "Repeated", CommandIds = [service.Id, service.Id] }];
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WorkspaceStore.WriteExportFileAsync(path, new WorkspaceConfiguration { Projects = [project] }));

            project.Groups = [new CommandGroupDefinition { Name = "Sequence", ExecutionMode = CommandGroupExecutionMode.Sequential, CommandIds = [service.Id] }];
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WorkspaceStore.WriteExportFileAsync(path, new WorkspaceConfiguration { Projects = [project] }));

            var duplicateGroupId = Guid.NewGuid();
            project.Groups =
            [
                new CommandGroupDefinition { Id = duplicateGroupId, Name = "First", CommandIds = [service.Id] },
                new CommandGroupDefinition { Id = duplicateGroupId, Name = "Second", CommandIds = [] }
            ];
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WorkspaceStore.WriteExportFileAsync(path, new WorkspaceConfiguration { Projects = [project] }));

            project.Groups =
            [
                new CommandGroupDefinition { Name = "Duplicate", CommandIds = [service.Id] },
                new CommandGroupDefinition { Name = "duplicate", CommandIds = [] }
            ];
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WorkspaceStore.WriteExportFileAsync(path, new WorkspaceConfiguration { Projects = [project] }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void RemovingCommandReferencesPreservesEmptyGroupsAndOtherMemberships()
    {
        var removed = new CommandDefinition { Name = "Removed" };
        var kept = new CommandDefinition { Name = "Kept" };
        var first = new CommandGroupDefinition { Name = "First", CommandIds = [removed.Id, kept.Id] };
        var second = new CommandGroupDefinition { Name = "Second", CommandIds = [removed.Id] };
        var project = new ProjectDefinition { Commands = [removed, kept], Groups = [first, second] };

        CommandGroupOperations.RemoveCommandReferences(project, removed.Id);

        CollectionAssert.AreEqual(new[] { kept.Id }, first.CommandIds);
        Assert.HasCount(0, second.CommandIds);
        Assert.HasCount(2, project.Groups);
    }

    [TestMethod]
    public void FindsSequentialMembershipAndRemapsImportedCommandIdsInOrder()
    {
        var first = new CommandDefinition { Name = "First" };
        var second = new CommandDefinition { Name = "Second" };
        var parallel = new CommandGroupDefinition { Name = "Parallel", CommandIds = [first.Id] };
        var sequential = new CommandGroupDefinition
        {
            Name = "Sequential",
            ExecutionMode = CommandGroupExecutionMode.Sequential,
            CommandIds = [second.Id, first.Id]
        };
        var project = new ProjectDefinition { Commands = [first, second], Groups = [parallel, sequential] };
        var mappedFirst = Guid.NewGuid();
        var mappedSecond = Guid.NewGuid();

        var memberships = CommandGroupOperations.GetSequentialGroupsContaining(project, first.Id);
        var remapped = CommandGroupOperations.RemapCommandIds(sequential.CommandIds,
            new Dictionary<Guid, Guid> { [first.Id] = mappedFirst, [second.Id] = mappedSecond });

        CollectionAssert.AreEqual(new[] { sequential.Id }, memberships.Select(group => group.Id).ToArray());
        CollectionAssert.AreEqual(new[] { mappedSecond, mappedFirst }, remapped);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            CommandGroupOperations.RemapCommandIds([Guid.NewGuid()], new Dictionary<Guid, Guid>()));
    }

    [TestMethod]
    public async Task SavesCommandOrderAndRemovalAcrossReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TalosDesk-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var first = new CommandDefinition { Name = "First", Command = "echo first", WorkingDirectory = directory };
            var second = new CommandDefinition { Name = "Second", Command = "echo second", WorkingDirectory = directory };
            var third = new CommandDefinition { Name = "Third", Command = "echo third", WorkingDirectory = directory };
            var project = new ProjectDefinition { Name = "Sample", Directory = directory, Commands = [first, second, third] };
            var store = new WorkspaceStore(Path.Combine(directory, "workspace.json"));
            await store.SaveAsync(new WorkspaceConfiguration { Projects = [project] });

            project.Commands.Remove(third);
            project.Commands.Insert(0, third);
            project.Commands.Remove(second);
            await store.SaveAsync(new WorkspaceConfiguration { Projects = [project] });

            var reloaded = await store.LoadAsync();
            CollectionAssert.AreEqual(new[] { third.Id, first.Id }, reloaded.Projects[0].Commands.Select(command => command.Id).ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WorkspaceStore.WriteExportFileAsync(Path.Combine(directory, "duplicate.json"), duplicate));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task RevisionChangesWhenWorkspaceContentChangesAndRepresentsMissingFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TalosDesk-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "workspace.json");
            var store = new WorkspaceStore(path);

            Assert.AreEqual(WorkspaceRevision.Missing, await store.GetRevisionAsync());
            await store.SaveAsync(new WorkspaceConfiguration());
            var first = await store.GetRevisionAsync();

            await store.SaveAsync(new WorkspaceConfiguration
            {
                Projects = [new ProjectDefinition { Name = "Changed", Directory = Path.Combine(directory, "project") }]
            });
            var second = await store.GetRevisionAsync();

            Assert.IsTrue(first.Exists);
            Assert.AreNotEqual(first, second);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task SnapshotConfigurationAndRevisionComeFromTheSameFileContent()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TalosDesk-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "workspace.json");
            var store = new WorkspaceStore(path);
            await store.SaveAsync(new WorkspaceConfiguration
            {
                Projects = [new ProjectDefinition { Name = "First", Directory = Path.Combine(directory, "first") }]
            });

            var snapshot = await store.LoadSnapshotAsync();
            Assert.AreEqual("First", snapshot.Configuration.Projects[0].Name);
            Assert.AreEqual(await store.GetRevisionAsync(), snapshot.Revision);

            await store.SaveAsync(new WorkspaceConfiguration
            {
                Projects = [new ProjectDefinition { Name = "Second", Directory = Path.Combine(directory, "second") }]
            });
            Assert.AreNotEqual(await store.GetRevisionAsync(), snapshot.Revision);
            Assert.AreEqual("First", snapshot.Configuration.Projects[0].Name);
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

    [TestMethod]
    public async Task RejectsUnknownFieldsAtEveryLayer()
    {
        using var sandbox = new TemporaryDirectory();
        var valid = await File.ReadAllTextAsync(await WriteValidWorkspace(sandbox.Path));
        var path = Path.Combine(sandbox.Path, "unknown-field.json");
        var cases = new (string Description, string Json)[]
        {
            ("root", Replace(valid, "\"SchemaVersion\": 4", "\"SchemaVersion\": 4, \"Extra\": 1")),
            ("project", Replace(valid, "\"Groups\": [", "\"Nickname\": \"x\", \"Groups\": [")),
            ("command", Replace(valid, "\"Kind\": 1", "\"Kindy\": 1, \"Kind\": 1")),
            ("group", Replace(valid, "\"CommandIds\": [", "\"Note\": \"x\", \"CommandIds\": [")),
            ("probe-typo", Replace(valid, "\"Port\": 12345", "\"TcpProbee\": { \"Port\": 12345 }, \"Port\": 12345")),
            ("variable", Replace(valid, "\"EnvironmentVariables\": [",
                "\"EnvironmentVariables\": [ { \"Name\": \"A\", \"IsSensitive\": false, \"Value\": \"\", \"Note\": \"x\" },")),
            ("lowercase-projects", Replace(valid, "\"Projects\"", "\"projects\"")),
            ("camel-projects", Replace(valid, "\"Projects\"", "\"projects\"").Replace("\"SchemaVersion\"", "\"schemaVersion\""))
        };

        foreach (var (description, json) in cases)
        {
            Assert.AreNotEqual(valid, json, description);
            await File.WriteAllTextAsync(path, json);
            var digest = await Sha256Async(path);
            var loadError = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new WorkspaceStore(path).LoadAsync());
            StringAssert.Contains(loadError.Message, "unsupported field name");
            var importError = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WorkspaceStore.ReadFileAsync(path));
            StringAssert.Contains(importError.Message, "unsupported field name");
            Assert.AreEqual(digest, await Sha256Async(path), description);
        }
    }

    [TestMethod]
    public async Task RejectsDuplicateKeysAtEveryLayerIncludingRootAndKind()
    {
        using var sandbox = new TemporaryDirectory();
        var valid = await File.ReadAllTextAsync(await WriteValidWorkspace(sandbox.Path));
        var path = Path.Combine(sandbox.Path, "duplicate-key.json");
        var cases = new (string Description, string Json)[]
        {
            ("schema-version", Replace(valid, "\"SchemaVersion\": 4,", "\"SchemaVersion\": 4,\n  \"SchemaVersion\": 4,")),
            ("projects", Replace(valid, "\"Projects\": [", "\"Projects\": [],\n  \"Projects\": [")),
            ("project-name", Replace(valid, "\"Name\": \"合成项目\"", "\"Name\": \"a\", \"Name\": \"合成项目\"")),
            ("commands", Replace(valid, "\"Commands\": [", "\"Commands\": [], \"Commands\": [")),
            ("kind", Replace(valid, "\"Kind\": 1", "\"Kind\": 0, \"Kind\": 1")),
            ("command-id", Replace(valid, "\"Id\": \"11111111-1111-1111-1111-111111111101\"",
                "\"Id\": \"11111111-1111-1111-1111-111111111101\",\n          \"Id\": \"11111111-1111-1111-1111-111111111199\"")),
            ("nested-var-value", Replace(valid, "{ \"Name\": \"A\", \"IsSensitive\": false, \"Value\": \"\" }",
                "{ \"Name\": \"A\", \"IsSensitive\": false, \"Value\": \"\", \"Value\": \"\" }")),
            ("probe-port", Replace(valid, "\"Port\": 12345", "\"Port\": 12345, \"Port\": 12345")),
            ("group-member", Replace(valid, "\"CommandIds\": [ \"11111111-1111-1111-1111-111111111101\" ]",
                "\"CommandIds\": [ \"11111111-1111-1111-1111-111111111101\" ],\n        \"CommandIds\": [ \"11111111-1111-1111-1111-111111111101\" ]"))
        };

        foreach (var (description, json) in cases)
        {
            Assert.AreNotEqual(valid, json, description);
            await File.WriteAllTextAsync(path, json);
            var digest = await Sha256Async(path);
            var loadError = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new WorkspaceStore(path).LoadAsync());
            StringAssert.Contains(loadError.Message, "duplicate field");
            var importError = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WorkspaceStore.ReadFileAsync(path));
            StringAssert.Contains(importError.Message, "duplicate field");
            Assert.AreEqual(digest, await Sha256Async(path), description);
        }
    }

    /// <summary>替换失败时立即失败，避免样例退化为原文件而让测试失去意义。</summary>
    private static string Replace(string source, string oldValue, string newValue)
    {
        var result = source.Replace(oldValue, newValue, StringComparison.Ordinal);
        Assert.AreNotEqual(source, result, $"未能替换样例：{oldValue}");
        return result;
    }

    [TestMethod]
    public async Task RejectsMalformedWorkspaceFilesAsInvalidDataWithoutRewriting()
    {
        using var sandbox = new TemporaryDirectory();
        var path = Path.Combine(sandbox.Path, "malformed.json");
        // Json 解析错误必须保留 JsonException 作为内部异常：上层据此区分损坏 JSON 与语义拒绝。
        var cases = new (string Json, bool ExpectsJsonMessage)[]
        {
            ("{", true),
            ("{\"SchemaVersion\":4,\"Projects\":[", true),
            ("{\"SchemaVersion\":\"four\",\"Projects\":[]}", true),
            ("{\"SchemaVersion\":4,\"Projects\":{}}", true),
            ("null", false),
            ("{\"SchemaVersion\":99,\"Projects\":[]}", false),
            ("{\"SchemaVersion\":4,\"Projects\":null}", false)
        };

        foreach (var (json, expectsJsonMessage) in cases)
        {
            await File.WriteAllTextAsync(path, json);
            var loadError = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new WorkspaceStore(path).LoadAsync());
            var importError = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WorkspaceStore.ReadFileAsync(path));
            if (expectsJsonMessage)
            {
                StringAssert.Contains(loadError.Message, "JSON");
                Assert.IsInstanceOfType<JsonException>(loadError.InnerException);
            }

            StringAssert.Contains(importError.Message, loadError.Message);
            Assert.AreEqual(json, await File.ReadAllTextAsync(path));
        }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("\r\n\t")]
    public async Task RejectsEmptyAndWhitespaceWorkspaceFilesWithoutRewriting(string content)
    {
        using var sandbox = new TemporaryDirectory();
        var path = Path.Combine(sandbox.Path, "empty.json");
        // 空文件同样不得被读取路径改写，也不得留下临时文件。
        await AssertRejectedAndFileUnchanged(path, content, "空白文件必须被拒绝且原文件不变");
    }

    /// <summary>
    /// 读取与导入共用一条契约：任何拒绝都不改写原文件。逐个拒绝用例都经由同一 helper 断言 SHA-256 未变，
    /// 避免"用户以为改了、其实没生效"或读取失败被悄悄改写成另一份配置。
    /// </summary>
    [TestMethod]
    public async Task EveryRejectionKeepsTheOriginalFileByteForByte()
    {
        using var sandbox = new TemporaryDirectory();
        var valid = await File.ReadAllTextAsync(await WriteValidWorkspace(sandbox.Path));
        var path = Path.Combine(sandbox.Path, "rejected.json");
        var cases = new (string Description, string Json)[]
        {
            ("unknown-root", Replace(valid, "\"SchemaVersion\": 4", "\"SchemaVersion\": 4, \"Foo\": 1")),
            ("unknown-project", Replace(valid, "\"Groups\": [", "\"Nickname\": \"x\", \"Groups\": [")),
            ("unknown-command", Replace(valid, "\"Kind\": 1", "\"Commnad\": \"x\", \"Kind\": 1")),
            ("unknown-group", Replace(valid, "\"CommandIds\": [", "\"Note\": \"x\", \"CommandIds\": [")),
            ("unknown-probe", Replace(valid, "\"TcpProbe\": {", "\"TcpProbe\": { \"TcpProbee\": 1,")),
            ("duplicate-root-projects", Replace(valid, "\"Projects\": [", "\"Projects\": [],\n  \"Projects\": [")),
            ("duplicate-command-kind", Replace(valid, "\"Kind\": 1", "\"Kind\": 0, \"Kind\": 1")),
            ("duplicate-probe", Replace(valid, "\"Port\": 12345", "\"Port\": 12345, \"Port\": 12345")),
            ("duplicate-variable", Replace(valid, "{ \"Name\": \"A\", \"IsSensitive\": false, \"Value\": \"\" }",
                "{ \"Name\": \"A\", \"IsSensitive\": false, \"Value\": \"\", \"Value\": \"\" }")),
            ("truncated", valid[..40]),
            ("empty", string.Empty),
            ("null-literal", "null")
        };

        foreach (var (description, json) in cases)
        {
            await AssertRejectedAndFileUnchanged(path, json, description);
        }
    }

    /// <summary>
    /// 读取与导入的拒绝场景共用同一断言：SHA-256 未变即原文件未被改写。
    /// </summary>
    private static async Task AssertRejectedAndFileUnchanged(string path, string json, string description = "")
    {
        await File.WriteAllTextAsync(path, json);
        var digest = await Sha256Async(path);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new WorkspaceStore(path).LoadAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => WorkspaceStore.ReadFileAsync(path));

        Assert.AreEqual(digest, await Sha256Async(path), description);
        Assert.HasCount(0, Directory.GetFiles(System.IO.Path.GetDirectoryName(path)!, "*.tmp"), description);
    }

    private static async Task<string> Sha256Async(string path)
    {
        using var stream = System.IO.File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static async Task<string> WriteValidWorkspace(string directory)
    {
        var path = Path.Combine(directory, "valid.json");
        await File.WriteAllTextAsync(path, $$"""
            {
              "SchemaVersion": 4,
              "Projects": [
                {
                  "Id": "11111111-1111-1111-1111-111111111111",
                  "Name": "合成项目",
                  "Directory": "{{directory.Replace("\\", "\\\\")}}",
                  "Commands": [
                    {
                      "Id": "11111111-1111-1111-1111-111111111101",
                      "Name": "合成服务",
                      "Purpose": "占位",
                      "Command": "exit 0",
                      "WorkingDirectory": "{{directory.Replace("\\", "\\\\")}}",
                      "Kind": 1,
                      "EnvironmentVariables": [ { "Name": "A", "IsSensitive": false, "Value": "" } ],
                      "TcpProbe": { "Address": "127.0.0.1", "Port": 12345, "IntervalSeconds": 2, "ConnectTimeoutSeconds": 1, "StartupTimeoutSeconds": 120, "FailureThreshold": 3 }
                    }
                  ],
                  "Groups": [ { "Id": "11111111-1111-1111-1111-111111111190", "Name": "分组", "ExecutionMode": 0, "CommandIds": [ "11111111-1111-1111-1111-111111111101" ] } ]
                }
              ]
            }
            """);
        await new WorkspaceStore(path).LoadAsync();
        return path;
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

    [TestMethod]
    public async Task GuardedSavesReturnTheRevisionOfTheExactWrittenBytes()
    {
        using var sandbox = new TemporaryDirectory();
        var path = Path.Combine(sandbox.Path, "工作区.json");
        var store = new WorkspaceStore(path);
        var configuration = new WorkspaceConfiguration
        {
            SchemaVersion = 1,
            Projects = [new() { Name = "中文项目 🌱", Directory = sandbox.Path }]
        };

        var first = await store.SaveAsync(configuration, WorkspaceRevision.Missing);
        Assert.AreEqual(Revision(await File.ReadAllBytesAsync(path)), first);
        Assert.AreEqual(WorkspaceStore.CurrentSchemaVersion, (await store.LoadAsync()).SchemaVersion);

        configuration.Projects[0].Name = "修改后";
        var second = await store.SaveAsync(configuration, first);
        Assert.AreNotEqual(first, second);
        Assert.AreEqual(Revision(await File.ReadAllBytesAsync(path)), second);
        Assert.HasCount(0, Directory.GetFiles(sandbox.Path, "*.tmp"));
    }

    [TestMethod]
    public async Task ExternalReplacementAfterCommitDoesNotBecomeTheSavedRevision()
    {
        using var sandbox = new TemporaryDirectory();
        var path = Path.Combine(sandbox.Path, "workspace.json");
        var externalPath = Path.Combine(sandbox.Path, "external.json");
        await new WorkspaceStore(externalPath).SaveAsync(new()
        {
            Projects = [new() { Name = "外部版本", Directory = sandbox.Path }]
        });
        var externalBytes = await File.ReadAllBytesAsync(externalPath);
        byte[]? committedBytes = null;
        var store = new WorkspaceStore(path, null, () =>
        {
            committedBytes = File.ReadAllBytes(path);
            File.Move(externalPath, path, overwrite: true);
        });

        var saved = await store.SaveAsync(new(), WorkspaceRevision.Missing);
        Assert.IsNotNull(committedBytes);
        Assert.AreEqual(Revision(committedBytes), saved);
        Assert.AreNotEqual(await store.GetRevisionAsync(), saved);

        await Assert.ThrowsExactlyAsync<WorkspaceChangedException>(() => store.SaveAsync(new(), saved));
        CollectionAssert.AreEqual(externalBytes, await File.ReadAllBytesAsync(path));
        Assert.HasCount(0, Directory.GetFiles(sandbox.Path, "*.tmp"));
    }

    [TestMethod]
    [DataRow("rewrite")]
    [DataRow("replace")]
    [DataRow("delete")]
    [DataRow("create")]
    public async Task GuardedSaveChecksForExternalChangesAfterPreparingTemporaryFile(string operation)
    {
        using var sandbox = new TemporaryDirectory();
        var path = Path.Combine(sandbox.Path, "workspace.json");
        var initialStore = new WorkspaceStore(path);
        var expected = operation == "create" ? WorkspaceRevision.Missing : await initialStore.SaveAsync(new());
        var externalPath = Path.Combine(sandbox.Path, "external.json");
        await new WorkspaceStore(externalPath).SaveAsync(new()
        {
            Projects = [new() { Name = "外部版本", Directory = sandbox.Path }]
        });
        var externalBytes = await File.ReadAllBytesAsync(externalPath);
        var prepared = false;
        var store = new WorkspaceStore(path, () =>
        {
            // 外部修改发生于临时 JSON 已完整写入后，不能靠保存入口的一次检查通过用例。
            var temporaryPath = Directory.GetFiles(sandbox.Path, "workspace.json.*.tmp").Single();
            using var document = JsonDocument.Parse(File.ReadAllBytes(temporaryPath));
            prepared = true;
            if (operation == "delete") File.Delete(path);
            else if (operation == "replace") File.Move(externalPath, path, overwrite: true);
            else File.WriteAllBytes(path, externalBytes);
        }, null);

        await Assert.ThrowsExactlyAsync<WorkspaceChangedException>(() => store.SaveAsync(new(), expected));
        Assert.IsTrue(prepared);
        if (operation == "delete") Assert.IsFalse(File.Exists(path), "保存不能重建外部已删除的工作区。");
        else CollectionAssert.AreEqual(externalBytes, await File.ReadAllBytesAsync(path));
        Assert.HasCount(0, Directory.GetFiles(sandbox.Path, "*.tmp"));
    }

    [TestMethod]
    public async Task ConcurrentGuardedSavesRejectTheStaleRevision()
    {
        using var sandbox = new TemporaryDirectory();
        var store = new WorkspaceStore(Path.Combine(sandbox.Path, "workspace.json"));
        var expected = await store.SaveAsync(new());
        var first = store.SaveAsync(new()
        {
            Projects = [new() { Name = "First", Directory = sandbox.Path }]
        }, expected);
        var second = store.SaveAsync(new()
        {
            Projects = [new() { Name = "Second", Directory = sandbox.Path }]
        }, expected);

        await Assert.ThrowsExactlyAsync<WorkspaceChangedException>(() => Task.WhenAll(first, second));
        var saves = new[] { first, second };
        Assert.HasCount(1, saves.Where(task => task.IsCompletedSuccessfully));
        Assert.HasCount(1, saves.Where(task => task.IsFaulted));
        Assert.AreEqual(saves.Single(task => task.IsCompletedSuccessfully).Result, await store.GetRevisionAsync());
        Assert.HasCount(0, Directory.GetFiles(sandbox.Path, "*.tmp"));
    }

    [TestMethod]
    public async Task CancellationAfterPreparingTemporaryFilePreservesExistingWorkspace()
    {
        using var sandbox = new TemporaryDirectory();
        var path = Path.Combine(sandbox.Path, "workspace.json");
        var original = await new WorkspaceStore(path).SaveAsync(new());
        var originalBytes = await File.ReadAllBytesAsync(path);
        using var cancellation = new CancellationTokenSource();
        var store = new WorkspaceStore(path, cancellation.Cancel, null);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => store.SaveAsync(new()
        {
            Projects = [new() { Name = "取消的版本", Directory = sandbox.Path }]
        }, original, cancellation.Token));
        CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(path));
        Assert.HasCount(0, Directory.GetFiles(sandbox.Path, "*.tmp"));
    }

    [TestMethod]
    public async Task FailedSaveLeavesTheCallersObjectUnnormalized()
    {
        using var sandbox = new TemporaryDirectory();
        var path = Path.Combine(sandbox.Path, "workspace.json");
        var store = new WorkspaceStore(path);
        await store.SaveAsync(new());
        var bytesBeforeFailedSave = await File.ReadAllBytesAsync(path);

        // 桌面传入的是 UI 绑定的那批实例；写盘失败不能把它们升级成内存与磁盘分叉的 schema 4。
        var command = new CommandDefinition { Name = "Check", Command = "Write-Output 'ok'", WorkingDirectory = sandbox.Path };
        var callerOwned = new WorkspaceConfiguration
        {
            SchemaVersion = 2,
            Projects =
            [
                new ProjectDefinition
                {
                    Name = "旧版项目",
                    Directory = sandbox.Path,
                    Commands = [command],
                    Groups = [new CommandGroupDefinition { Name = "旧分组", CommandIds = [command.Id] }]
                }
            ]
        };
        using (var lockedFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Exception? saveError = null;
            try { await store.SaveAsync(callerOwned); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { saveError = exception; }
            Assert.IsNotNull(saveError, "Replacing a locked workspace must fail.");
        }

        Assert.AreEqual(2, callerOwned.SchemaVersion, "保存失败不能改写调用方对象的 SchemaVersion。");
        Assert.HasCount(1, callerOwned.Projects[0].Groups, "保存失败不能清空调用方对象的 Groups。");
        Assert.AreEqual("旧分组", callerOwned.Projects[0].Groups[0].Name);
        Assert.AreSame(command, callerOwned.Projects[0].Commands[0], "保存入口不得替换调用方持有的实例。");
        CollectionAssert.AreEqual(bytesBeforeFailedSave, await File.ReadAllBytesAsync(path), "写盘失败必须保留磁盘原内容。");
        Assert.HasCount(0, Directory.GetFiles(sandbox.Path, "*.tmp"));
    }

    [TestMethod]
    public async Task SuccessfulSaveUpgradesTheFileWithoutTouchingTheCallersObject()
    {
        using var sandbox = new TemporaryDirectory();
        var path = Path.Combine(sandbox.Path, "workspace.json");
        var store = new WorkspaceStore(path);
        var command = new CommandDefinition { Name = "Check", Command = "Write-Output 'ok'", WorkingDirectory = sandbox.Path };
        var callerOwned = new WorkspaceConfiguration
        {
            SchemaVersion = 1,
            Projects =
            [
                new ProjectDefinition
                {
                    Name = "旧版项目",
                    Directory = sandbox.Path,
                    Commands = [command],
                    Groups = [new CommandGroupDefinition { Name = "旧分组", CommandIds = [command.Id] }]
                }
            ]
        };

        await store.SaveAsync(callerOwned);

        var saved = await store.LoadAsync();
        Assert.AreEqual(WorkspaceStore.CurrentSchemaVersion, saved.SchemaVersion, "写入磁盘的内容仍需升级到 schema 4。");
        Assert.HasCount(0, saved.Projects[0].Groups, "旧工作区保存时按既有规则清空 Groups。");
        Assert.AreEqual(1, callerOwned.SchemaVersion, "成功保存同样不得改写调用方对象。");
        Assert.HasCount(1, callerOwned.Projects[0].Groups);
        Assert.AreEqual(command.Id, saved.Projects[0].Commands[0].Id, "深复制必须保留命令 ID。");
    }

    private static WorkspaceRevision Revision(byte[] bytes) => new(true, Convert.ToHexString(SHA256.HashData(bytes)));

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedSavePreservesExistingWorkspaceAndRemovesTemporaryFile(bool guarded)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TalosDesk-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "workspace.json");
            var store = new WorkspaceStore(path);
            var revision = await store.SaveAsync(new WorkspaceConfiguration
            {
                Projects = [new ProjectDefinition { Name = "Original", Directory = Path.Combine(directory, "original") }]
            });
            var originalBytes = await File.ReadAllBytesAsync(path);

            using (var lockedFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Exception? saveError = null;
                try
                {
                    var replacement = new WorkspaceConfiguration
                    {
                        Projects = [new ProjectDefinition { Name = "Replacement", Directory = Path.Combine(directory, "replacement") }]
                    };
                    if (guarded) await store.SaveAsync(replacement, revision);
                    else await store.SaveAsync(replacement);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    saveError = exception;
                }
                Assert.IsNotNull(saveError, "Replacing a locked workspace must fail.");
            }

            CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(path));
            Assert.AreEqual("Original", (await store.LoadAsync()).Projects.Single().Name);
            Assert.HasCount(0, Directory.GetFiles(directory, "workspace.json.*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
