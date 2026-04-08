using KaneCode.Services;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;
using System.Reflection;

namespace KaneCode.Tests.Services;

public class RoslynClassificationColorizerTests
{
    [Fact]
    public void WhenResetIsCalledThenCachedClassificationsAreCleared()
    {
        RoslynClassificationColorizer colorizer = new(new RoslynWorkspaceService());
        IReadOnlyList<ClassifiedSpan> spans = [new ClassifiedSpan(new TextSpan(0, 1), ClassificationTypeNames.ClassName)];
        colorizer.SetClassifiedSpans(spans);

        colorizer.Reset(@"C:\repo\NewFile.cs");

        IReadOnlyList<ClassifiedSpan> result = GetClassifiedSpans(colorizer);
        Assert.Empty(result);
    }

    [Fact]
    public void WhenResetIsCalledWithFilePathThenFilePathIsUpdated()
    {
        RoslynClassificationColorizer colorizer = new(new RoslynWorkspaceService());
        colorizer.FilePath = @"C:\repo\OldFile.cs";

        colorizer.Reset(@"C:\repo\NewFile.cs");

        Assert.Equal(@"C:\repo\NewFile.cs", colorizer.FilePath);
    }

    private static IReadOnlyList<ClassifiedSpan> GetClassifiedSpans(RoslynClassificationColorizer colorizer)
    {
        FieldInfo? field = typeof(RoslynClassificationColorizer).GetField("_classifiedSpans", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);

        object? value = field.GetValue(colorizer);
        IReadOnlyList<ClassifiedSpan> spans = Assert.IsAssignableFrom<IReadOnlyList<ClassifiedSpan>>(value);
        return spans;
    }
}
