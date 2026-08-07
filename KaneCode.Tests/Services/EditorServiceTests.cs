using KaneCode.Services;

namespace KaneCode.Tests.Services;

public class EditorServiceTests
{
    [Theory]
    [InlineData(@"C:\images\photo.png", true)]
    [InlineData(@"C:\images\photo.jpg", true)]
    [InlineData(@"C:\images\photo.jpeg", true)]
    [InlineData(@"C:\images\photo.gif", true)]
    [InlineData(@"C:\images\photo.bmp", true)]
    [InlineData(@"C:\images\icon.ico", true)]
    [InlineData(@"C:\images\photo.tiff", true)]
    [InlineData(@"C:\images\photo.webp", true)]
    [InlineData(@"C:\images\drawing.svg", true)]
    [InlineData(@"C:\images\PHOTO.PNG", true)] // case-insensitive
    [InlineData(@"C:\docs\readme.md", false)]
    [InlineData(@"C:\src\Program.cs", false)]
    [InlineData(@"C:\data\file.txt", false)]
    [InlineData(@"C:\images\photo", false)] // no extension
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsImageFile_ReturnsExpectedResult(string? path, bool expected)
    {
        bool result = EditorService.IsImageFile(path!);

        Assert.Equal(expected, result);
    }
}
