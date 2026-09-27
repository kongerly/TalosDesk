using System.Text.Json;

namespace TalosDesk.Core.Configuration;

public sealed class WorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public WorkspaceStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TalosDesk", "workspace.json");
    }

    public string FilePath { get; }

    public async Task<WorkspaceConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath)) return new WorkspaceConfiguration();
        await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, useAsync: true);
        var configuration = await JsonSerializer.DeserializeAsync<WorkspaceConfiguration>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        Validate(configuration);

        return configuration!;
    }

    public static async Task<WorkspaceConfiguration> ReadFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, useAsync: true);
        var configuration = await JsonSerializer.DeserializeAsync<WorkspaceConfiguration>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        Validate(configuration);
        return configuration!;
    }

    public static async Task WriteFileAsync(string filePath, WorkspaceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        Validate(configuration);
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? throw new InvalidOperationException("The workspace path has no parent directory.");
        Directory.CreateDirectory(directory);
        await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 16_384, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, configuration, JsonOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(WorkspaceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Validate(configuration);
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

    private static void Validate(WorkspaceConfiguration? configuration)
    {
        if (configuration is null || configuration.SchemaVersion != 1 || configuration.Projects is null)
        {
            throw new InvalidDataException("The TalosDesk workspace file is empty or uses an unsupported schema version.");
        }

        if (configuration.Projects.Any(project => project is null || project.Commands is null || string.IsNullOrWhiteSpace(project.Name) || string.IsNullOrWhiteSpace(project.Directory)))
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
        }
    }
}
