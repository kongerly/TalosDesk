using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TalosDesk.Core.Configuration;

public sealed class WorkspaceStore
{
    public const int CurrentSchemaVersion = 4;
    // Disallow 让未知字段（拼错的 "TcpProbee"、旧版本残留键）不再被静默丢弃；只影响读取，序列化不受影响。
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Action? _beforeCommit;
    private readonly Action? _afterCommit;

    public WorkspaceStore(string? filePath = null)
        : this(filePath, null, null)
    {
    }

    // 隔离回归可在提交边界安排外部修改，生产入口不注入回调。
    internal WorkspaceStore(string? filePath, Action? beforeCommit, Action? afterCommit)
    {
        FilePath = Path.GetFullPath(filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TalosDesk", "workspace.json"));
        _beforeCommit = beforeCommit;
        _afterCommit = afterCommit;
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
        var clone = DeepClone(configuration);
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
                    TcpProbe = command.TcpProbe?.Clone(),
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

    public Task<WorkspaceRevision> SaveAsync(WorkspaceConfiguration configuration, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(configuration, null, cancellationToken);

    public Task<WorkspaceRevision> SaveAsync(WorkspaceConfiguration configuration, WorkspaceRevision expectedRevision,
        CancellationToken cancellationToken = default) => SaveCoreAsync(configuration, expectedRevision, cancellationToken);

    private async Task<WorkspaceRevision> SaveCoreAsync(WorkspaceConfiguration configuration, WorkspaceRevision? expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        // PrepareForWrite 会升级 SchemaVersion 并清空旧版 Groups，必须在门内作用于副本：
        // 写盘失败时调用方（桌面传的是 UI 绑定对象）不能已变成规范化后的版本而与磁盘分叉。
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = DeepClone(configuration);
            PrepareForWrite(snapshot);
            var revision = await WriteAtomicallyAsync(FilePath, snapshot, cancellationToken, expectedRevision, _beforeCommit).ConfigureAwait(false);
            _afterCommit?.Invoke();
            return revision;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>
    /// 保存入口只读取调用方对象。桌面传入的是 UI 绑定的那批实例，写入失败时内存已升级而磁盘未升级
    /// 属于最难复现的分叉，因此这里用 JSON 往返复制一份再规范化。
    /// </summary>
    private static WorkspaceConfiguration DeepClone(WorkspaceConfiguration configuration)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkspaceConfiguration>(JsonSerializer.SerializeToUtf8Bytes(configuration, JsonOptions), JsonOptions)
                ?? throw new InvalidDataException("The TalosDesk workspace is empty.");
        }
        catch (JsonException exception)
        {
            // 复制只经过内存对象，失败说明调用方对象本身无法表达为合法工作区。
            throw new InvalidDataException("The TalosDesk workspace cannot be copied for writing.", exception);
        }
    }

    private static void PrepareForWrite(WorkspaceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.SchemaVersion is not (1 or 2 or 3 or CurrentSchemaVersion))
            throw new InvalidDataException("The TalosDesk workspace uses an unsupported schema version.");
        NormalizeAndValidate(configuration);
    }

    private static async Task<WorkspaceRevision> WriteAtomicallyAsync(string filePath, WorkspaceConfiguration configuration,
        CancellationToken cancellationToken, WorkspaceRevision? expectedRevision = null, Action? beforeCommit = null)
    {
        // 写入与返回摘要共用同一份字节，不能在替换后把外部版本误认为自己的保存结果。
        var bytes = JsonSerializer.SerializeToUtf8Bytes(configuration, JsonOptions);
        var revision = new WorkspaceRevision(true, Convert.ToHexString(SHA256.HashData(bytes)));
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("The workspace path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 16_384, useAsync: true))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            beforeCommit?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            // 完成临时文件后才校验；校验与替换之间不再异步等待或调度界面回调。
            if (expectedRevision is { } expected && ReadRevisionForCommit(fullPath) != expected)
                throw new WorkspaceChangedException();

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // 首次保存不覆盖刚由外部创建的文件，即使它出现于最后一次校验之后。
                File.Move(temporaryPath, fullPath, overwrite: expectedRevision?.Exists != false);
            }
            catch (IOException) when (expectedRevision?.Exists == false && File.Exists(fullPath))
            {
                throw new WorkspaceChangedException();
            }
            return revision;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static WorkspaceRevision ReadRevisionForCommit(string filePath)
    {
        try
        {
            // 摘要读取期间拒绝写入和替换，句柄关闭后立即提交同目录临时文件。
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new WorkspaceRevision(true, Convert.ToHexString(SHA256.HashData(stream)));
        }
        catch (FileNotFoundException) { return WorkspaceRevision.Missing; }
        catch (DirectoryNotFoundException) { return WorkspaceRevision.Missing; }
    }

    private static WorkspaceConfiguration DeserializeAndNormalize(byte[] bytes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The TalosDesk workspace file is not valid JSON.", exception);
        }

        using (document)
        {
            RejectDuplicateMembers(document.RootElement);
            var configuration = Deserialize(bytes);
            if (configuration.SchemaVersion >= 3) ValidateSchemaThreeShape(document.RootElement);
            ValidateProbeShape(document.RootElement, configuration.SchemaVersion);
            NormalizeAndValidate(configuration);
            return configuration;
        }
    }

    private static WorkspaceConfiguration Deserialize(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkspaceConfiguration>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The TalosDesk workspace file is empty or uses an unsupported schema version.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The TalosDesk workspace JSON contains an unsupported field name, value type or duplicate definition.", exception);
        }
    }

