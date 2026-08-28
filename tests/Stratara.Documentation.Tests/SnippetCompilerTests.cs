namespace Stratara.Documentation.Tests;

public class SnippetCompilerTests
{
    [Fact]
    public void Compile_AcceptsAStatementLevelSnippetThatUsesTheAmbientServiceCollection()
    {
        var snippet = new DocumentationSnippet("fixture.md", 1, "services.AddStrataraValidation();");

        Assert.Empty(SnippetCompiler.Compile(snippet));
    }

    [Fact]
    public void Compile_AcceptsATypeLevelSnippet()
    {
        var snippet = new DocumentationSnippet(
            "fixture.md",
            1,
            "public sealed record PlaceOrder(Guid OrderId) : Stratara.Abstractions.Mediator.ICommand;");

        Assert.Empty(SnippetCompiler.Compile(snippet));
    }

    [Fact]
    public void Compile_ReportsAMethodThatDoesNotExist()
    {
        var snippet = new DocumentationSnippet("fixture.md", 1, "services.AddSomethingThatWasNeverShipped();");

        var errors = SnippetCompiler.Compile(snippet);

        Assert.Contains(errors, error => error.Contains("AddSomethingThatWasNeverShipped", StringComparison.Ordinal));
    }

    [Fact]
    public void Compile_ReportsASignatureThatDoesNotMatch()
    {
        var snippet = new DocumentationSnippet(
            "fixture.md",
            1,
            "Stratara.Abstractions.Messaging.IBusEnvelopeSigner signer = null!;\nsigner.Sign(new object());");

        Assert.NotEmpty(SnippetCompiler.Compile(snippet));
    }

    [Fact]
    public void DeclaresAType_DistinguishesTheTwoShapes()
    {
        Assert.True(SnippetCompiler.DeclaresAType("public sealed record Widget(string Name);"));
        Assert.False(SnippetCompiler.DeclaresAType("var widget = 1;"));
    }

    [Fact]
    public void Compile_ReportsTheSharedKeySnippetThatShippedBefore341()
    {
        var snippet = new DocumentationSnippet(
            "docs/guides/hmac-bus-envelope.md",
            1,
            """
            services.AddBusEnvelopeIntegrity(options =>
            {
                options.Mode = Stratara.Abstractions.Messaging.BusEnvelopeIntegrityMode.Strict;
                options.SharedKey = configuration["BusIntegrity:SharedKey"];
            });
            """);

        Assert.NotEmpty(SnippetCompiler.Compile(snippet));
    }
}
