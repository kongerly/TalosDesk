namespace TalosDesk.Core.Diagnostics;

public enum CrashSource
{
    Dispatcher,
    AppDomain,
    UnobservedTask
}

public enum CrashWriteStatus
{
    Written,
    Disabled,
    Rejected,
    Failed
}

public enum CrashReadStatus
{
    Available,
    Corrupted
}

public enum DiagnosticSettingsStatus
{
    Available,
    Corrupted,
    SaveFailed
}

public sealed record DiagnosticSettings(int SchemaVersion = 1, bool IsEnabled = true);

public sealed record DiagnosticSettingsResult(
    DiagnosticSettings Settings,
    DiagnosticSettingsStatus Status,
    bool CanEdit);

public sealed record CrashFrame(string Type, string Method);

public sealed record CrashExceptionNode(
    string Type,
    int HResult,
    IReadOnlyList<CrashFrame> Frames,
    IReadOnlyList<CrashExceptionNode> InnerExceptions,
    bool Truncated);

public sealed record CrashRecord(
    int SchemaVersion,
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    string ApplicationVersion,
    string ReleaseChannel,
    string OperatingSystem,
    string Runtime,
    string Architecture,
    CrashSource Source,
    bool IsFatal,
    CrashExceptionNode Exception,
    bool Truncated);

public sealed record CrashRecordSummary(
    string FileName,
    CrashReadStatus Status,
    Guid? Id,
    DateTimeOffset? OccurredAtUtc,
    string? ExceptionType,
    bool? IsFatal,
    long SizeBytes);

public sealed record CrashWriteResult(CrashWriteStatus Status, string? FileName = null);

public sealed record CrashListResult(
    IReadOnlyList<CrashRecordSummary> Records,
    bool MaintenanceFailed,
    long TotalBytes);

public sealed record CrashReadResult(CrashReadStatus Status, CrashRecord? Record);

public sealed record CrashClearResult(int DeletedCount, int RemainingCount, bool Completed);
