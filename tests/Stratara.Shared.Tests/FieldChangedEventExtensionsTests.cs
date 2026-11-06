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
        public Guid Reference { get; set; }
        public Address? Home { get; set; }
    }

    private sealed record Address(string Street, int Number);

    private enum Level
    {
        Junior,
        Senior
    }

    private static readonly string[] TagsAsArray = ["a", "b"];

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
    public void A_collection_of_another_shape_is_applied_like_a_stored_one()
    {
        var person = new Person();

        person.ApplyPropertyChanged(new FieldChangedEvent<Person>(nameof(Person.Tags), TagsAsArray));

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

    private const string ProtectedValue =
        """{"__enc":true,"v":1,"alg":1,"kid":"k","n":"AAAA","t":"AAAA","ct":"AAAA"}""";

    private static FieldChangedEvent<Person> ReadByAnEarlierHost(string propertyName) =>
        JsonSerializer.Deserialize<FieldChangedEvent<Person>>(
            $$"""{"PropertyName":"{{propertyName}}","NewValue":{{ProtectedValue}}}""")!;

    [Fact]
    public void An_absent_value_clears_a_reference_property()
    {
        var person = new Person { FirstName = "Old" };

        person.ApplyPropertyChanged(new FieldChangedEvent<Person>(nameof(Person.FirstName), null));

        Assert.Null(person.FirstName);
    }

    [Fact]
    public void An_absent_value_clears_a_nullable_value_property()
    {
        var person = new Person { ManagerId = Guid.NewGuid() };

        person.ApplyPropertyChanged(new FieldChangedEvent<Person>(nameof(Person.ManagerId), null));

        Assert.Null(person.ManagerId);
    }

    [Fact]
    public void An_absent_value_resets_a_non_nullable_value_property_to_its_default()
    {
        var person = new Person { Age = 36, Reference = Guid.NewGuid(), Level = Level.Senior };

        person.ApplyPropertyChanged(new FieldChangedEvent<Person>(nameof(Person.Age), null));
        person.ApplyPropertyChanged(new FieldChangedEvent<Person>(nameof(Person.Reference), null));
        person.ApplyPropertyChanged(new FieldChangedEvent<Person>(nameof(Person.Level), null));

        Assert.Equal((0, Guid.Empty, Level.Junior), (person.Age, person.Reference, person.Level));
    }

    [Theory]
    [InlineData(nameof(Person.FirstName))]
    [InlineData(nameof(Person.Age))]
    [InlineData(nameof(Person.ManagerId))]
    [InlineData(nameof(Person.Level))]
    [InlineData(nameof(Person.Tags))]
    public void An_earlier_host_fails_loudly_on_a_protected_value_for_a_typed_property(string propertyName)
    {
        var person = new Person();

        Assert.Throws<JsonException>(() => person.ApplyPropertyChanged(ReadByAnEarlierHost(propertyName)));
    }

    [Fact]
    public void An_earlier_host_reads_a_protected_value_for_a_record_property_as_an_empty_record()
    {
        var person = new Person();

        person.ApplyPropertyChanged(ReadByAnEarlierHost(nameof(Person.Home)));

        Assert.Equal(new Address(null!, 0), person.Home);
    }

    [Fact]
    public void An_earlier_host_assigns_a_protected_value_to_an_untyped_property_without_noticing()
    {
        var person = new Person();

        person.ApplyPropertyChanged(ReadByAnEarlierHost(nameof(Person.Extra)));

        Assert.True(Assert.IsType<JsonElement>(person.Extra).TryGetProperty("__enc", out _));
    }
}
