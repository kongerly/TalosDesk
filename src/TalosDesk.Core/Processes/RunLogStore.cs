using System.Text;
using System.Text.Json;

namespace TalosDesk.Core.Processes;

public sealed record RunLogSettings(long MaxBytes = 500L * 1024 * 1024, int RetentionDays = 30);

public sealed class RunLogInfo
{
    public Guid ProjectId { get; set; }
    public Guid CommandId { get; set; }
    public Guid RunId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string State { get; set; } = "Running";
    public int? ExitCode { get; set; }
    public bool Truncated { get; set; }
    public bool WriteFailed { get; set; }
}

public sealed class RunLogWriter
{
    internal RunLogWriter(RunLogInfo info, string directory, FileStream stdout, FileStream stderr)
    {
        Info = info;
        Directory = directory;
        Stdout = stdout;
        Stderr = stderr;
    }

    public RunLogInfo Info { get; }
    internal string Directory { get; }
    internal FileStream Stdout { get; }
    internal FileStream Stderr { get; }
    internal bool Closed { get; set; }
    internal int WarningReported;
}

/// <summary>Stores command output outside the workspace JSON, with a quota per workspace.</summary>
public sealed class RunLogStore
{
    private const string MetadataFile = "run.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly object _sync = new();
    private readonly HashSet<Guid> _active = [];
    private long _storedBytes;

    public RunLogStore(string workspacePath) => RootPath = Path.GetFullPath(workspacePath) + ".logs";

    public string RootPath { get; }
    public RunLogSettings Settings { get; private set; } = new();

    public void Initialize()
    {
        lock (_sync)
        {
            System.IO.Directory.CreateDirectory(RootPath);
            var settingsPath = Path.Combine(RootPath, "settings.json");
            if (File.Exists(settingsPath))
            {
                Settings = JsonSerializer.Deserialize<RunLogSettings>(File.ReadAllText(settingsPath))
                    ?? throw new InvalidDataException("日志设置文件为空。");
                ValidateSettings(Settings);
            }
            foreach (var (info, directory) in EnumerateRuns())
            {
                if (info.State != "Running") continue;
                info.State = "Interrupted";
                info.EndedAt = DateTimeOffset.Now;
                SaveInfo(directory, info);
            }
            _storedBytes = CalculateStoredBytes();
            Prune(DateTimeOffset.Now);
        }
    }

    public void UpdateSettings(RunLogSettings settings)
    {
        ValidateSettings(settings);
        lock (_sync)
        {
            System.IO.Directory.CreateDirectory(RootPath);
            WriteJsonAtomically(Path.Combine(RootPath, "settings.json"), settings);
            Settings = settings;
            Prune(DateTimeOffset.Now);
        }
    }

