using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TalosDesk.Core.Diagnostics;

public sealed class CrashRecordStore
{
    internal const int MaximumRecordBytes = 64 * 1024;
    internal const int MaximumRecordCount = 100;
    internal const long MaximumTotalBytes = 5L * 1024 * 1024;
    private const int MaximumExceptionNodes = 8;
    private const int MaximumExceptionDepth = 4;
    private const int MaximumFrames = 32;
    private const int MaximumStringLength = 256;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;

    public CrashRecordStore(string workspacePath, TimeProvider? timeProvider = null)
    {
        RootPath = Path.GetFullPath(workspacePath) + ".crashes";
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string RootPath { get; }

    public CrashWriteResult Write(
        Exception exception,
        CrashSource source,
        bool isFatal,
        string applicationVersion,
        string releaseChannel,
        bool enabled = true)
    {
        if (!enabled) return new CrashWriteResult(CrashWriteStatus.Disabled);
        if (exception is null) return new CrashWriteResult(CrashWriteStatus.Rejected);

        lock (_sync)
        {
            string? temporaryPath = null;
            try
            {
                Directory.CreateDirectory(RootPath);
                PruneCore(_timeProvider.GetUtcNow());
                var record = Project(exception, source, isFatal, applicationVersion, releaseChannel);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
                if (bytes.Length > MaximumRecordBytes) return new CrashWriteResult(CrashWriteStatus.Rejected);
                if (!EnsureCapacity(bytes.Length, _timeProvider.GetUtcNow())) return new CrashWriteResult(CrashWriteStatus.Rejected);

                var fileName = $"crash-{record.OccurredAtUtc:yyyyMMddTHHmmssfffZ}-{record.Id:N}.json";
                var targetPath = Path.Combine(RootPath, fileName);
                temporaryPath = targetPath + ".tmp";
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(true);
                }
                File.Move(temporaryPath, targetPath);
                return new CrashWriteResult(CrashWriteStatus.Written, fileName);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
            {
                if (temporaryPath is not null) TryDelete(temporaryPath);
                return new CrashWriteResult(CrashWriteStatus.Failed);
            }
        }
    }

    public CrashListResult List()
    {
        lock (_sync)
        {
            if (!Directory.Exists(RootPath)) return new CrashListResult([], false, 0);
            var maintenanceFailed = false;
            try { maintenanceFailed = !PruneCore(_timeProvider.GetUtcNow()); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                maintenanceFailed = true;
            }

            var records = new List<CrashRecordSummary>();
            long total = 0;
            try
            {
                foreach (var file in EnumerateManagedFiles())
                {
                    var info = new FileInfo(file);
                    total += info.Length;
                    var read = ReadCore(file);
                    records.Add(read.Record is { } record
                        ? new CrashRecordSummary(info.Name, CrashReadStatus.Available, record.Id,
                            record.OccurredAtUtc, record.Exception.Type, record.IsFatal, info.Length)
                        : new CrashRecordSummary(info.Name, CrashReadStatus.Corrupted, null, null, null, null, info.Length));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                maintenanceFailed = true;
            }

            return new CrashListResult(records
                .OrderByDescending(record => record.OccurredAtUtc ?? DateTimeOffset.MinValue)
                .ThenBy(record => record.FileName, StringComparer.Ordinal)
                .ToArray(), maintenanceFailed, total);
        }
    }

    public CrashReadResult Read(string fileName)
    {
        if (!IsManagedName(fileName)) return new CrashReadResult(CrashReadStatus.Corrupted, null);
        lock (_sync) return ReadCore(Path.Combine(RootPath, fileName));
    }

    public CrashClearResult Clear()
    {
        lock (_sync)
        {
            if (!Directory.Exists(RootPath)) return new CrashClearResult(0, 0, true);
            var deleted = 0;
            var completed = true;
            foreach (var file in EnumerateManagedFiles(includeTemporary: true).ToArray())
            {
                try { File.Delete(file); deleted++; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { completed = false; }
            }
            var remaining = EnumerateManagedFiles(includeTemporary: true).Count();
            return new CrashClearResult(deleted, remaining, completed && remaining == 0);
        }
    }

    private CrashRecord Project(Exception exception, CrashSource source, bool isFatal, string version, string channel)
    {
        var remainingNodes = MaximumExceptionNodes;
        var truncated = false;
        var node = ProjectException(exception, 0, ref remainingNodes, ref truncated);
        return new CrashRecord(1, Guid.NewGuid(), _timeProvider.GetUtcNow(), Limit(version), Limit(channel),
            Limit(RuntimeInformation.OSDescription), Limit(RuntimeInformation.FrameworkDescription),
            RuntimeInformation.ProcessArchitecture.ToString(), source, isFatal, node, truncated);
    }

    private static CrashExceptionNode ProjectException(
        Exception exception, int depth, ref int remainingNodes, ref bool recordTruncated)
    {
        remainingNodes--;
        var nodeTruncated = false;
        var frames = new List<CrashFrame>();
        try
        {
            var sourceFrames = new StackTrace(exception, false).GetFrames() ?? [];
            foreach (var frame in sourceFrames.Take(MaximumFrames))
            {
                var method = frame.GetMethod();
                frames.Add(ProjectFrame(method));
            }
            if (sourceFrames.Length > MaximumFrames) nodeTruncated = true;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        {
            frames.Clear();
            frames.Add(new CrashFrame("[未知类型]", "[未知方法]"));
            nodeTruncated = true;
        }

        var inners = new List<CrashExceptionNode>();
        IEnumerable<Exception> sourceInners = exception is AggregateException aggregate
            ? aggregate.InnerExceptions
            : exception.InnerException is { } inner ? [inner] : [];
        foreach (var innerException in sourceInners)
        {
            if (depth + 1 >= MaximumExceptionDepth || remainingNodes <= 0)
            {
                nodeTruncated = true;
                break;
            }
            inners.Add(ProjectException(innerException, depth + 1, ref remainingNodes, ref recordTruncated));
        }

        recordTruncated |= nodeTruncated;
        return new CrashExceptionNode(SafeExceptionType(exception), exception.HResult, frames, inners, nodeTruncated);
    }

    private static CrashFrame ProjectFrame(MethodBase? method)
    {
        try
        {
            if (method?.DeclaringType?.Assembly.GetName().Name is not string assemblyName ||
                !IsTrustedAssembly(assemblyName))
                return new CrashFrame("[外部类型]", "[外部方法]");
            return new CrashFrame(SafeTypeName(method.DeclaringType), Limit(StripGenericArity(method.Name)));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return new CrashFrame("[未知类型]", "[未知方法]");
        }
    }

    private static string SafeExceptionType(Exception exception)
    {
        try
        {
            var type = exception.GetType();
            var assemblyName = type.Assembly.GetName().Name;
            return assemblyName is not null && IsTrustedAssembly(assemblyName) ? SafeTypeName(type) : "[外部异常]";
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        {
            return "[未知异常]";
        }
    }

    private static string SafeTypeName(Type type)
    {
        if (type.IsGenericType) type = type.GetGenericTypeDefinition();
        return Limit(StripGenericArity(type.FullName ?? type.Name));
    }

    private static bool IsTrustedAssembly(string name) =>
        name.Equals("TalosDesk.App", StringComparison.Ordinal) ||
        name.Equals("TalosDesk.Core", StringComparison.Ordinal) ||
        name.Equals("System.Private.CoreLib", StringComparison.Ordinal) ||
        name.StartsWith("System.", StringComparison.Ordinal) ||
        name.StartsWith("Microsoft.", StringComparison.Ordinal) ||
        name.Equals("PresentationFramework", StringComparison.Ordinal) ||
        name.Equals("WindowsBase", StringComparison.Ordinal);

    private static string StripGenericArity(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '`') { builder.Append(value[index]); continue; }
            while (index + 1 < value.Length && char.IsDigit(value[index + 1])) index++;
        }
        return builder.ToString();
    }

    private static string Limit(string? value)
    {
        value ??= "[未知]";
        return value.Length <= MaximumStringLength ? value : value[..MaximumStringLength];
    }

    private bool EnsureCapacity(int newBytes, DateTimeOffset now)
    {
        PruneCore(now);
        var files = GetManagedFileInfos();
        while (files.Count >= MaximumRecordCount || files.Sum(file => file.Length) + newBytes > MaximumTotalBytes)
        {
            if (files.Count == 0) return false;
            if (!TryDelete(files[0].FullName)) return false;
            files.RemoveAt(0);
        }
        return true;
    }

    private bool PruneCore(DateTimeOffset now)
    {
        if (!Directory.Exists(RootPath)) return true;
        var completed = true;
        foreach (var temp in EnumerateManagedFiles(includeRecords: false, includeTemporary: true).ToArray())
            completed &= TryDelete(temp);

        var files = GetManagedFileInfos();
        foreach (var file in files.Where(file => now - file.LastWriteTimeUtc > Retention).ToArray())
        {
            if (TryDelete(file.FullName)) files.Remove(file); else completed = false;
        }
        while (files.Count > MaximumRecordCount || files.Sum(file => file.Length) > MaximumTotalBytes)
        {
            if (!TryDelete(files[0].FullName)) { completed = false; break; }
            files.RemoveAt(0);
        }
        return completed;
    }

    private List<FileInfo> GetManagedFileInfos() => EnumerateManagedFiles()
        .Select(path => new FileInfo(path))
        .OrderBy(file => file.LastWriteTimeUtc)
        .ThenBy(file => file.Name, StringComparer.Ordinal)
        .ToList();

    private IEnumerable<string> EnumerateManagedFiles(bool includeRecords = true, bool includeTemporary = false)
    {
        if (!Directory.Exists(RootPath)) yield break;
        foreach (var path in Directory.EnumerateFiles(RootPath, "crash-*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if ((includeRecords && IsManagedName(name)) || (includeTemporary && IsManagedTemporaryName(name)))
                yield return path;
        }
    }

    private CrashReadResult ReadCore(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumRecordBytes ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
                return new CrashReadResult(CrashReadStatus.Corrupted, null);
            var record = JsonSerializer.Deserialize<CrashRecord>(File.ReadAllBytes(path), JsonOptions);
            return Validate(record) ? new CrashReadResult(CrashReadStatus.Available, record) :
                new CrashReadResult(CrashReadStatus.Corrupted, null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return new CrashReadResult(CrashReadStatus.Corrupted, null);
        }
    }

    private static bool Validate(CrashRecord? record)
    {
        if (record is not { SchemaVersion: 1 } || record.Id == Guid.Empty ||
            record.ApplicationVersion is null || record.ReleaseChannel is null || record.OperatingSystem is null ||
            record.Runtime is null || record.Architecture is null || record.Exception is null ||
            record.ApplicationVersion.Length > MaximumStringLength || record.ReleaseChannel.Length > MaximumStringLength ||
            record.OperatingSystem.Length > MaximumStringLength || record.Runtime.Length > MaximumStringLength ||
            record.Architecture.Length > MaximumStringLength) return false;
        var nodeCount = 0;
        return ValidateNode(record.Exception, 0, ref nodeCount);
    }

    private static bool ValidateNode(CrashExceptionNode? node, int depth, ref int count)
    {
        if (node is null || node.Type is null || node.Frames is null || node.InnerExceptions is null ||
            depth >= MaximumExceptionDepth || ++count > MaximumExceptionNodes ||
            node.Type.Length > MaximumStringLength || node.Frames.Count > MaximumFrames) return false;
        if (node.Frames.Any(frame => frame is null || frame.Type is null || frame.Method is null ||
            frame.Type.Length > MaximumStringLength || frame.Method.Length > MaximumStringLength)) return false;
        foreach (var inner in node.InnerExceptions)
            if (!ValidateNode(inner, depth + 1, ref count)) return false;
        return true;
    }

    private static bool IsManagedName(string fileName) =>
        fileName.StartsWith("crash-", StringComparison.Ordinal) &&
        fileName.EndsWith(".json", StringComparison.Ordinal) &&
        fileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;

    private static bool IsManagedTemporaryName(string fileName) =>
        fileName.StartsWith("crash-", StringComparison.Ordinal) &&
        fileName.EndsWith(".json.tmp", StringComparison.Ordinal) &&
        fileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;

    private static bool TryDelete(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0) info.Delete();
            return !File.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
