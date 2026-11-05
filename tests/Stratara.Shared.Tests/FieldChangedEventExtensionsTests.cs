using System.Text.Json;
using Stratara.Shared.EventSourcing;
using Stratara.Shared.EventSourcing.Extensions;

namespace Stratara.Shared.Tests;

public class FieldChangedEventExtensionsTests
{
    private class Person
    {
        public string? FirstName { get; set; }
        public int Age { get; set; }
        public Guid? ManagerId { get; set; }
        public Level Level { get; set; }
        public List<string> Tags { get; set; } = [];
        public object? Extra { get; set; }
    }

    private enum Level
    {
        Junior,
        Senior
    }

    private static FieldChangedEvent<Person> Stored(FieldChangedEvent<Person> evt) =>
        JsonSerializer.Deserialize<FieldChangedEvent<Person>>(JsonSerializer.Serialize(evt))!;

    [Fact]
    public void A_stored_string_change_is_replayed()
    {
        var person = new Person { FirstName = "Old" };

        person.ApplyPropertyChanged(Stored(new FieldChangedEvent<Person>(nameof(Person.FirstName), "New")));

        Assert.Equal("New", person.FirstName);
    }

    [Fact]
    public void A_stored_number_change_is_replayed()
    {
        var person = new Person { Age = 10 };

        person.ApplyPropertyChanged(Stored(new FieldChangedEvent<Person>(nameof(Person.Age), 42)));

        Assert.Equal(42, person.Age);
    }

    [Fact]
    public void A_stored_nullable_identifier_change_is_replayed()
    {
        var manager = Guid.NewGuid();
        var person = new Person();

        person.ApplyPropertyChanged(Stored(new FieldChangedEvent<Person>(nameof(Person.ManagerId), manager)));

        Assert.Equal(manager, person.ManagerId);
    }

    [Fact]
    public void A_stored_enum_change_is_replayed()
    {
        var person = new Person();

        person.ApplyPropertyChanged(Stored(new FieldChangedEvent<Person>(nameof(Person.Level), Level.Senior)));

        Assert.Equal(Level.Senior, person.Level);
    }

    [Fact]
    public void A_stored_collection_change_is_replayed()
    {
        var person = new Person();

        person.ApplyPropertyChanged(
            Stored(new FieldChangedEvent<Person>(nameof(Person.Tags), new List<string> { "a", "b" })));

        Assert.Equal(["a", "b"], person.Tags);
    }

    [Fact]
    public void A_stored_change_that_clears_a_field_is_replayed()
    {
        var person = new Person { FirstName = "Old" };

        person.ApplyPropertyChanged(Stored(new FieldChangedEvent<Person>(nameof(Person.FirstName), null)));

        Assert.Null(person.FirstName);
    }

    [Fact]
    public void A_stored_change_to_an_untyped_property_keeps_the_json_value()
    {
        var person = new Person();

        person.ApplyPropertyChanged(Stored(new FieldChangedEvent<Person>(nameof(Person.Extra), "x")));

        Assert.Equal("x", Assert.IsType<JsonElement>(person.Extra).GetString());
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
