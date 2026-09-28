using System.Security.Cryptography;
using System.Text.Json;

namespace TalosDesk.Core.Configuration;

public sealed class WorkspaceStore
{
    public const int CurrentSchemaVersion = 3;
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
        var configuration = DeserializeAndNormalize(bytes);
        var revision = new WorkspaceRevision(true, Convert.ToHexString(SHA256.HashData(bytes)));
        return new WorkspaceSnapshot(configuration, revision);
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
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        return DeserializeAndNormalize(bytes);
    }

    public static async Task WriteExportFileAsync(string filePath, WorkspaceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var export = CreateExportProjection(configuration);
        await WriteAtomicallyAsync(filePath, export, cancellationToken).ConfigureAwait(false);
    }

    public static WorkspaceConfiguration CreateExportProjection(WorkspaceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var clone = JsonSerializer.Deserialize<WorkspaceConfiguration>(JsonSerializer.SerializeToUtf8Bytes(configuration, JsonOptions), JsonOptions)
            ?? throw new InvalidDataException("The TalosDesk workspace is empty.");
        PrepareForWrite(clone);
        return new WorkspaceConfiguration
        {
            Projects = clone.Projects.Select(project => new ProjectDefinition
            {
                Id = project.Id,
                Name = project.Name,
                Directory = project.Directory,
                Commands = project.Commands.Select(command => new CommandDefinition
                {
                    Id = command.Id,
                    Name = command.Name,
                    Purpose = command.Purpose,
                    Command = command.Command,
                    WorkingDirectory = command.WorkingDirectory,
                    Kind = command.Kind,
                    EnvironmentVariables = command.EnvironmentVariables.Select(variable => variable.IsSensitive
                        ? new CommandEnvironmentVariable { Name = variable.Name, IsSensitive = true, ValueState = "Required" }
                        : variable.Clone()).ToList()
                }).ToList(),
                Groups = project.Groups.Select(group => new CommandGroupDefinition
                {
                    Id = group.Id,
                    Name = group.Name,
                    ExecutionMode = group.ExecutionMode,
                    CommandIds = group.CommandIds.ToList()
                }).ToList()
            }).ToList()
        };
    }

    public async Task SaveAsync(WorkspaceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        PrepareForWrite(configuration);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAtomicallyAsync(FilePath, configuration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static void PrepareForWrite(WorkspaceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.SchemaVersion is not (1 or 2 or CurrentSchemaVersion))
            throw new InvalidDataException("The TalosDesk workspace uses an unsupported schema version.");
        NormalizeAndValidate(configuration);
    }

    private static async Task WriteAtomicallyAsync(string filePath, WorkspaceConfiguration configuration, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("The workspace path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 16_384, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, configuration, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static WorkspaceConfiguration DeserializeAndNormalize(byte[] bytes)
    {
        var configuration = JsonSerializer.Deserialize<WorkspaceConfiguration>(bytes, JsonOptions);
        if (configuration?.SchemaVersion == CurrentSchemaVersion)
        {
            using var document = JsonDocument.Parse(bytes);
            ValidateSchemaThreeShape(document.RootElement);
        }
        NormalizeAndValidate(configuration);
        return configuration!;
    }

    private static void ValidateSchemaThreeShape(JsonElement root)
    {
        if (!root.TryGetProperty("Projects", out var projects) || projects.ValueKind != JsonValueKind.Array) return;
        foreach (var project in projects.EnumerateArray())
        {
            if (project.ValueKind != JsonValueKind.Object) continue;
            if (!project.TryGetProperty("Commands", out var commands) || commands.ValueKind != JsonValueKind.Array) continue;
            foreach (var command in commands.EnumerateArray())
            {
                if (command.ValueKind != JsonValueKind.Object) continue;
                if (!command.TryGetProperty("EnvironmentVariables", out var variables) || variables.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("A schema 3 command is missing its environment variable list.");
                foreach (var variable in variables.EnumerateArray()) ValidateVariableShape(variable);
            }
        }
    }

    private static void ValidateVariableShape(JsonElement variable)
    {
        if (variable.ValueKind != JsonValueKind.Object) throw new InvalidDataException("An environment variable is invalid.");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in variable.EnumerateObject())
        {
            if (!fields.Add(property.Name) || property.Name is not ("Name" or "IsSensitive" or "Value" or "ValueState" or "ProtectedValue"))
                throw new InvalidDataException("An environment variable has duplicate or unsupported fields.");
        }

        if (!fields.Contains("Name") || variable.GetProperty("Name").ValueKind != JsonValueKind.String ||
            !fields.Contains("IsSensitive") || variable.GetProperty("IsSensitive").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("An environment variable is missing a required field.");

        var sensitive = variable.GetProperty("IsSensitive").GetBoolean();
        bool valid;
        if (!sensitive)
        {
            valid = fields.SetEquals(["Name", "IsSensitive", "Value"]) && variable.GetProperty("Value").ValueKind == JsonValueKind.String;
        }
        else if (fields.SetEquals(["Name", "IsSensitive", "ValueState"]))
        {
            valid = variable.GetProperty("ValueState").ValueKind == JsonValueKind.String &&
                    variable.GetProperty("ValueState").GetString() == "Required";
        }
        else
        {
            valid = fields.SetEquals(["Name", "IsSensitive", "ValueState", "ProtectedValue"]) &&
                    variable.GetProperty("ValueState").ValueKind == JsonValueKind.String &&
                    variable.GetProperty("ValueState").GetString() == "Protected" &&
                    variable.GetProperty("ProtectedValue").ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(variable.GetProperty("ProtectedValue").GetString());
        }
        if (!valid) throw new InvalidDataException("An environment variable has an invalid value state.");
    }

    private static void NormalizeAndValidate(WorkspaceConfiguration? configuration)
    {
        if (configuration is null || configuration.SchemaVersion is not (1 or 2 or CurrentSchemaVersion) || configuration.Projects is null)
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
                if (command.EnvironmentVariables is null || command.EnvironmentVariables.Any(variable => variable is null))
                    throw new InvalidDataException($"Command '{command.Name}' has an invalid environment variable list.");
                var variableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var variable in command.EnvironmentVariables)
                {
                    if (string.IsNullOrWhiteSpace(variable.Name) || variable.Name != variable.Name.Trim() ||
                        variable.Name.Contains('=') || variable.Name.Contains('\0') || !variableNames.Add(variable.Name))
                        throw new InvalidDataException($"Command '{command.Name}' has an invalid or duplicate environment variable name.");
                    var valid = variable.IsSensitive
                        ? variable.Value is null && (variable.ValueState == "Required" && variable.ProtectedValue is null ||
                                                     variable.ValueState == "Protected" && !string.IsNullOrEmpty(variable.ProtectedValue))
                        : variable.Value is not null && !variable.Value.Contains('\0') && variable.ValueState is null && variable.ProtectedValue is null;
                    if (!valid) throw new InvalidDataException($"Command '{command.Name}' has an invalid environment variable value state.");
                }
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
