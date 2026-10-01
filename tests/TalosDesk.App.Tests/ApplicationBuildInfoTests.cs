using System.Reflection;
using System.Reflection.Emit;
using TalosDesk.Core.Updates;

namespace TalosDesk.App.Tests;

[TestClass]
public sealed class ApplicationBuildInfoTests
{
    [TestMethod]
    public void AppAssemblyCarriesValidStableMetadata()
    {
        var info = ApplicationBuildInfo.Read(typeof(App).Assembly);

        Assert.IsTrue(info.IsValid);
        Assert.AreEqual("0.2.0", info.Version);
        Assert.AreEqual(ReleaseChannel.Stable, info.Channel);
    }

    [TestMethod]
    public void MissingDuplicateOrInvalidMetadataIsRejected()
    {
        var missing = ApplicationBuildInfo.Read(CreateAssembly("0.1.1"));
        var duplicate = ApplicationBuildInfo.Read(CreateAssembly("0.1.1", "Preview", "Stable"));
        var invalidVersion = ApplicationBuildInfo.Read(CreateAssembly("1.2", "Preview"));

        Assert.IsFalse(missing.IsValid);
        Assert.IsFalse(duplicate.IsValid);
        Assert.IsFalse(invalidVersion.IsValid);
    }

    [TestMethod]
    public void ValidBuildMetadataIsAcceptedButExcludedFromComparisonIdentity()
    {
        var info = ApplicationBuildInfo.Read(CreateAssembly("0.1.1+local.7", "Preview"));

        Assert.IsTrue(info.IsValid);
        Assert.AreEqual("0.1.1+local.7", info.DisplayVersion);
        Assert.AreEqual("0.1.1", info.Version);
    }

    private static Assembly CreateAssembly(string version, params string[] channels)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("TalosDesk.BuildInfoTest." + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, [version]));
        foreach (var channel in channels)
        {
            assembly.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!,
                ["ReleaseChannel", channel]));
        }
        return assembly;
    }
}
