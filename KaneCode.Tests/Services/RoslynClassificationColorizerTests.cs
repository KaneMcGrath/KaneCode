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

    [Fact]
    public void WhenSetClassifiedSpansReceivesUnsortedInputThenSpansAreSorted()
    {
        RoslynClassificationColorizer colorizer = new(new RoslynWorkspaceService());

        // Deliberately out of document order; the colorizer's binary-search lookup
        // requires sorted spans, so it must sort defensively.
        IReadOnlyList<ClassifiedSpan> unsorted =
        [
            new ClassifiedSpan(new TextSpan(20, 5), ClassificationTypeNames.ClassName),
            new ClassifiedSpan(new TextSpan(0, 4), ClassificationTypeNames.Keyword),
            new ClassifiedSpan(new TextSpan(10, 3), ClassificationTypeNames.MethodName)
        ];

        colorizer.SetClassifiedSpans(unsorted);

        IReadOnlyList<ClassifiedSpan> stored = GetClassifiedSpans(colorizer);

        Assert.Equal(3, stored.Count);
        Assert.True(stored[0].TextSpan.Start < stored[1].TextSpan.Start);
        Assert.True(stored[1].TextSpan.Start < stored[2].TextSpan.Start);
        Assert.Equal(0, stored[0].TextSpan.Start);
        Assert.Equal(10, stored[1].TextSpan.Start);
        Assert.Equal(20, stored[2].TextSpan.Start);
    }

    [Fact]
    public void WhenSetClassifiedSpansReceivesSortedInputThenOrderIsPreserved()
    {
        RoslynClassificationColorizer colorizer = new(new RoslynWorkspaceService());

        IReadOnlyList<ClassifiedSpan> sorted =
        [
            new ClassifiedSpan(new TextSpan(0, 4), ClassificationTypeNames.Keyword),
            new ClassifiedSpan(new TextSpan(10, 3), ClassificationTypeNames.MethodName)
        ];

        colorizer.SetClassifiedSpans(sorted);

        IReadOnlyList<ClassifiedSpan> stored = GetClassifiedSpans(colorizer);

        Assert.Same(sorted, stored);
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
