using System.Collections;
using TalosDesk.Core.Editing;

namespace TalosDesk.Core.Tests;

[TestClass]
public sealed class CommandCompletionProviderTests
{
    [TestMethod]
    public async Task CompletesPathExecutablesAtCommandAndPipelinePositions()
    {
        using var sandbox = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(sandbox.Path, "alpha.exe"), string.Empty);
        File.WriteAllText(Path.Combine(sandbox.Path, "alpha.cmd"), string.Empty);
        File.WriteAllText(Path.Combine(sandbox.Path, "alpha.txt"), string.Empty);
        var unavailablePath = Path.Combine(sandbox.Path, "unavailable");
        var provider = CreateProvider($"{unavailablePath}{Path.PathSeparator}{sandbox.Path}", ".EXE;.CMD");

        var atStart = await provider.GetCompletionsAsync("alp", 3, sandbox.Path);
        var afterPipeline = await provider.GetCompletionsAsync("Get-Date | alp", 14, sandbox.Path);

        CollectionAssert.AreEquivalent(new[] { "alpha.cmd", "alpha.exe" },
            atStart.Select(item => item.InsertText).ToArray());
        CollectionAssert.AreEquivalent(new[] { "alpha.cmd", "alpha.exe" },
            afterPipeline.Select(item => item.InsertText).ToArray());
        Assert.IsTrue(atStart.All(item => item.Kind == CommandCompletionKind.Command));
    }

    [TestMethod]
    public async Task CompletesRelativeUnicodePathsAndQuotesNamesWithSpaces()
    {
        using var sandbox = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(sandbox.Path, "src"));
        Directory.CreateDirectory(Path.Combine(source.FullName, "项目目录"));
        Directory.CreateDirectory(Path.Combine(sandbox.Path, "Project Folder"));
        File.WriteAllText(Path.Combine(source.FullName, "Program.ps1"), string.Empty);
        var provider = CreateProvider(string.Empty, ".EXE");

        var relative = await provider.GetCompletionsAsync(@".\src\Pro", 9, sandbox.Path);
        var unicode = await provider.GetCompletionsAsync(@".\src\项", 7, sandbox.Path);
        var spaced = await provider.GetCompletionsAsync("tool Pro", 8, sandbox.Path);
        var spacedCommand = await provider.GetCompletionsAsync(@".\Pro", 5, sandbox.Path);

        Assert.HasCount(1, relative);
        Assert.HasCount(1, unicode);
        Assert.HasCount(1, spaced);
        Assert.AreEqual(@".\src\Program.ps1", relative[0].InsertText);
        Assert.AreEqual(@".\src\项目目录\", unicode[0].InsertText);
        var spacedItem = spaced[0];
        Assert.AreEqual("\"Project Folder\\\"", spacedItem.InsertText);
        Assert.AreEqual(5, spacedItem.ReplacementStart);
        Assert.AreEqual(3, spacedItem.ReplacementLength);
        Assert.HasCount(1, spacedCommand);
        Assert.AreEqual("& \".\\Project Folder\\\"", spacedCommand[0].InsertText);
    }

    [TestMethod]
    public async Task PreservesExistingQuotesDuringPathCompletion()
    {
        using var sandbox = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(sandbox.Path, "Project Folder"));
        var provider = CreateProvider(string.Empty, ".EXE");
        const string text = "tool \"Pro";

        var result = await provider.GetCompletionsAsync(text, text.Length, sandbox.Path);

        Assert.HasCount(1, result);
        var item = result[0];
        Assert.AreEqual("Project Folder\\", item.InsertText);
        Assert.AreEqual(6, item.ReplacementStart);
    }

    [TestMethod]
    public async Task CompletesEnvironmentVariablesCaseInsensitively()
    {
        var variables = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = "value",
            ["PATHEXT"] = "value",
            ["TEMP"] = "value"
        };
        var provider = new CommandCompletionProvider(() => string.Empty, () => ".EXE", () => variables);

        var result = await provider.GetCompletionsAsync("Write-Output $env:pa", 20, Path.GetTempPath());

        CollectionAssert.AreEquivalent(new[] { "PATH", "PATHEXT" }, result.Select(item => item.InsertText).ToArray());
        Assert.IsTrue(result.All(item => item.ReplacementStart == 18 && item.ReplacementLength == 2));
    }

    [TestMethod]
    public async Task InvalidDirectoriesAndCanceledRequestsReturnSafely()
    {
        var provider = CreateProvider(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), ".EXE");

        var invalid = await provider.GetCompletionsAsync("tool value", 10,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.HasCount(0, invalid);
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
            await provider.GetCompletionsAsync("a", 1, Path.GetTempPath(), cancellation.Token));
    }

    [TestMethod]
    public async Task ResultsAreDeduplicatedSortedAndLimitedWithoutExecutingInput()
    {
        using var sandbox = new TemporaryDirectory();
        var firstPath = Directory.CreateDirectory(Path.Combine(sandbox.Path, "bin-one"));
        var secondPath = Directory.CreateDirectory(Path.Combine(sandbox.Path, "bin-two"));
        for (var index = 0; index < 60; index++)
        {
            File.WriteAllText(Path.Combine(firstPath.FullName, $"tool-{index:D3}.cmd"), string.Empty);
        }

        File.WriteAllText(Path.Combine(secondPath.FullName, "tool-000.cmd"), string.Empty);
        for (var index = 60; index < 120; index++)
        {
            File.WriteAllText(Path.Combine(secondPath.FullName, $"tool-{index:D3}.cmd"), string.Empty);
        }
        var marker = Path.Combine(sandbox.Path, "must-not-exist.txt");
        var provider = CreateProvider($"{firstPath.FullName}{Path.PathSeparator}{secondPath.FullName}", ".CMD");
        var text = $"Set-Content -LiteralPath '{marker}'";

        var commands = await provider.GetCompletionsAsync("tool-", 5, sandbox.Path);
        await provider.GetCompletionsAsync(text, text.Length, sandbox.Path);

        Assert.HasCount(100, commands);
        Assert.AreEqual("tool-000.cmd", commands[0].InsertText);
        Assert.AreEqual("tool-099.cmd", commands[^1].InsertText);
        Assert.IsFalse(File.Exists(marker), "Completion must never execute the edited command.");
    }

    private static CommandCompletionProvider CreateProvider(string path, string pathExtensions) =>
        new(() => path, () => pathExtensions, () => new Hashtable());

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"TalosDesk-completion-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
