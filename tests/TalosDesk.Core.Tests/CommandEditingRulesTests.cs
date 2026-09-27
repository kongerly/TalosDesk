using TalosDesk.Core.Editing;

namespace TalosDesk.Core.Tests;

[TestClass]
public sealed class CommandEditingRulesTests
{
    [TestMethod]
    [DataRow('(', ')')]
    [DataRow('[', ']')]
    [DataRow('{', '}')]
    [DataRow('\'', '\'')]
    [DataRow('"', '"')]
    public void RecognizesSupportedPairs(char opening, char closing)
    {
        Assert.IsTrue(CommandEditingRules.TryGetClosingCharacter(opening, out var actual));
        Assert.AreEqual(closing, actual);
        Assert.IsTrue(CommandEditingRules.IsPair(opening, closing));
        Assert.IsTrue(CommandEditingRules.IsClosingCharacter(closing));
    }

    [TestMethod]
    public void NewLineIndentationPreservesWhitespaceAndIndentsAfterOpeningDelimiter()
    {
        Assert.AreEqual("    ", CommandEditingRules.GetNewLineIndentation("    Write-Output value"));
        Assert.AreEqual("        ", CommandEditingRules.GetNewLineIndentation("    if ($ready) {"));
        Assert.AreEqual("    ", CommandEditingRules.GetNewLineIndentation("\tWrite-Output value"));
    }

    [TestMethod]
    public void IndentationPositionContainsOnlyWhitespaceBeforeCaret()
    {
        Assert.IsTrue(CommandEditingRules.IsIndentationPosition("    "));
        Assert.IsTrue(CommandEditingRules.IsIndentationPosition(string.Empty));
        Assert.IsFalse(CommandEditingRules.IsIndentationPosition("  Write"));
    }
}
