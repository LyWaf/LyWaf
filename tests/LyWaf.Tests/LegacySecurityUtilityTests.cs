using LyWaf.Utils;
using Xunit;

namespace LyWaf.Tests;

public sealed class LegacySecurityUtilityTests
{
    [Theory]
    [InlineData("un/**/ion", "union")]
    [InlineData("un/*aa*/ion", "union")]
    [InlineData("un/*aa*/ion select", "union select")]
    [InlineData("un/*aa*/ion sel/**/ect", "union select")]
    [InlineData("un/*aa*/ion sel/*select*/ect", "union select")]
    [InlineData("un/*aa*/ion\nsel/*select*/ect", "union select")]
    public void SqlCommentsAreRemovedBeforeInspection(string input, string expected) =>
        Assert.Equal(expected, SqlUtil.Unescape(input));

    [Theory]
    [InlineData("union all select")]
    [InlineData("union\nselect")]
    [InlineData("uni/**/on\nselect")]
    public void UnionSelectPatternMatchesNormalizedInput(string input) =>
        Assert.Matches(@"(?:(union(.|\n)*select))", SqlUtil.Unescape(input));
}
