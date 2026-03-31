using KaneCode.Infrastructure;

namespace KaneCode.Tests.Infrastructure;

public class RelayCommandTests
{
    [Fact]
    public void WhenConstructedWithNullExecuteThenThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new RelayCommand(null!));
    }

    [Fact]
    public void WhenExecutedThenCallbackIsInvoked()
    {
        object? receivedParameter = null;
        RelayCommand command = new RelayCommand(p => receivedParameter = p);

        command.Execute("test");

        Assert.Equal("test", receivedParameter);
    }

    [Fact]
    public void WhenExecutedWithNullParameterThenCallbackReceivesNull()
    {
        object? receivedParameter = "not null";
        RelayCommand command = new RelayCommand(p => receivedParameter = p);

        command.Execute(null);

        Assert.Null(receivedParameter);
    }

    [Fact]
    public void WhenCanExecuteIsNullThenCanExecuteReturnsTrue()
    {
        RelayCommand command = new RelayCommand(_ => { });

        bool result = command.CanExecute(null);

        Assert.True(result);
    }

    [Fact]
    public void WhenCanExecuteReturnsFalseThenCanExecuteReturnsFalse()
    {
        RelayCommand command = new RelayCommand(_ => { }, _ => false);

        bool result = command.CanExecute(null);

        Assert.False(result);
    }

    [Fact]
    public void WhenCanExecuteReturnsTrueThenCanExecuteReturnsTrue()
    {
        RelayCommand command = new RelayCommand(_ => { }, _ => true);

        bool result = command.CanExecute(null);

        Assert.True(result);
    }

    [Fact]
    public void WhenCanExecuteReceivesParameterThenParameterIsForwarded()
    {
        object? receivedParameter = null;
        RelayCommand command = new RelayCommand(_ => { }, p =>
        {
            receivedParameter = p;
            return true;
        });

        command.CanExecute("hello");

        Assert.Equal("hello", receivedParameter);
    }
}
