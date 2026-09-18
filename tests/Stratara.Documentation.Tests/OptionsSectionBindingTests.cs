using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Session;
using Stratara.Identity.AspNetCore.Authentication;
using Stratara.Outbox.RabbitMQ.Outbox;
using Stratara.Outbox.RabbitMQ.Projections;
using Stratara.Projections.Services;
using Stratara.Sagas.Services;
using Stratara.Security;
using Stratara.Shared.EventSourcing;
using Stratara.Shared.Messaging;

namespace Stratara.Documentation.Tests;

/// <summary>
/// Every public options type that declares the configuration section it belongs to is read from that
/// section by the registration that adds it — directly and through the composites. Two types declared a
/// section for several releases and were bound by nothing: <c>"SessionContext": { "AllowTenantHeader": true }</c>
/// and <c>"ProjectionReplay": { "LeaseSeconds": 600 }</c> did nothing. A new options type that declares a
/// section and is not listed here fails <see cref="EveryOptionsTypeWithASection_HasARegistrationCase"/>.
/// </summary>
public class OptionsSectionBindingTests
{
    private const string ServiceBusConnectionString =
        "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=v";

    private static readonly IReadOnlyList<BindingCase> Cases =
    [
        Case<SessionContextOptions>("AddSessionContext()", "AllowTenantHeader", "True",
            b => b.Services.AddSessionContext()),
        Case<SessionContextOptions>("AddCommonFrameworkServices()", "AllowTenantHeader", "True",
            b => b.AddCommonFrameworkServices()),

        Case<ProjectionReplayOptions>("AddProjectionReplayState()", "LeaseSeconds", "617",
            b => b.Services.AddProjectionReplayState()),
        Case<ProjectionReplayOptions>("AddOutboxDispatcher()", "LeaseSeconds", "617",
            b => b.Services.AddOutboxDispatcher()),
        Case<ProjectionReplayOptions>("AddEventProjectionServices()", "LeaseSeconds", "617",
            b => b.AddEventProjectionServices()),

        Case<OutboxOptions>("AddOutboxDispatcher()", "BatchSize", "617",
            b => b.Services.AddOutboxDispatcher()),
        Case<OutboxOptions>("AddOutboxWorker(configuration)", "BatchSize", "617",
            b => b.Services.AddOutboxWorker(b.Configuration)),

        Case<MessagingOptions>("AddMessaging()", "Topics:0:Name", "orders",
            b => b.AddMessaging(),
            o => o.Topics.FirstOrDefault()?.Name),
        Case<BusEnvelopeJsonOptions>("AddMessaging()", "MaxDepth", "17",
            b => b.AddMessaging()),
        Case<MessageRetryOptions>("AddMessaging()", "MaxDeliveryAttempts", "17",
            b => b.AddMessaging()),
        Case<MessageRetryOptions>("AddAzureServiceBus(connectionString)", "MaxDeliveryAttempts", "17",
            b => b.Services.AddAzureServiceBus(ServiceBusConnectionString)),

        Case<BusEnvelopeIntegrityOptions>("AddBusEnvelopeIntegrity(configuration)", "Mode", "Permissive",
            b => b.Services.AddBusEnvelopeIntegrity(b.Configuration)),

        Case<EventSourcingOptions>("AddWriteStore(configuration)", null, null,
            b => b.Services.AddWriteStore(b.Configuration)),

        Case<ProjectionOptions>("AddProjectionHandling(configuration)", "BatchSize", "617",
            b => b.Services.AddProjectionHandling(b.Configuration)),

        Case<SagaOptions>("AddSagaHandling(configuration)", "DegreeOfParallelism", "3",
            b => b.Services.AddSagaHandling(b.Configuration)),

        Case<StrataraBlobEncryptionOptions>("AddStrataraBlobEncryption()", "LegacyBlobsCarryPurpose", "True",
            b => b.Services.AddStrataraBlobEncryption()),
        Case<StrataraBlobEncryptionOptions>("AddSecurity()", "LegacyBlobsCarryPurpose", "True",
            b => b.Services.AddSecurity()),
        Case<StrataraBlobEncryptionOptions>("AddStrataraFileKeyStore(configuration)", "LegacyBlobsCarryPurpose", "True",
            b => b.Services.AddStrataraFileKeyStore(b.Configuration)),

        Case<StrataraFileKeyStoreOptions>("AddStrataraFileKeyStore(configuration)", "StorePath", "bound-keystore.json",
            b => b.Services.AddStrataraFileKeyStore(b.Configuration)),

        new(typeof(StrataraJwtBearerOptions), "AddStrataraJwtBearer(configuration)", "Audience", "bound-audience",
            b => b.Services.AddAuthentication().AddStrataraJwtBearer(b.Configuration),
            provider => provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                .Get(JwtBearerDefaults.AuthenticationScheme).Audience),

        new(typeof(StrataraOpenIdConnectOptions), "AddStrataraOpenIdConnect(configuration)", "ClientId", "bound-client",
            b => b.Services.AddAuthentication().AddStrataraOpenIdConnect(b.Configuration),
            provider => provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
                .Get(OpenIdConnectDefaults.AuthenticationScheme).ClientId)
        {
            Supporting = new Dictionary<string, string> { ["Authority"] = "https://issuer.example" },
        },
    ];

