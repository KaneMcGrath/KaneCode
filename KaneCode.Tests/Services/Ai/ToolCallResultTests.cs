using KaneCode.Services.Ai;

namespace KaneCode.Tests.Services.Ai;

public class ToolCallResultTests
{
    [Fact]
    public void WhenOkCalledThenSuccessIsTrueAndOutputIsSet()
    {
        ToolCallResult result = ToolCallResult.Ok("file contents");

        Assert.True(result.Success);
        Assert.Equal("file contents", result.Output);
        Assert.Null(result.Error);
    }

    [Fact]
    public void WhenFailCalledThenSuccessIsFalseAndErrorIsSet()
    {
        ToolCallResult result = ToolCallResult.Fail("not found");

        Assert.False(result.Success);
        Assert.Equal("not found", result.Error);
    }

    [Fact]
    public void WhenOkCalledWithEmptyStringThenOutputIsEmpty()
    {
        ToolCallResult result = ToolCallResult.Ok(string.Empty);

        Assert.True(result.Success);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public void WhenFailCalledThenOutputDefaultsToEmpty()
    {
        ToolCallResult result = ToolCallResult.Fail("error");

        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public void WhenTwoOkResultsHaveSameOutputThenTheyAreEqual()
    {
        ToolCallResult result1 = ToolCallResult.Ok("data");
        ToolCallResult result2 = ToolCallResult.Ok("data");

        Assert.Equal(result1, result2);
    }

    [Fact]
    public void WhenTwoFailResultsHaveDifferentErrorsThenTheyAreNotEqual()
    {
        ToolCallResult result1 = ToolCallResult.Fail("error1");
        ToolCallResult result2 = ToolCallResult.Fail("error2");

        Assert.NotEqual(result1, result2);
    }
}