    public RunLogWriter Begin(Guid projectId, Guid commandId, DateTimeOffset startedAt)
    {
        if (projectId == Guid.Empty || commandId == Guid.Empty) throw new ArgumentException("Project and command IDs are required.");
        lock (_sync)
        {
            Prune(startedAt);
            var info = new RunLogInfo { ProjectId = projectId, CommandId = commandId, RunId = Guid.NewGuid(), StartedAt = startedAt };
            var directory = GetRunDirectory(info);
            System.IO.Directory.CreateDirectory(directory);
            FileStream? stdout = null;
            FileStream? stderr = null;
            try
            {
                stdout = new FileStream(Path.Combine(directory, "stdout.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16_384);
                stderr = new FileStream(Path.Combine(directory, "stderr.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16_384);
                SaveInfo(directory, info);
                var createdBytes = GetDirectoryBytes(directory);
                _active.Add(info.RunId);
                Prune(startedAt, createdBytes);
                if (_storedBytes + createdBytes > Settings.MaxBytes)
                    throw new IOException("日志容量已满，运行中的批次无法清理。");
                _storedBytes += createdBytes;
                return new RunLogWriter(info, directory, stdout, stderr);
            }
            catch
            {
                _active.Remove(info.RunId);
                stdout?.Dispose();
                stderr?.Dispose();
                if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
                throw;
            }
        }
    }

    public void Append(RunLogWriter writer, CommandOutput output)
    {
        lock (_sync)
        {
            if (writer.Closed || writer.Info.Truncated || writer.Info.WriteFailed) return;
            var bytes = Utf8.GetBytes(output.Text + Environment.NewLine);
            try
            {
                if (_storedBytes + bytes.Length > Settings.MaxBytes)
                {
                    Prune(DateTimeOffset.Now, bytes.Length);
                    if (_storedBytes + bytes.Length > Settings.MaxBytes)
                    {
                        writer.Info.Truncated = true;
                        TrySaveIntegrityStatus(writer);
                        return;
                    }
                }
                var stream = output.Stream == "stderr" ? writer.Stderr : writer.Stdout;
                stream.Write(bytes);
                stream.Flush();
                _storedBytes += bytes.Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                writer.Info.WriteFailed = true;
                _storedBytes = CalculateStoredBytes();
                TrySaveIntegrityStatus(writer);
            }
        }
    }

    public void Complete(RunLogWriter writer, CommandRunResult? result)
    {
        lock (_sync)
        {
            if (writer.Closed) return;
            writer.Closed = true;
            writer.Stdout.Dispose();
            writer.Stderr.Dispose();
            _active.Remove(writer.Info.RunId);
            writer.Info.EndedAt = DateTimeOffset.Now;
            writer.Info.State = result?.State.ToString() ?? "Interrupted";
            writer.Info.ExitCode = result?.ExitCode;
            try
            {
                var metadataPath = Path.Combine(writer.Directory, MetadataFile);
                var priorBytes = new FileInfo(metadataPath).Length;
                SaveInfo(writer.Directory, writer.Info);
                _storedBytes += new FileInfo(metadataPath).Length - priorBytes;
                Prune(DateTimeOffset.Now);
            }
            catch (IOException) { writer.Info.WriteFailed = true; }
            catch (UnauthorizedAccessException) { writer.Info.WriteFailed = true; }
        }
    }

    public void Discard(RunLogWriter writer)
    {
        lock (_sync)
        {
            if (writer.Closed) return;
            writer.Closed = true;
            writer.Stdout.Dispose();
            writer.Stderr.Dispose();
            _active.Remove(writer.Info.RunId);
            _storedBytes -= GetDirectoryBytes(writer.Directory);
            System.IO.Directory.Delete(writer.Directory, true);
        }
    }

    public IReadOnlyList<RunLogInfo> GetRuns(Guid projectId, Guid commandId)
    {
        lock (_sync)
        {
            return EnumerateRuns().Where(item => item.Info.ProjectId == projectId && item.Info.CommandId == commandId)
                .Select(item => item.Info).OrderByDescending(info => info.StartedAt).ToArray();
        }
    }

    public IReadOnlyList<string> ReadTail(RunLogInfo info, string stream, int maxLines = 10_000)
    {
        if (stream is not ("stdout" or "stderr")) throw new ArgumentException("Unknown output stream.", nameof(stream));
        if (maxLines < 1) throw new ArgumentOutOfRangeException(nameof(maxLines));
        lock (_sync)
        {
            var path = Path.Combine(GetRunDirectory(info), stream + ".log");
            var tail = new Queue<string>(maxLines);
            if (!File.Exists(path)) return [];
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(file, Utf8);
            while (reader.ReadLine() is { } line)
            {
                if (tail.Count == maxLines) tail.Dequeue();
                tail.Enqueue(line);
            }
            return tail.ToArray();
        }
    }

    public void ClearHistory()
    {
        lock (_sync)
        {
            foreach (var (info, directory) in EnumerateRuns())
            {
                if (!_active.Contains(info.RunId)) System.IO.Directory.Delete(directory, true);
            }
            _storedBytes = CalculateStoredBytes();
        }
    }

    private void Prune(DateTimeOffset now, long requiredBytes = 0)
    {
        var completed = EnumerateRuns().Where(item => !_active.Contains(item.Info.RunId))
            .OrderBy(item => item.Info.StartedAt).ToArray();
        foreach (var (info, directory) in completed)
        {
            if (info.StartedAt < now.AddDays(-Settings.RetentionDays) || _storedBytes + requiredBytes > Settings.MaxBytes)
            {
                _storedBytes -= GetDirectoryBytes(directory);
                System.IO.Directory.Delete(directory, true);
            }
        }
    }

    private IEnumerable<(RunLogInfo Info, string Directory)> EnumerateRuns()
    {
        if (!System.IO.Directory.Exists(RootPath)) yield break;
        foreach (var projectDirectory in System.IO.Directory.EnumerateDirectories(RootPath))
        {
            if (!IsSafeGuidDirectory(projectDirectory)) continue;
            foreach (var commandDirectory in System.IO.Directory.EnumerateDirectories(projectDirectory))
            {
                if (!IsSafeGuidDirectory(commandDirectory)) continue;
                foreach (var directory in System.IO.Directory.EnumerateDirectories(commandDirectory))
                {
                    if (!IsSafeGuidDirectory(directory)) continue;
                    var metadataPath = Path.Combine(directory, MetadataFile);
                    if (!File.Exists(metadataPath)) continue;
                    RunLogInfo? info;
                    try { info = JsonSerializer.Deserialize<RunLogInfo>(File.ReadAllText(metadataPath)); }
                    catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { continue; }
                    if (info is not null && info.RunId != Guid.Empty && Path.GetFullPath(directory) == GetRunDirectory(info))
                        yield return (info, directory);
                }
            }
        }
    }

    private static bool IsSafeGuidDirectory(string path) =>
        Guid.TryParseExact(Path.GetFileName(path), "N", out _) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    private string GetRunDirectory(RunLogInfo info) => Path.Combine(RootPath,
        info.ProjectId.ToString("N"), info.CommandId.ToString("N"), info.RunId.ToString("N"));

    private long CalculateStoredBytes() => EnumerateRuns().Sum(item => GetDirectoryBytes(item.Directory));

    private static long GetDirectoryBytes(string directory) => System.IO.Directory.EnumerateFiles(directory).Sum(path => new FileInfo(path).Length);

    private static void SaveInfo(string directory, RunLogInfo info) =>
        WriteJsonAtomically(Path.Combine(directory, MetadataFile), info);

    private void TrySaveIntegrityStatus(RunLogWriter writer)
    {
        try
        {
            var metadataPath = Path.Combine(writer.Directory, MetadataFile);
            var oldBytes = new FileInfo(metadataPath).Length;
            SaveInfo(writer.Directory, writer.Info);
            _storedBytes += new FileInfo(metadataPath).Length - oldBytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions), Utf8);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidateSettings(RunLogSettings settings)
    {
        if (settings.MaxBytes < 1024 * 1024 || settings.MaxBytes > 100L * 1024 * 1024 * 1024 ||
            settings.RetentionDays is < 1 or > 3650)
            throw new ArgumentOutOfRangeException(nameof(settings), "日志上限必须是 1 MB–100 GB、1–3650 天。");
    }
}
