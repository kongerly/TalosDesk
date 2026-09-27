using System.Security.Cryptography;
using System.Text.Json;

namespace TalosDesk.Core.Configuration;

public sealed class WorkspaceStore
{
    public const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public WorkspaceStore(string? filePath = null)
    {
        FilePath = Path.GetFullPath(filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TalosDesk", "workspace.json"));
    }

    public string FilePath { get; }

    public async Task<WorkspaceSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath)) return new WorkspaceSnapshot(new WorkspaceConfiguration(), WorkspaceRevision.Missing);
        var bytes = await File.ReadAllBytesAsync(FilePath, cancellationToken).ConfigureAwait(false);
        var configuration = JsonSerializer.Deserialize<WorkspaceConfiguration>(bytes, JsonOptions);
        NormalizeAndValidate(configuration);
        var revision = new WorkspaceRevision(true, Convert.ToHexString(SHA256.HashData(bytes)));
        return new WorkspaceSnapshot(configuration!, revision);
    }

    public async Task<WorkspaceRevision> GetRevisionAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath)) return WorkspaceRevision.Missing;
        await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 16_384, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new WorkspaceRevision(true, Convert.ToHexString(hash));
    }

    public async Task<WorkspaceConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        return (await LoadSnapshotAsync(cancellationToken).ConfigureAwait(false)).Configuration;
    }

    public static async Task<WorkspaceConfiguration> ReadFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, useAsync: true);
        var configuration = await JsonSerializer.DeserializeAsync<WorkspaceConfiguration>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        NormalizeAndValidate(configuration);
        return configuration!;
    }

    public static async Task WriteFileAsync(string filePath, WorkspaceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        PrepareForWrite(configuration);
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? throw new InvalidOperationException("The workspace path has no parent directory.");
        Directory.CreateDirectory(directory);
        await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 16_384, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, configuration, JsonOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(WorkspaceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        PrepareForWrite(configuration);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(FilePath) ?? throw new InvalidOperationException("The workspace path has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 16_384, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, configuration, JsonOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, FilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static void PrepareForWrite(WorkspaceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.SchemaVersion = CurrentSchemaVersion;
        NormalizeAndValidate(configuration);
    }

    private static void NormalizeAndValidate(WorkspaceConfiguration? configuration)
    {
        if (configuration is null || configuration.SchemaVersion is not (1 or CurrentSchemaVersion) || configuration.Projects is null)
        {
            throw new InvalidDataException("The TalosDesk workspace file is empty or uses an unsupported schema version.");
        }

        var legacyWorkspace = configuration.SchemaVersion == 1;
        configuration.SchemaVersion = CurrentSchemaVersion;
        if (legacyWorkspace)
        {
            foreach (var project in configuration.Projects)
            {
                if (project is not null) project.Groups = [];
            }
        }

        if (configuration.Projects.Any(project => project is null || project.Commands is null || project.Groups is null ||
                string.IsNullOrWhiteSpace(project.Name) || string.IsNullOrWhiteSpace(project.Directory)))
        {
            throw new InvalidDataException("The TalosDesk workspace contains an incomplete project.");
        }

        if (configuration.Projects.Select(project => project.Id).Distinct().Count() != configuration.Projects.Count)
        {
            throw new InvalidDataException("The TalosDesk workspace contains duplicate project IDs.");
        }

        try
        {
            var projectDirectories = configuration.Projects.Select(project => Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.Directory)));
            if (projectDirectories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != configuration.Projects.Count)
            {
                throw new InvalidDataException("The TalosDesk workspace contains duplicate project folders.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidDataException("The TalosDesk workspace contains an invalid project folder.", exception);
        }

        var commandIds = new HashSet<Guid>();
        var groupIds = new HashSet<Guid>();
        foreach (var project in configuration.Projects)
        {
            if (project.Commands.Any(command => command is null || string.IsNullOrWhiteSpace(command.Name) || string.IsNullOrWhiteSpace(command.Command) || string.IsNullOrWhiteSpace(command.WorkingDirectory)))
            {
                throw new InvalidDataException($"Project '{project.Name}' contains an incomplete command.");
            }

            if (project.Commands.Select(command => command.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != project.Commands.Count)
            {
                throw new InvalidDataException($"Project '{project.Name}' contains duplicate command names.");
            }

            foreach (var command in project.Commands)
            {
                if (!commandIds.Add(command.Id)) throw new InvalidDataException("The TalosDesk workspace contains duplicate command IDs.");
                if (!Enum.IsDefined(command.Kind)) throw new InvalidDataException($"Command '{command.Name}' has an unsupported run type.");
            }

            if (project.Groups.Any(group => group is null || string.IsNullOrWhiteSpace(group.Name) || group.CommandIds is null))
            {
                throw new InvalidDataException($"Project '{project.Name}' contains an incomplete command group.");
            }

            if (project.Groups.Select(group => group.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != project.Groups.Count)
            {
                throw new InvalidDataException($"Project '{project.Name}' contains duplicate command group names.");
            }

            var projectCommands = project.Commands.ToDictionary(command => command.Id);
            foreach (var group in project.Groups)
            {
                if (!groupIds.Add(group.Id)) throw new InvalidDataException("The TalosDesk workspace contains duplicate command group IDs.");
                if (!Enum.IsDefined(group.ExecutionMode)) throw new InvalidDataException($"Command group '{group.Name}' has an unsupported execution mode.");
                if (group.CommandIds.Distinct().Count() != group.CommandIds.Count)
                {
                    throw new InvalidDataException($"Command group '{group.Name}' contains duplicate command references.");
                }

                if (group.CommandIds.Any(commandId => !projectCommands.ContainsKey(commandId)))
                {
                    throw new InvalidDataException($"Command group '{group.Name}' references a command outside its project.");
                }

                if (group.ExecutionMode == CommandGroupExecutionMode.Sequential &&
                    group.CommandIds.Any(commandId => projectCommands[commandId].Kind == CommandKind.Service))
                {
                    throw new InvalidDataException($"Sequential command group '{group.Name}' contains a service command.");
                }
            }
        }
    }
}

public readonly record struct WorkspaceRevision(bool Exists, string Sha256)
{
    public static WorkspaceRevision Missing { get; } = new(false, string.Empty);
}

public sealed record WorkspaceSnapshot(WorkspaceConfiguration Configuration, WorkspaceRevision Revision);
