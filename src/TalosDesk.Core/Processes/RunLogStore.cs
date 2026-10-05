using System.Security.Cryptography;
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
    private sealed record LogLocation(string ParentDirectory);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly object _sync = new();
    private readonly HashSet<Guid> _active = [];
    private readonly string _workspacePath;
    private readonly string _defaultRootPath;
    private readonly string _locationFilePath;
    private readonly Func<string, Stream> _openRead;
    private long _storedBytes;

    public RunLogStore(string workspacePath)
        : this(workspacePath, path => new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 16_384, FileOptions.RandomAccess))
    {
    }

    internal RunLogStore(string workspacePath, Func<string, Stream> openRead)
    {
        _openRead = openRead;
        _workspacePath = Path.GetFullPath(workspacePath);
        _defaultRootPath = _workspacePath + ".logs";
        _locationFilePath = _workspacePath + ".log-location.json";
        RootPath = _defaultRootPath;
    }

    public string RootPath { get; private set; }
    public RunLogSettings Settings { get; private set; } = new();

    public void Initialize()
    {
        lock (_sync)
        {
            RootPath = _defaultRootPath;
            if (File.Exists(_locationFilePath))
            {
                var location = JsonSerializer.Deserialize<LogLocation>(File.ReadAllText(_locationFilePath))
                    ?? throw new InvalidDataException("日志位置文件为空。");
                RootPath = GetCustomRootPath(location.ParentDirectory);
            }
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

    /// <summary>Moves this workspace's logs to a selected parent folder. Returns an old folder left behind if it could not be removed.</summary>
    public string? ChangeLocation(string? parentDirectory)
    {
        lock (_sync)
        {
            if (_active.Count != 0) throw new InvalidOperationException("有运行中的日志批次，不能更改保存位置。");
            var target = parentDirectory is null ? _defaultRootPath : GetCustomRootPath(parentDirectory);
            if (Path.TrimEndingDirectorySeparator(target).Equals(Path.TrimEndingDirectorySeparator(RootPath), StringComparison.OrdinalIgnoreCase))
                return null;
            var source = RootPath;
            if (IsWithin(target, source) || IsWithin(source, target))
                throw new IOException("新旧日志目录不能互相包含。请选择其他文件夹。");
            if (System.IO.Directory.Exists(target) || File.Exists(target))
                throw new IOException("目标日志目录已存在。请选择其他文件夹，避免覆盖已有日志。");

            var parent = Path.GetDirectoryName(target)!;
            System.IO.Directory.CreateDirectory(parent);
            var staging = target + ".moving-" + Guid.NewGuid().ToString("N");
            var targetCreated = false;
            try
            {
                if (System.IO.Directory.Exists(source)) CopyDirectory(source, staging);
                else System.IO.Directory.CreateDirectory(staging);
                System.IO.Directory.Move(staging, target);
                targetCreated = true;
                if (parentDirectory is null) File.Delete(_locationFilePath);
                else WriteJsonAtomically(_locationFilePath, new LogLocation(Path.GetFullPath(parentDirectory)));
            }
            catch
            {
                if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, true);
                if (targetCreated && System.IO.Directory.Exists(target)) System.IO.Directory.Delete(target, true);
                throw;
            }

            RootPath = target;
            if (!System.IO.Directory.Exists(source)) return null;
            try { System.IO.Directory.Delete(source, true); return null; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return source; }
        }
    }

    private static bool IsWithin(string path, string directory) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private string GetCustomRootPath(string parentDirectory)
    {
        if (string.IsNullOrWhiteSpace(parentDirectory) || !Path.IsPathFullyQualified(parentDirectory))
            throw new InvalidDataException("日志位置必须是绝对路径。");
        var parent = Path.GetFullPath(parentDirectory);
        var workspaceHash = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(_workspacePath.ToUpperInvariant())))[..16].ToLowerInvariant();
        return Path.Combine(parent, $"TalosDesk-{workspaceHash}.logs");
    }

    private static void CopyDirectory(string source, string target)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("日志目录包含链接，无法安全迁移。");
        System.IO.Directory.CreateDirectory(target);
        foreach (var file in System.IO.Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("日志目录包含链接，无法安全迁移。");
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        foreach (var directory in System.IO.Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
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

    public IReadOnlyList<string> ReadTail(RunLogInfo info, string stream, int maxLines = 10_000,
        CancellationToken cancellationToken = default)
    {
        if (stream is not ("stdout" or "stderr")) throw new ArgumentException("Unknown output stream.", nameof(stream));
        if (maxLines < 1) throw new ArgumentOutOfRangeException(nameof(maxLines));
        cancellationToken.ThrowIfCancellationRequested();
        string path;
        lock (_sync) path = Path.Combine(GetRunDirectory(info), stream + ".log");
        Stream file;
        try { file = _openRead(path); }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        using (file)
        {
            // 等当前追加完成后固定文件长度，后续追加不进入快照；扫描和解码不占用写入锁。
            long length;
            lock (_sync) length = file.Length;
            var position = length;
            var start = 0L;
            var separators = 0;
            var skipCarriageReturn = false;
            var buffer = new byte[16_384];
            while (position > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, position);
                position -= count;
                file.Position = position;
                file.ReadExactly(buffer.AsSpan(0, count));
                for (var index = count - 1; index >= 0; index--)
                {
                    var value = buffer[index];
                    if (value == '\r' && skipCarriageReturn)
                    {
                        skipCarriageReturn = false;
                        continue;
                    }
                    skipCarriageReturn = value == '\n';
                    if (value is not ((byte)'\r' or (byte)'\n')) continue;
                    // EOF 的结束符属于最后一行；CRLF 即使跨块也只计一次。
                    if (position + index == length - 1) continue;
                    if (++separators != maxLines) continue;
                    start = position + index + 1;
                    break;
                }
                if (start > 0) break;
            }
            file.Position = start;
            using var snapshot = new LogSnapshotStream(file, length - start, cancellationToken);
            using var reader = new StreamReader(snapshot, Utf8, detectEncodingFromByteOrderMarks: start == 0);
            var tail = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                tail.Add(line);
            }
            return tail.ToArray();
        }
    }

    private sealed class LogSnapshotStream(Stream file, long remaining, CancellationToken cancellationToken) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = file.Read(buffer, offset, (int)Math.Min(count, remaining));
            remaining -= read;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public void ClearHistory()
    {
        lock (_sync)
        {
            foreach (var (runId, directory) in EnumerateRunDirectories())
            {
                if (!_active.Contains(runId)) System.IO.Directory.Delete(directory, true);
            }
            _storedBytes = CalculateStoredBytes();
        }
    }

    private void Prune(DateTimeOffset now, long requiredBytes = 0)
    {
        var completed = EnumerateRunDirectories().Where(item => !_active.Contains(item.RunId))
            .Select(item => (item.Directory, StartedAt: TryReadInfo(item.Directory)?.StartedAt
                ?? new DateTimeOffset(System.IO.Directory.GetLastWriteTimeUtc(item.Directory))))
            .OrderBy(item => item.StartedAt).ToArray();
        foreach (var (directory, startedAt) in completed)
        {
            if (startedAt < now.AddDays(-Settings.RetentionDays) || _storedBytes + requiredBytes > Settings.MaxBytes)
            {
                var deletedBytes = GetDirectoryBytes(directory);
                System.IO.Directory.Delete(directory, true);
                _storedBytes -= deletedBytes;
            }
        }
    }

    private IEnumerable<(RunLogInfo Info, string Directory)> EnumerateRuns()
    {
        foreach (var (_, directory) in EnumerateRunDirectories())
        {
            if (TryReadInfo(directory) is { } info) yield return (info, directory);
        }
    }

    private RunLogInfo? TryReadInfo(string directory)
    {
        try
        {
            var info = JsonSerializer.Deserialize<RunLogInfo>(File.ReadAllText(Path.Combine(directory, MetadataFile)));
            return info is not null && info.RunId != Guid.Empty && Path.GetFullPath(directory) == GetRunDirectory(info)
                ? info : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    // 批次目录独立于元数据枚举，损坏或缺失的 run.json 不能绕过容量和清理规则。
    private IEnumerable<(Guid RunId, string Directory)> EnumerateRunDirectories()
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
                    yield return (Guid.ParseExact(Path.GetFileName(directory), "N"), directory);
                }
            }
        }
    }

    private static bool IsSafeGuidDirectory(string path) =>
        Guid.TryParseExact(Path.GetFileName(path), "N", out _) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    private string GetRunDirectory(RunLogInfo info) => Path.Combine(RootPath,
        info.ProjectId.ToString("N"), info.CommandId.ToString("N"), info.RunId.ToString("N"));

    private long CalculateStoredBytes() => EnumerateRunDirectories().Sum(item => GetDirectoryBytes(item.Directory));

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
