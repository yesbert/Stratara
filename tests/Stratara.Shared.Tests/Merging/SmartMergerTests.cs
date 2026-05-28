using Stratara.Shared.Merging.SmartMerging;

namespace Stratara.Shared.Tests.Merging;

public class SmartMergerTests
{
    private sealed class TestCommand
    {
        public string Name { get; set; } = "";
        public int Value { get; set; }
    }

    private sealed class TestAggregate
    {
        public string Name { get; set; } = "";
        public int Value { get; set; }
        public string Extra { get; set; } = "";
    }

    [Fact]
    public void Merge_UnchangedCommand_UsesCurrentValues()
    {
        var originalCmd = new TestCommand { Name = "Original", Value = 10 };
        var source = new TestAggregate { Name = "Original", Value = 10, Extra = "x" };
        var current = new TestAggregate { Name = "Updated", Value = 20, Extra = "y" };

        var result = SmartMerger<TestCommand, TestAggregate>.Merge(originalCmd, source, current);

        Assert.Equal("Updated", result.Name);
        Assert.Equal(20, result.Value);
    }

    [Fact]
    public void Merge_ChangedCommand_PreservesCommandValues()
    {
        var originalCmd = new TestCommand { Name = "Changed", Value = 99 };
        var source = new TestAggregate { Name = "Original", Value = 10 };
        var current = new TestAggregate { Name = "Server", Value = 20 };

        var result = SmartMerger<TestCommand, TestAggregate>.Merge(originalCmd, source, current);

        Assert.Equal("Changed", result.Name);
        Assert.Equal(99, result.Value);
    }

    [Fact]
    public void Merge_MixedChanges_MergesCorrectly()
    {
        var originalCmd = new TestCommand { Name = "Changed", Value = 10 };
        var source = new TestAggregate { Name = "Original", Value = 10 };
        var current = new TestAggregate { Name = "Server", Value = 20 };

        var result = SmartMerger<TestCommand, TestAggregate>.Merge(originalCmd, source, current);

        Assert.Equal("Changed", result.Name);
        Assert.Equal(20, result.Value);
    }

    [Fact]
    public void Merge_PropertyOnlyInAggregate_Ignored()
    {
        var originalCmd = new TestCommand { Name = "Test", Value = 5 };
        var source = new TestAggregate { Name = "Test", Value = 5, Extra = "SourceExtra" };
        var current = new TestAggregate { Name = "Test", Value = 5, Extra = "CurrentExtra" };

        var result = SmartMerger<TestCommand, TestAggregate>.Merge(originalCmd, source, current);

        Assert.Equal("Test", result.Name);
        Assert.Equal(5, result.Value);
    }

    [Fact]
    public void Merge_AllPropertiesChanged_AllFromCommand()
    {
        var originalCmd = new TestCommand { Name = "CmdName", Value = 100 };
        var source = new TestAggregate { Name = "SrcName", Value = 1 };
        var current = new TestAggregate { Name = "CurName", Value = 50 };

        var result = SmartMerger<TestCommand, TestAggregate>.Merge(originalCmd, source, current);

        Assert.Equal("CmdName", result.Name);
        Assert.Equal(100, result.Value);
    }

    [Fact]
    public void Merge_NullPropertyValues_HandledCorrectly()
    {
        var originalCmd = new TestCommand { Name = null!, Value = 0 };
        var source = new TestAggregate { Name = null!, Value = 0 };
        var current = new TestAggregate { Name = "Current", Value = 10 };

        var result = SmartMerger<TestCommand, TestAggregate>.Merge(originalCmd, source, current);

        Assert.Equal("Current", result.Name);
        Assert.Equal(10, result.Value);
    }

    [Fact]
    public void Merge_DifferentPropertyTypes_Ignored()
    {
        var cmd = new CommandWithDifferentType { SharedName = "Test" };
        var source = new AggregateWithDifferentType { SharedName = "Test" };
        var current = new AggregateWithDifferentType { SharedName = "Updated" };

        var result = SmartMerger<CommandWithDifferentType, AggregateWithDifferentType>.Merge(cmd, source, current);

        Assert.Equal("Updated", result.SharedName);
    }

    [Fact]
    public void Merge_EmptyTypes_ReturnsDefaultCommand()
    {
        var cmd = new EmptyCommand();
        var source = new EmptyAggregate();
        var current = new EmptyAggregate();

        var result = SmartMerger<EmptyCommand, EmptyAggregate>.Merge(cmd, source, current);

        Assert.NotNull(result);
    }

    [Fact]
    public void Merge_ComplexScenario_ServerChangedWhileEditing()
    {
        var originalCmd = new TestCommand { Name = "UserEdit", Value = 10 };
        var source = new TestAggregate { Name = "Original", Value = 10, Extra = "e1" };
        var current = new TestAggregate { Name = "Original", Value = 42, Extra = "e2" };

        var result = SmartMerger<TestCommand, TestAggregate>.Merge(originalCmd, source, current);

        Assert.Equal("UserEdit", result.Name);
        Assert.Equal(42, result.Value);
    }

    private sealed class CommandWithDifferentType
    {
        public string SharedName { get; set; } = "";
    }

    private sealed class AggregateWithDifferentType
    {
        public string SharedName { get; set; } = "";
    }

    private sealed class EmptyCommand;

    private sealed class EmptyAggregate;

    private sealed class TypeMismatchCommand
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
    }

    private sealed class TypeMismatchAggregate
    {
        public string Name { get; set; } = "";
        public string Count { get; set; } = "";
    }

    [Fact]
    public void Merge_TypeMismatch_SkipsMismatchedProperty()
    {
        var cmd = new TypeMismatchCommand { Name = "changed", Count = 42 };
        var source = new TypeMismatchAggregate { Name = "original", Count = "ten" };
        var current = new TypeMismatchAggregate { Name = "original", Count = "twenty" };

        var result = SmartMerger<TypeMismatchCommand, TypeMismatchAggregate>.Merge(cmd, source, current);

        Assert.Equal("changed", result.Name);
        Assert.Equal(0, result.Count);
    }

    private sealed class ReadOnlyPropCommand
    {
        public string ReadOnly { get; } = "fixed";
        public int Writable { get; set; }
    }

    private sealed class ReadOnlyPropAggregate
    {
        public string ReadOnly { get; set; } = "";
        public int Writable { get; set; }
    }

    [Fact]
    public void Merge_ReadOnlyCommandProperty_IsSkipped()
    {
        var cmd = new ReadOnlyPropCommand { Writable = 99 };
        var source = new ReadOnlyPropAggregate { ReadOnly = "src", Writable = 1 };
        var current = new ReadOnlyPropAggregate { ReadOnly = "cur", Writable = 1 };

        var result = SmartMerger<ReadOnlyPropCommand, ReadOnlyPropAggregate>.Merge(cmd, source, current);

        Assert.Equal(99, result.Writable);
        Assert.Equal("fixed", result.ReadOnly);
    }
}