    public static TheoryData<string> CaseNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var bindingCase in Cases)
            {
                data.Add(bindingCase.Name);
            }

            return data;
        }
    }

    public static TheoryData<string> OptionsTypes
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var (type, _) in OptionsDeclaringASection())
            {
                data.Add(type.FullName ?? type.Name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(OptionsTypes))]
    public void EveryOptionsTypeWithASection_HasARegistrationCase(string typeName)
    {
        Assert.True(
            Cases.Any(c => (c.Options.FullName ?? c.Options.Name) == typeName),
            $"'{typeName}' declares a configuration section, and no registration is shown to read it. "
            + "Bind the section in the registration that adds the options, and add a case here.");
    }

    [Fact]
    public void TheOptionsTypesAreFound()
    {
        var found = OptionsDeclaringASection().Select(o => o.Type).ToList();

        Assert.Contains(typeof(SessionContextOptions), found);
        Assert.Contains(typeof(ProjectionReplayOptions), found);
        Assert.Contains(typeof(StrataraJwtBearerOptions), found);
        Assert.All(Cases, c => Assert.Contains(c.Options, found));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void TheRegistration_ReadsTheDeclaredSection(string caseName)
    {
        var bindingCase = Cases.Single(c => c.Name == caseName);
        var sectionName = OptionsDeclaringASection().Single(o => o.Type == bindingCase.Options).SectionName;

        if (bindingCase.Key is null || bindingCase.Value is null)
        {
            AssertCarriesNoSetting(bindingCase, sectionName);
            return;
        }

        var builder = Host.CreateEmptyApplicationBuilder(null);
        var settings = new Dictionary<string, string?> { [$"{sectionName}:{bindingCase.Key}"] = bindingCase.Value };
        foreach (var (key, value) in bindingCase.Supporting)
        {
            settings[$"{sectionName}:{key}"] = value;
        }

        builder.Configuration.AddInMemoryCollection(settings);

        bindingCase.Register(builder);
        using var provider = builder.Services.BuildServiceProvider();

        Assert.True(
            bindingCase.Value == bindingCase.Read(provider),
            $"{bindingCase.Name}: '{sectionName}:{bindingCase.Key}' = '{bindingCase.Value}' in the host's configuration did not arrive.");
    }

    /// <summary>
    /// A type that carries no setting yet has nothing to observe, so the registration is held to binding
    /// the options from configuration at all — and the case is held to naming a setting as soon as the
    /// type gains one.
    /// </summary>
    private static void AssertCarriesNoSetting(BindingCase bindingCase, string sectionName)
    {
        Assert.DoesNotContain(bindingCase.Options.GetProperties(BindingFlags.Public | BindingFlags.Instance), p => p.CanWrite);

        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [$"{sectionName}:Unused"] = "1" });
        bindingCase.Register(builder);

        var configureType = typeof(IConfigureOptions<>).MakeGenericType(bindingCase.Options);
        var fromConfiguration = typeof(NamedConfigureFromConfigurationOptions<>).MakeGenericType(bindingCase.Options);
        Assert.Contains(builder.Services, d => d.ServiceType == configureType && fromConfiguration.IsInstanceOfType(d.ImplementationInstance));
    }

    private static IEnumerable<(Type Type, string SectionName)> OptionsDeclaringASection()
    {
        foreach (var type in FrameworkSurface.ExportedTypes)
        {
            foreach (var fieldName in new[] { "SectionName", "DefaultSectionName" })
            {
                var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (field?.IsLiteral == true && field.GetRawConstantValue() is string value && value.Length > 0)
                {
                    yield return (type, value);
                    break;
                }
            }
        }
    }

    private static BindingCase Case<TOptions>(
        string registration,
        string? key,
        string? value,
        Action<IHostApplicationBuilder> register,
        Func<TOptions, string?>? read = null)
        where TOptions : class
    {
        var property = key is null ? null : typeof(TOptions).GetProperty(key);
        return new BindingCase(typeof(TOptions), registration, key, value, register, provider =>
        {
            var options = provider.GetRequiredService<IOptions<TOptions>>().Value;
            return read is not null
                ? read(options)
                : Convert.ToString(property?.GetValue(options), CultureInfo.InvariantCulture);
        });
    }

    private sealed record BindingCase(
        Type Options,
        string Registration,
        string? Key,
        string? Value,
        Action<IHostApplicationBuilder> Register,
        Func<IServiceProvider, string?> Read)
    {
        public IReadOnlyDictionary<string, string> Supporting { get; init; } = new Dictionary<string, string>();

        public string Name => $"{Options.Name} via {Registration}";
    }
}
