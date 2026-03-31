using System.ComponentModel;
using KaneCode.Infrastructure;

namespace KaneCode.Tests.Infrastructure;

public class ObservableObjectTests
{
    private sealed class TestObservable : ObservableObject
    {
        private string _name = string.Empty;

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        private int _count;

        public int Count
        {
            get => _count;
            set => SetProperty(ref _count, value);
        }
    }

    [Fact]
    public void WhenPropertyChangedThenEventIsRaised()
    {
        TestObservable obj = new TestObservable();
        string? changedProperty = null;
        obj.PropertyChanged += (_, e) => changedProperty = e.PropertyName;

        obj.Name = "hello";

        Assert.Equal("Name", changedProperty);
    }

    [Fact]
    public void WhenPropertySetToSameValueThenEventIsNotRaised()
    {
        TestObservable obj = new TestObservable { Name = "hello" };
        bool eventRaised = false;
        obj.PropertyChanged += (_, _) => eventRaised = true;

        obj.Name = "hello";

        Assert.False(eventRaised);
    }

    [Fact]
    public void WhenPropertyChangedThenValueIsUpdated()
    {
        TestObservable obj = new TestObservable();

        obj.Name = "world";

        Assert.Equal("world", obj.Name);
    }

    [Fact]
    public void WhenSetPropertyReturnsTrueThenValueWasChanged()
    {
        TestObservable obj = new TestObservable();

        obj.Name = "changed";

        Assert.Equal("changed", obj.Name);
    }

    [Fact]
    public void WhenMultiplePropertiesChangedThenCorrectEventsAreRaised()
    {
        TestObservable obj = new TestObservable();
        List<string?> changedProperties = new List<string?>();
        obj.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        obj.Name = "test";
        obj.Count = 42;

        Assert.Equal(new[] { "Name", "Count" }, changedProperties);
    }

    [Fact]
    public void WhenNoSubscribersThenSetPropertyDoesNotThrow()
    {
        TestObservable obj = new TestObservable();

        Exception? exception = Record.Exception(() => obj.Name = "safe");

        Assert.Null(exception);
    }
}
