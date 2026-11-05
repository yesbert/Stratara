using Stratara.Shared.Merging.ChangeTracking;

namespace Stratara.Shared.Tests.Merging;

public class ChangeSetBuilderTests
{
    private sealed class Entity
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
        public string? Email { get; set; }
    }

    private sealed class Changes
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
        public string? Email { get; set; }
    }

    private sealed class PartialChanges
    {
        public string Name { get; set; } = "";
        public string Extra { get; set; } = "";
    }

    private enum Status
    {
        Draft,
        Active
    }

    private sealed class Order
    {
        public Status Status { get; set; }
        public int Quantity { get; set; }
        public int? Discount { get; set; }
        public bool IsDeleted { get; }
        public List<string> Tags { get; set; } = [];
        public Dictionary<string, int> Stock { get; set; } = [];
        public string Name { get; private set; } = "";

        public static Order Named(string name) => new() { Name = name };
    }

    private sealed class OrderUpdate
    {
        public string Status { get; set; } = "";
        public int? Quantity { get; set; }
        public int Discount { get; set; }
        public bool IsDeleted { get; set; }
        public List<string> Tags { get; set; } = [];
        public Dictionary<string, int> Stock { get; set; } = [];
        public string Name { get; set; } = "";
    }

    [Fact]
    public void A_property_of_an_incompatible_type_is_skipped()
    {
        var order = new Order { Status = Status.Active };

        var result = ChangeSetBuilder<Order, OrderUpdate>.CreateChangeSet(
            order, order, new OrderUpdate { Status = "Active" });

        Assert.DoesNotContain(result, change => change.PropertyName == nameof(Order.Status));
    }

    [Fact]
    public void A_property_the_aggregate_cannot_set_is_skipped()
    {
        var order = new Order();

        var result = ChangeSetBuilder<Order, OrderUpdate>.CreateChangeSet(
            order, order, new OrderUpdate { IsDeleted = true });

        Assert.DoesNotContain(result, change => change.PropertyName == nameof(Order.IsDeleted));
    }

    [Fact]
    public void A_property_with_a_private_setter_takes_part()
    {
        var order = Order.Named("old");

        var result = ChangeSetBuilder<Order, OrderUpdate>.CreateChangeSet(
            order, order, new OrderUpdate { Name = "new" });

        Assert.Contains(result, change => change.PropertyName == nameof(Order.Name));
    }

    [Fact]
    public void A_property_that_differs_only_in_nullability_takes_part()
    {
        var order = new Order { Quantity = 1, Discount = 5 };

        var result = ChangeSetBuilder<Order, OrderUpdate>.CreateChangeSet(
            order, order, new OrderUpdate { Quantity = 2, Discount = 5 });

        var change = Assert.Single(result, change => change.PropertyName is nameof(Order.Quantity) or nameof(Order.Discount));
        Assert.Equal(nameof(Order.Quantity), change.PropertyName);
    }

    [Fact]
    public void A_collection_with_the_same_content_is_not_a_change()
    {
        var source = new Order { Tags = ["a"], Stock = new() { ["x"] = 1 } };
        var current = new Order { Tags = ["a", "b"], Stock = new() { ["x"] = 2 } };

        var result = ChangeSetBuilder<Order, OrderUpdate>.CreateChangeSet(
            source, current, new OrderUpdate { Tags = ["a"], Stock = new() { ["x"] = 1 } });

        Assert.DoesNotContain(result, change => change.PropertyName is nameof(Order.Tags) or nameof(Order.Stock));
    }

    [Theory]
    [InlineData("b,a")]
    [InlineData("a,b,c")]
    [InlineData("a")]
    [InlineData("")]
    public void A_collection_with_different_content_is_a_change(string submitted)
    {
        var order = new Order { Tags = ["a", "b"] };
        var update = new OrderUpdate { Tags = [.. submitted.Split(',', StringSplitOptions.RemoveEmptyEntries)] };

        var result = ChangeSetBuilder<Order, OrderUpdate>.CreateChangeSet(order, order, update);

        Assert.Contains(result, change => change.PropertyName == nameof(Order.Tags));
    }

    [Fact]
    public void A_dictionary_with_a_different_value_is_a_change()
    {
        var order = new Order { Stock = new() { ["x"] = 1 } };

        var result = ChangeSetBuilder<Order, OrderUpdate>.CreateChangeSet(
            order, order, new OrderUpdate { Stock = new() { ["x"] = 2 } });

        Assert.Contains(result, change => change.PropertyName == nameof(Order.Stock));
    }

    [Fact]
    public void CreateChangeSet_NoChanges_ReturnsEmpty()
    {
        var source = new Entity { Name = "Alice", Age = 30, Email = "alice@test.com" };
        var current = new Entity { Name = "Alice Updated", Age = 31, Email = "updated@test.com" };
        var changes = new Changes { Name = "Alice", Age = 30, Email = "alice@test.com" };

        var result = ChangeSetBuilder<Entity, Changes>.CreateChangeSet(source, current, changes);

        Assert.Empty(result);
    }

    [Fact]
    public void CreateChangeSet_AllChanged_ReturnsAllDifferences()
    {
        var source = new Entity { Name = "Alice", Age = 30, Email = "old@test.com" };
        var current = new Entity { Name = "Alice", Age = 30, Email = "old@test.com" };
        var changes = new Changes { Name = "Bob", Age = 25, Email = "new@test.com" };

        var result = ChangeSetBuilder<Entity, Changes>.CreateChangeSet(source, current, changes);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void CreateChangeSet_MixedChanges_ReturnsOnlyChanged()
    {
        var source = new Entity { Name = "Alice", Age = 30, Email = "alice@test.com" };
        var current = new Entity { Name = "Alice Server", Age = 31, Email = "server@test.com" };
        var changes = new Changes { Name = "Alice Client", Age = 30, Email = "alice@test.com" };

        var result = ChangeSetBuilder<Entity, Changes>.CreateChangeSet(source, current, changes);

        var change = Assert.Single(result);
        Assert.Equal("Name", change.PropertyName);
    }

    [Fact]
    public void CreateChangeSet_NullValues_HandledCorrectly()
    {
        var source = new Entity { Name = "Alice", Age = 30, Email = null };
        var current = new Entity { Name = "Alice", Age = 30, Email = "new@test.com" };
        var changes = new Changes { Name = "Alice", Age = 30, Email = "changed@test.com" };

        var result = ChangeSetBuilder<Entity, Changes>.CreateChangeSet(source, current, changes);

        var change = Assert.Single(result);
        Assert.Equal("Email", change.PropertyName);
    }

    [Fact]
    public void CreateChangeSet_NullSource_ThrowsArgumentNull()
    {
        var current = new Entity { Name = "Alice" };
        var changes = new Changes { Name = "Alice" };

        Assert.Throws<ArgumentNullException>(() =>
            ChangeSetBuilder<Entity, Changes>.CreateChangeSet(null!, current, changes));
    }

    [Fact]
    public void CreateChangeSet_NullCurrent_ThrowsArgumentNull()
    {
        var source = new Entity { Name = "Alice" };
        var changes = new Changes { Name = "Alice" };

        Assert.Throws<ArgumentNullException>(() =>
            ChangeSetBuilder<Entity, Changes>.CreateChangeSet(source, null!, changes));
    }

    [Fact]
    public void CreateChangeSet_NullChanges_ThrowsArgumentNull()
    {
        var source = new Entity { Name = "Alice" };
        var current = new Entity { Name = "Alice" };

        Assert.Throws<ArgumentNullException>(() =>
            ChangeSetBuilder<Entity, Changes>.CreateChangeSet(source, current, null!));
    }

    [Fact]
    public void CreateChangeSet_PropertyNotOnBase_Skipped()
    {
        var source = new Entity { Name = "Alice", Age = 30 };
        var current = new Entity { Name = "Alice", Age = 30 };
        var changes = new PartialChanges { Name = "Alice", Extra = "SomeValue" };

        var result = ChangeSetBuilder<Entity, PartialChanges>.CreateChangeSet(source, current, changes);

        Assert.Empty(result);
        Assert.DoesNotContain(result, d => d.PropertyName == "Extra");
    }
}
