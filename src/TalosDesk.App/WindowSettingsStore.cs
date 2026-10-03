using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace TalosDesk.App;

internal sealed record WindowSettings(double Width = 1400, double Height = 860, bool IsMaximized = false,
    string? MonitorName = null, double OffsetX = 0, double OffsetY = 0, int SchemaVersion = 1)
{
    [JsonIgnore]
    public bool IsValid => SchemaVersion == 1 && double.IsFinite(Width) && double.IsFinite(Height) &&
        double.IsFinite(OffsetX) && double.IsFinite(OffsetY) && Width > 0 && Height > 0;

    public WindowSettings Fit(Size workArea, Size minimum) => this with
    {
        Width = Math.Clamp(Width, Math.Min(minimum.Width, workArea.Width), workArea.Width),
        Height = Math.Clamp(Height, Math.Min(minimum.Height, workArea.Height), workArea.Height)
    };

    public Rect RestoreBounds(ScreenArea screen, Size minimum)
    {
        var area = screen.WorkArea;
        var fitted = Fit(new Size(area.Width / screen.Scale, area.Height / screen.Scale), minimum);
        var width = fitted.Width * screen.Scale;
        var height = fitted.Height * screen.Scale;
        var sameScreen = string.Equals(MonitorName, screen.Name, StringComparison.OrdinalIgnoreCase);
        var left = sameScreen ? area.Left + OffsetX * screen.Scale : area.Left + (area.Width - width) / 2;
        var top = sameScreen ? area.Top + OffsetY * screen.Scale : area.Top + (area.Height - height) / 2;
        return new Rect(Math.Clamp(left, area.Left, area.Right - width),
            Math.Clamp(top, area.Top, area.Bottom - height), width, height);
    }
}

internal sealed record ScreenArea(string Name, Rect WorkArea, double Scale, bool IsPrimary);

internal sealed class WindowSettingsStore(string workspacePath)
{
    public string FilePath { get; } = Path.GetFullPath(workspacePath) + ".window-settings.json";

    public WindowSettings Load()
    {
        try
        {
            using var stream = File.OpenRead(FilePath);
            if (stream.Length > 4096) return new();
            var settings = JsonSerializer.Deserialize<WindowSettings>(stream);
            return settings is { IsValid: true } ? settings : new();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new();
        }
    }

    public bool Save(WindowSettings settings)
    {
        if (!settings.IsValid) return false;
        var temporaryPath = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings));
            File.Move(temporaryPath, FilePath, true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
