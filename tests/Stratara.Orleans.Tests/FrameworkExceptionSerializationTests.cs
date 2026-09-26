using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Erasure;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Validation;
using Stratara.Orleans.Hosting;

namespace Stratara.Orleans.Tests;

/// <summary>
/// A framework exception thrown by a handler on one silo reaches a caller on another with its type and its inner
/// exceptions — the provider's failure among them — so a transport or a worker there can tell a concurrency conflict,
/// or a save that committed but could not publish, from any other failure. The round trip is the one Orleans makes
/// between silos.
/// </summary>
public sealed class FrameworkExceptionSerializationTests
{
    private static Exception? RoundTrip(Exception exception, bool registered = true)
    {
        var services = new ServiceCollection().AddSerializer();
        if (registered)
        {
            FrameworkExceptionSerialization.Register(services);
        }

        var serializer = services.BuildServiceProvider().GetRequiredService<Serializer>();
        return serializer.Deserialize<Exception>(serializer.SerializeToArray(exception));
    }

    private static Npgsql.PostgresException UniqueViolation() => new("duplicate key value", "ERROR", "ERROR", "23505");

    [Fact]
    public void A_concurrency_conflict_keeps_its_type_and_its_providers_failure()
    {
        var back = Assert.IsType<ConcurrencyException>(RoundTrip(
            new ConcurrencyException(Guid.NewGuid(), "Order", new DbUpdateException("the save failed", UniqueViolation()))));

        Assert.Equal(string.Empty, back.AggregateTypeName);
        Assert.Contains("Order", back.Message, StringComparison.Ordinal);
        Assert.IsType<Npgsql.PostgresException>(Assert.IsType<DbUpdateException>(back.InnerException).InnerException);
    }

    [Fact]
    public void A_save_that_committed_but_could_not_publish_keeps_its_type_and_its_providers_failure()
    {
        var streamId = Guid.NewGuid();
        var back = Assert.IsType<CommittedEventsNotPublishedException>(RoundTrip(
            new CommittedEventsNotPublishedException([streamId], 1, new Npgsql.NpgsqlException("connection refused"))));

        Assert.Empty(back.StreamIds);
        Assert.Contains(streamId.ToString(), back.Message, StringComparison.Ordinal);
        Assert.IsType<Npgsql.NpgsqlException>(back.InnerException);
    }

    [Fact]
    public void The_framework_exceptions_properties_read_empty_rather_than_null_after_a_crossing()
    {
        Assert.Equal(string.Empty, Assert.IsType<PermissionAuthorizationException>(RoundTrip(new PermissionAuthorizationException("orders.write"))).RequiredPermission);
        Assert.Equal(string.Empty, Assert.IsType<AuthorizationException>(RoundTrip(new AuthorizationException("admin"))).RequiredRole);
        Assert.Empty(Assert.IsType<ErasureIncompleteException>(RoundTrip(
            new ErasureIncompleteException(ErasurePlane.KeyMaterial, new ErasureReport([]), new InvalidOperationException("down")))).Completed.Planes);
        Assert.Equal(string.Empty, Assert.IsType<PrecedingFactMissingException>(RoundTrip(new PrecedingFactMissingException(Guid.NewGuid(), "Updated"))).EventTypeName);
    }

    [Fact]
    public void Without_the_registration_the_type_does_not_cross()
    {
        Assert.ThrowsAny<Exception>(() => RoundTrip(new ConcurrencyException(Guid.NewGuid(), "Order"), registered: false));
    }

    [Fact]
    public void The_execution_models_registrations_register_it()
    {
        var services = new ServiceCollection().AddStrataraOrleansCommandDispatcher();
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<ExceptionSerializationOptions>>().Value;

        Assert.Contains(FrameworkExceptionSerialization.NamespacePrefix, options.SupportedNamespacePrefixes);
        Assert.True(options.SupportedExceptionTypeFilter(typeof(Npgsql.NpgsqlException)));
    }

    [Fact]
    public void A_validation_failure_keeps_which_field_failed_and_how_but_not_the_value_it_was_given()
    {
        var thrown = Throw(new StrataraValidationException(
        [
            new ValidationFailure("Email", "must be an address", "email.format", "jane@", ValidationSeverity.Error),
            new ValidationFailure("Name", "is required"),
        ]));

        var back = Assert.IsType<StrataraValidationException>(RoundTrip(thrown));

        Assert.Collection(back.Failures,
            failure => Assert.Equal(new ValidationFailure("Email", "must be an address", "email.format"), failure),
            failure => Assert.Equal(new ValidationFailure("Name", "is required"), failure));
        Assert.Equal(thrown.Message, back.Message);
        Assert.DoesNotContain("jane@", back.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(Throw), back.StackTrace, StringComparison.Ordinal);
    }

    [Fact]
    public void A_validation_failure_crosses_as_the_inner_exception_of_another()
    {
        var back = Assert.IsType<InvalidOperationException>(RoundTrip(
            new InvalidOperationException("the handler failed", new StrataraValidationException([new ValidationFailure("Name", "is required")]))));

        Assert.Equal("Name", Assert.Single(Assert.IsType<StrataraValidationException>(back.InnerException).Failures).PropertyName);
    }

    private static T Throw<T>(T exception) where T : Exception
    {
        try
        {
            throw exception;
        }
        catch (T thrown)
        {
            return thrown;
        }
    }
}