    /// <summary>
    /// JsonSerializer 对重复键采用"后者覆盖前者"，但第二个 "Projects"、"Kind" 或 "Commands"
    /// 恰恰是必须拒绝的配置失败模式，因此在整个文档上统一按层拒绝重复成员。
    /// </summary>
    private static void RejectDuplicateMembers(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var members = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!members.Add(property.Name))
                        throw new InvalidDataException($"The TalosDesk workspace contains the duplicate field '{property.Name}'.");
                    RejectDuplicateMembers(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) RejectDuplicateMembers(item);
                break;
        }
    }

    private static void ValidateProbeShape(JsonElement root, int schema)
    {
        if (!root.TryGetProperty("Projects", out var projects) || projects.ValueKind != JsonValueKind.Array) return;
        foreach (var project in projects.EnumerateArray())
        {
            if (project.ValueKind != JsonValueKind.Object || !project.TryGetProperty("Commands", out var commands) || commands.ValueKind != JsonValueKind.Array) continue;
            foreach (var command in commands.EnumerateArray())
            {
                if (command.ValueKind != JsonValueKind.Object) continue;
                var probes = command.EnumerateObject().Where(property => property.Name == "TcpProbe").ToArray();
                if (probes.Length > 1) throw new InvalidDataException("TCP 探测字段重复。");
                if (probes.Length == 0 || probes[0].Value.ValueKind == JsonValueKind.Null) continue;
                var probe = probes[0].Value;
                if (schema != CurrentSchemaVersion || probe.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("非空 TCP 探测配置要求 schema 4。");
                var fields = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in probe.EnumerateObject())
                {
                    if (!fields.Add(property.Name) || property.Name is not ("Address" or "Port" or "IntervalSeconds" or "ConnectTimeoutSeconds" or "StartupTimeoutSeconds" or "FailureThreshold"))
                        throw new InvalidDataException("TCP 探测包含重复或未知字段。");
                    if (property.Name == "Address" ? property.Value.ValueKind != JsonValueKind.String :
                        property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out _))
                        throw new InvalidDataException("TCP 探测字段类型无效。");
                }
                if (fields.Count != 6) throw new InvalidDataException("TCP 探测缺少必需字段。");
            }
        }
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
        if (configuration is null || configuration.SchemaVersion is not (1 or 2 or 3 or CurrentSchemaVersion) || configuration.Projects is null)
        {
            throw new InvalidDataException("The TalosDesk workspace file is empty or uses an unsupported schema version.");
        }

        var legacyWorkspace = configuration.SchemaVersion == 1;
        if (configuration.SchemaVersion < CurrentSchemaVersion && configuration.Projects
            .Where(project => project?.Commands is not null).SelectMany(project => project.Commands)
            .Any(command => command?.TcpProbe is not null))
            throw new InvalidDataException("非空 TCP 探测配置要求 schema 4。");
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

        if (configuration.Projects.Any(project => project.Id == Guid.Empty))
        {
            throw new InvalidDataException("工作区包含全零 GUID，项目 ID 不能为空。");
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
                if (command.Id == Guid.Empty) throw new InvalidDataException("工作区包含全零 GUID，命令 ID 不能为空。");
                if (!commandIds.Add(command.Id)) throw new InvalidDataException("The TalosDesk workspace contains duplicate command IDs.");
                if (!Enum.IsDefined(command.Kind)) throw new InvalidDataException($"Command '{command.Name}' has an unsupported run type.");
                TcpProbeConfiguration.ValidateCommand(command);
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
