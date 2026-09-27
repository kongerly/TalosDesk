namespace TalosDesk.Core.Configuration;

public sealed class WorkspaceConfiguration
{
    public int SchemaVersion { get; set; } = 1;
    public List<ProjectDefinition> Projects { get; set; } = [];
}

public sealed class ProjectDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public List<CommandDefinition> Commands { get; set; } = [];
}

public sealed class CommandDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public CommandKind Kind { get; set; }
}

public enum CommandKind
{
    Task,
    Service
}
