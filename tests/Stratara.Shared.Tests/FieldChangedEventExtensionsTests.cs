using Stratara.Shared.EventSourcing;
using Stratara.Shared.EventSourcing.Extensions;

namespace Stratara.Shared.Tests;

public class FieldChangedEventExtensionsTests
{
    private class Person
    {
        public string? FirstName { get; set; }
        public int Age { get; set; }
    }

    [Fact]
    public void ApplyPropertyChanged_Updates_String_Property()
    {
        // Arrange
        var person = new Person { FirstName = "Old" };
        var evt = new FieldChangedEvent<Person>(nameof(Person.FirstName), "New");

        // Act
        person.ApplyPropertyChanged(evt);

        // Assert
        Assert.Equal("New", person.FirstName);
    }

    [Fact]
    public void ApplyPropertyChanged_Updates_Value_Type_Property()
    {
        // Arrange
        var person = new Person { Age = 10 };
        var evt = new FieldChangedEvent<Person>(nameof(Person.Age), 42);

        // Act
        person.ApplyPropertyChanged(evt);

        // Assert
        Assert.Equal(42, person.Age);
    }

    [Fact]
    public void ApplyPropertyChanged_Throws_On_Unknown_Property()
    {
        // Arrange
        var person = new Person();
        var evt = new FieldChangedEvent<Person>("DoesNotExist", 1);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => person.ApplyPropertyChanged(evt));
    }
}
