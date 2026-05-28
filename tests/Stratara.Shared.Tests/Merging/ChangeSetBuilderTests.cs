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

        Assert.Single(result);
        Assert.Equal("Name", result[0].PropertyName);
    }

    [Fact]
    public void CreateChangeSet_NullValues_HandledCorrectly()
    {
        var source = new Entity { Name = "Alice", Age = 30, Email = null };
        var current = new Entity { Name = "Alice", Age = 30, Email = "new@test.com" };
        var changes = new Changes { Name = "Alice", Age = 30, Email = "changed@test.com" };

        var result = ChangeSetBuilder<Entity, Changes>.CreateChangeSet(source, current, changes);

        Assert.Single(result);
        Assert.Equal("Email", result[0].PropertyName);
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
