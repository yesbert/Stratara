using Stratara.Shared.Merging.ChangeTracking;

namespace Stratara.Shared.Tests.Merging;

public class ChangeMergerTests
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
    public void ApplyChanges_NoConflicts_UsesCurrentValues()
    {
        var source = new Entity { Name = "Alice", Age = 30, Email = "alice@test.com" };
        var current = new Entity { Name = "Alice Updated", Age = 31, Email = "alice-new@test.com" };
        var changes = new Changes { Name = "Alice", Age = 30, Email = "alice@test.com" };

        var result = ChangeMerger<Entity, Changes>.ApplyChanges(source, current, changes);

        Assert.Equal("Alice Updated", result.MergedChanges.Name);
        Assert.Equal(31, result.MergedChanges.Age);
        Assert.Equal("alice-new@test.com", result.MergedChanges.Email);
    }

    [Fact]
    public void ApplyChanges_AllChanged_UsesChangeValues()
    {
        var source = new Entity { Name = "Alice", Age = 30, Email = "alice@test.com" };
        var current = new Entity { Name = "Alice Server", Age = 31, Email = "server@test.com" };
        var changes = new Changes { Name = "Alice Client", Age = 25, Email = "client@test.com" };

        var result = ChangeMerger<Entity, Changes>.ApplyChanges(source, current, changes);

        Assert.Equal("Alice Client", result.MergedChanges.Name);
        Assert.Equal(25, result.MergedChanges.Age);
        Assert.Equal("client@test.com", result.MergedChanges.Email);
    }

    [Fact]
    public void ApplyChanges_MixedConflicts_ResolvesCorrectly()
    {
        var source = new Entity { Name = "Alice", Age = 30, Email = "alice@test.com" };
        var current = new Entity { Name = "Alice Server", Age = 31, Email = "server@test.com" };
        var changes = new Changes { Name = "Alice Client", Age = 30, Email = "alice@test.com" };

        var result = ChangeMerger<Entity, Changes>.ApplyChanges(source, current, changes);

        Assert.Equal("Alice Client", result.MergedChanges.Name);
        Assert.Equal(31, result.MergedChanges.Age);
        Assert.Equal("server@test.com", result.MergedChanges.Email);
    }

    [Fact]
    public void ApplyChanges_NullValues_HandledCorrectly()
    {
        var source = new Entity { Name = "Alice", Age = 30, Email = null };
        var current = new Entity { Name = "Alice", Age = 30, Email = "new@test.com" };
        var changes = new Changes { Name = "Alice", Age = 30, Email = null };

        var result = ChangeMerger<Entity, Changes>.ApplyChanges(source, current, changes);

        Assert.Equal("new@test.com", result.MergedChanges.Email);
    }

    [Fact]
    public void ApplyChanges_EmptyDifferences_WhenNoConflicts()
    {
        var source = new Entity { Name = "Alice", Age = 30, Email = "alice@test.com" };
        var current = new Entity { Name = "Alice Updated", Age = 31, Email = "updated@test.com" };
        var changes = new Changes { Name = "Alice", Age = 30, Email = "alice@test.com" };

        var result = ChangeMerger<Entity, Changes>.ApplyChanges(source, current, changes);

        Assert.Empty(result.Differences);
    }

    [Fact]
    public void ApplyChanges_TracksDifferences_WhenChanged()
    {
        var source = new Entity { Name = "Alice", Age = 30, Email = "alice@test.com" };
        var current = new Entity { Name = "Alice Server", Age = 31, Email = "server@test.com" };
        var changes = new Changes { Name = "Alice Client", Age = 30, Email = "alice@test.com" };

        var result = ChangeMerger<Entity, Changes>.ApplyChanges(source, current, changes);

        Assert.Single(result.Differences);
        Assert.Equal("Name", result.Differences[0].PropertyName);
    }

    [Fact]
    public void ApplyChanges_DifferenceContainsAllValues()
    {
        var source = new Entity { Name = "Alice", Age = 30, Email = "alice@test.com" };
        var current = new Entity { Name = "Alice Server", Age = 31, Email = "server@test.com" };
        var changes = new Changes { Name = "Alice Client", Age = 25, Email = "client@test.com" };

        var result = ChangeMerger<Entity, Changes>.ApplyChanges(source, current, changes);

        var nameDiff = result.Differences.First(d => d.PropertyName == "Name");
        Assert.Equal("Alice", nameDiff.SourceValue);
        Assert.Equal("Alice Server", nameDiff.CurrentValue);
        Assert.Equal("Alice Client", nameDiff.ChangeValue);
    }

    [Fact]
    public void ApplyChanges_PropertyNotOnBase_UsesChangeValue()
    {
        var source = new Entity { Name = "Alice", Age = 30 };
        var current = new Entity { Name = "Alice Updated", Age = 31 };
        var changes = new PartialChanges { Name = "Alice Client", Extra = "ExtraValue" };

        var result = ChangeMerger<Entity, PartialChanges>.ApplyChanges(source, current, changes);

        Assert.Equal("ExtraValue", result.MergedChanges.Extra);
    }

    [Fact]
    public void ApplyChanges_NullSource_ThrowsArgumentNullException()
    {
        var current = new Entity { Name = "Alice" };
        var changes = new Changes { Name = "Alice" };

        Assert.Throws<ArgumentNullException>(() =>
            ChangeMerger<Entity, Changes>.ApplyChanges(null!, current, changes));
    }

    [Fact]
    public void ApplyChanges_NullCurrent_ThrowsArgumentNullException()
    {
        var source = new Entity { Name = "Alice" };
        var changes = new Changes { Name = "Alice" };

        Assert.Throws<ArgumentNullException>(() =>
            ChangeMerger<Entity, Changes>.ApplyChanges(source, null!, changes));
    }

    [Fact]
    public void ApplyChanges_NullChanges_ThrowsArgumentNullException()
    {
        var source = new Entity { Name = "Alice" };
        var current = new Entity { Name = "Alice" };

        Assert.Throws<ArgumentNullException>(() =>
            ChangeMerger<Entity, Changes>.ApplyChanges(source, current, null!));
    }

    [Fact]
    public void ApplyChanges_StringProperties_ComparedCorrectly()
    {
        var source = new Entity { Name = "Original", Age = 30, Email = "test@test.com" };
        var current = new Entity { Name = "Original", Age = 30, Email = "test@test.com" };
        var changes = new Changes { Name = "Changed", Age = 30, Email = "test@test.com" };

        var result = ChangeMerger<Entity, Changes>.ApplyChanges(source, current, changes);

        Assert.Equal("Changed", result.MergedChanges.Name);
        Assert.Single(result.Differences);
    }

    [Fact]
    public void ApplyChanges_IntProperties_ComparedCorrectly()
    {
        var source = new Entity { Name = "Alice", Age = 30, Email = "test@test.com" };
        var current = new Entity { Name = "Alice", Age = 30, Email = "test@test.com" };
        var changes = new Changes { Name = "Alice", Age = 99, Email = "test@test.com" };

        var result = ChangeMerger<Entity, Changes>.ApplyChanges(source, current, changes);

        Assert.Equal(99, result.MergedChanges.Age);
        Assert.Single(result.Differences);
    }

    [Fact]
    public void ApplyChanges_ConcurrentEdits_MergesCorrectly()
    {
        var source = new Entity { Name = "Alice", Age = 30, Email = "alice@test.com" };
        var current = new Entity { Name = "Alice", Age = 31, Email = "alice@test.com" };
        var changes = new Changes { Name = "Alice Smith", Age = 30, Email = "alice@test.com" };

        var result = ChangeMerger<Entity, Changes>.ApplyChanges(source, current, changes);

        Assert.Equal("Alice Smith", result.MergedChanges.Name);
        Assert.Equal(31, result.MergedChanges.Age);
        Assert.Equal("alice@test.com", result.MergedChanges.Email);
    }

    [Fact]
    public void ApplyChanges_AllFieldsSame_NoChanges()
    {
        var source = new Entity { Name = "Alice", Age = 30, Email = "alice@test.com" };
        var current = new Entity { Name = "Alice", Age = 30, Email = "alice@test.com" };
        var changes = new Changes { Name = "Alice", Age = 30, Email = "alice@test.com" };

        var result = ChangeMerger<Entity, Changes>.ApplyChanges(source, current, changes);

        Assert.Empty(result.Differences);
        Assert.Equal("Alice", result.MergedChanges.Name);
        Assert.Equal(30, result.MergedChanges.Age);
        Assert.Equal("alice@test.com", result.MergedChanges.Email);
    }
}
