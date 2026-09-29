using System.Text;
using System.Text.Json;

namespace TalosDesk.Core.Diagnostics;

public sealed class DiagnosticSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly UTF8Encoding Utf8 = new(false);

    public DiagnosticSettingsStore(string workspacePath)
    {
        SettingsPath = Path.GetFullPath(workspacePath) + ".diagnostics-settings.json";
    }

    public string SettingsPath { get; }

    public DiagnosticSettingsResult Load()
    {
        if (!File.Exists(SettingsPath))
            return new DiagnosticSettingsResult(new DiagnosticSettings(), DiagnosticSettingsStatus.Available, true);

        try
        {
            var bytes = File.ReadAllBytes(SettingsPath);
            if (bytes.Length > 4096) return Corrupted();
            var settings = JsonSerializer.Deserialize<DiagnosticSettings>(bytes);
            return settings is { SchemaVersion: 1 }
                ? new DiagnosticSettingsResult(settings, DiagnosticSettingsStatus.Available, true)
                : Corrupted();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return Corrupted();
        }
    }

    public DiagnosticSettingsResult Save(bool enabled)
    {
        var settings = new DiagnosticSettings(IsEnabled: enabled);
        var directory = Path.GetDirectoryName(SettingsPath)!;
        var temporaryPath = SettingsPath + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions), Utf8);
            File.Move(temporaryPath, SettingsPath, true);
            return new DiagnosticSettingsResult(settings, DiagnosticSettingsStatus.Available, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDeleteTemporary(temporaryPath);
            return new DiagnosticSettingsResult(settings, DiagnosticSettingsStatus.SaveFailed, true);
        }
    }

    private static DiagnosticSettingsResult Corrupted() =>
        new(new DiagnosticSettings(IsEnabled: false), DiagnosticSettingsStatus.Corrupted, false);

    private static void TryDeleteTemporary(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
