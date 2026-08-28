using Microsoft.Extensions.Options;
using Stratara.Shared.Messaging;

namespace Stratara.Documentation.Tests;

/// <summary>
/// The topic and subscription names an operator provisions are the ones the framework falls back to
/// when nothing is configured. Two guides documented <c>stratara.commands.{appName}</c> instead —
/// a name nothing has ever published to.
/// </summary>
public class MessagingTopicNameTests
{
    private const string RoutingPage = "docs/guides/outbox-setup-rabbitmq.md";

    public static TheoryData<string> Defaults
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in ResolveDefaults())
            {
                data.Add(name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Defaults))]
    public void DefaultName_IsDocumentedInTheRoutingModel(string name)
    {
        var page = DocumentationCorpus.Page(RoutingPage);

        Assert.True(
            DocumentationCorpus.MentionsToken(page, name),
            $"'{name}' is what the framework uses when the Messaging section configures nothing, "
            + $"and {RoutingPage} does not name it. An operator provisioning a broker from that page "
            + "creates entities nothing publishes to.");
    }

    [Fact]
    public void TheDefaultsAreTheOnesTheFrameworkResolves()
    {
        Assert.Contains("command", ResolveDefaults());
        Assert.Contains("event-bundle", ResolveDefaults());
    }

    private static IReadOnlyList<string> ResolveDefaults()
    {
        var identifier = new MessagingIdentifier(Options.Create(new MessagingOptions()));

        return
        [
            identifier.CommandTopic,
            identifier.CommandSubscription,
            identifier.HeavyCommandTopic,
            identifier.HeavyCommandSubscription,
            identifier.EventBundleTopic,
            identifier.EventBundleSubscription,
            identifier.EventBundleSagaSubscription,
            identifier.NotificationTopic,
        ];
    }
}
