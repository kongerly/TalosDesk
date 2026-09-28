using TalosDesk.Core.Configuration;

namespace TalosDesk.Core.Tests;

[TestClass]
public sealed class CommandEnvironmentVariableEditorTests
{
    [TestMethod]
    public void KeepsProtectedValueWithoutDecryptingAndRenameRequiresNewValue()
    {
        var original = CommandEnvironmentVariableEditor.Apply(null, "API_TOKEN", true, EnvironmentValueEditAction.Replace, "synthetic-value");
        var kept = CommandEnvironmentVariableEditor.Apply(original, "API_TOKEN", true, EnvironmentValueEditAction.KeepExisting);
        var renamed = CommandEnvironmentVariableEditor.Apply(original, "RENAMED_TOKEN", true, EnvironmentValueEditAction.KeepExisting);

        Assert.AreNotSame(original, kept);
        Assert.AreEqual(original.ProtectedValue, kept.ProtectedValue);
        Assert.AreEqual("Required", renamed.ValueState);
        Assert.IsNull(renamed.ProtectedValue);
        Assert.AreEqual("API_TOKEN", original.Name);
        Assert.AreEqual("synthetic-value", SensitiveValueProtector.Unprotect(original.ProtectedValue!));
    }

    [TestMethod]
    public void HandlesEmptyReplacementPlainConversionAndExplicitRemoval()
    {
        var plain = CommandEnvironmentVariableEditor.Apply(null, "APP_MODE", false, EnvironmentValueEditAction.Replace, "current");
        var encrypted = CommandEnvironmentVariableEditor.Apply(plain, "APP_MODE", true, EnvironmentValueEditAction.EncryptExistingPlain);
        var replaced = CommandEnvironmentVariableEditor.Apply(encrypted, "APP_MODE", true, EnvironmentValueEditAction.Replace, "");
        var required = CommandEnvironmentVariableEditor.Apply(replaced, "APP_MODE", true, EnvironmentValueEditAction.Require);
        var backToPlain = CommandEnvironmentVariableEditor.Apply(encrypted, "APP_MODE", false, EnvironmentValueEditAction.Replace, "");

        Assert.AreEqual("current", SensitiveValueProtector.Unprotect(encrypted.ProtectedValue!));
        Assert.AreEqual("", SensitiveValueProtector.Unprotect(replaced.ProtectedValue!));
        Assert.AreEqual("Required", required.ValueState);
        Assert.AreEqual("", backToPlain.Value);
        Assert.IsNull(backToPlain.ProtectedValue);
        Assert.AreEqual("current", plain.Value);
    }

    [TestMethod]
    public void RejectsImplicitSensitiveToPlainAndInvalidNamesOrValues()
    {
        var original = CommandEnvironmentVariableEditor.Apply(null, "API_TOKEN", true, EnvironmentValueEditAction.Require);
        Assert.ThrowsExactly<ArgumentException>(() => CommandEnvironmentVariableEditor.Apply(original, "API_TOKEN", false,
            EnvironmentValueEditAction.KeepExisting));
        Assert.ThrowsExactly<ArgumentException>(() => CommandEnvironmentVariableEditor.Apply(null, " name", true,
            EnvironmentValueEditAction.Require));
        Assert.ThrowsExactly<ArgumentException>(() => CommandEnvironmentVariableEditor.Apply(null, "x=y", false,
            EnvironmentValueEditAction.Replace, "value"));
        Assert.ThrowsExactly<ArgumentException>(() => CommandEnvironmentVariableEditor.Apply(null, "OK", true,
            EnvironmentValueEditAction.Replace, new string('x', 4097)));
        Assert.ThrowsExactly<ArgumentException>(() => CommandEnvironmentVariableEditor.Apply(null, "OK", false,
            EnvironmentValueEditAction.Replace, "bad\0value"));
    }
}
