using KaneCode.Models;
using Xunit;

namespace KaneCode.Tests.Models;

public class ReferenceItemTests
{
    [Theory]
    [InlineData(ReferenceKind.Definition, "Definition", "📘")]
    [InlineData(ReferenceKind.Reference, "Reference", "🔎")]
    [InlineData(ReferenceKind.Implementation, "Implementation", "🔧")]
    public void WhenKindVariesThenDisplayPropertiesMatch(ReferenceKind kind, string expectedDisplayName, string expectedIcon)
    {
        ReferenceItem item = new(
            "Demo.Symbol",
            @"C:\Project\Demo.cs",
            "Demo.cs",
            "DemoProject",
            10,
            5,
            42,
            "Demo.Symbol();",
            kind);

        Assert.Equal(expectedDisplayName, item.KindDisplayName);
        Assert.Equal(expectedIcon, item.KindIcon);
    }
}
