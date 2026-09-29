using System.Reflection;
using TalosDesk.Core.Updates;

namespace TalosDesk.App;

internal sealed record ApplicationBuildInfo(
    string DisplayVersion,
    string? Version,
    ReleaseChannel? Channel,
    string? Error)
{
    public bool IsValid => Version is not null && Channel is not null && Error is null;

    public static ApplicationBuildInfo Read(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informationalVersions = assembly.GetCustomAttributes<AssemblyInformationalVersionAttribute>()
            .Select(attribute => attribute.InformationalVersion)
            .ToArray();
        var channelValues = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => string.Equals(attribute.Key, "ReleaseChannel", StringComparison.Ordinal))
            .Select(attribute => attribute.Value)
            .ToArray();

        var displayVersion = informationalVersions.Length == 1 && !string.IsNullOrWhiteSpace(informationalVersions[0])
            ? informationalVersions[0]
            : "未知";
        if (informationalVersions.Length != 1 ||
            !SemanticVersion.TryParse(informationalVersions[0], false, out var version))
        {
            return new ApplicationBuildInfo(displayVersion, null, null, "本机版本信息异常，更新检查不可用。");
        }

        if (channelValues.Length != 1 || channelValues[0] is not string channelValue ||
            channelValue is not ("Stable" or "Preview") ||
            !Enum.TryParse<ReleaseChannel>(channelValue, false, out var channel))
        {
            return new ApplicationBuildInfo(displayVersion, version!.Identity, null, "本机构建渠道信息异常，更新检查不可用。");
        }

        return new ApplicationBuildInfo(displayVersion, version!.Identity, channel, null);
    }
}
