using System.Text.Json.Serialization;

namespace TalosDesk.Core.Configuration;

public sealed class WorkspaceConfiguration
{
    public int SchemaVersion { get; set; } = WorkspaceStore.CurrentSchemaVersion;
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CommandShell Shell { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AutoRestartEnabled { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TcpProbeConfiguration? TcpProbe { get; set; }
    public List<CommandEnvironmentVariable> EnvironmentVariables { get; set; } = [];
}

public sealed class TcpProbeConfiguration
{
    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public int IntervalSeconds { get; set; } = 2;
    public int ConnectTimeoutSeconds { get; set; } = 1;
    public int StartupTimeoutSeconds { get; set; } = 120;
    public int FailureThreshold { get; set; } = 3;

    public TcpProbeConfiguration Clone() => (TcpProbeConfiguration)MemberwiseClone();

    public void Validate()
    {
        if (Address is not ("127.0.0.1" or "::1") || Port is < 1 or > 65535 ||
            IntervalSeconds is < 1 or > 60 || ConnectTimeoutSeconds is < 1 or > 60 ||
            StartupTimeoutSeconds is < 1 or > 3600 || StartupTimeoutSeconds < ConnectTimeoutSeconds ||
            FailureThreshold is < 1 or > 100)
            throw new InvalidDataException("TCP 探测参数无效：请检查回环地址、端口、时间和失败阈值。");
    }

    public static void ValidateCommand(CommandDefinition command)
    {
        if (command.TcpProbe is null) return;
        if (command.Kind != CommandKind.Service) throw new InvalidDataException("只有服务命令可以开启 TCP 探测。");
        command.TcpProbe.Validate();
    }
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

public enum CommandShell
{
    PowerShell,
    Cmd
}

public static class CommandShellRules
{
    public static void Validate(CommandShell shell, string command)
    {
        if (!Enum.IsDefined(shell)) throw new InvalidDataException("命令 Shell 无效。");
        if (shell == CommandShell.Cmd && (command.Length > 7000 || command.IndexOfAny(['\r', '\n', '\0']) >= 0))
            throw new InvalidDataException("Cmd 首版只支持不超过 7,000 个字符的单行命令。多行流程请保存为 UTF-8 批处理并显式调用。");
    }
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
