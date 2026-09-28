using System.Text.Json.Serialization;

namespace TalosDesk.Core.Configuration;

public sealed class WorkspaceConfiguration
{
    public int SchemaVersion { get; set; } = 3;
    public List<ProjectDefinition> Projects { get; set; } = [];
}

public sealed class ProjectDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public List<CommandDefinition> Commands { get; set; } = [];
    public List<CommandGroupDefinition> Groups { get; set; } = [];
}

public sealed class CommandDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public CommandKind Kind { get; set; }
    public List<CommandEnvironmentVariable> EnvironmentVariables { get; set; } = [];
}

public sealed class CommandEnvironmentVariable
{
    public string Name { get; set; } = string.Empty;
    public bool IsSensitive { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValueState { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProtectedValue { get; set; }

    public CommandEnvironmentVariable Clone() => new()
    {
        Name = Name,
        IsSensitive = IsSensitive,
        Value = Value,
        ValueState = ValueState,
        ProtectedValue = ProtectedValue
    };
}

public enum CommandKind
{
    Task,
    Service
}

public sealed class CommandGroupDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public CommandGroupExecutionMode ExecutionMode { get; set; }
    public List<Guid> CommandIds { get; set; } = [];
}

public enum CommandGroupExecutionMode
{
    Parallel,
    Sequential
}
