using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Stratara.Abstractions.Mediator;

public class Program;

public class MyAggregateMarker;

public class MyCommandMarker;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);

public class DirectoryDbContext(DbContextOptions<DirectoryDbContext> options) : DbContext(options);

public class ApplicationUser : IdentityUser;

public sealed record AccountOpened(Guid AccountId, string Owner, decimal InitialBalance);

public sealed record AmountDeposited(Guid AccountId, decimal Amount);

public sealed record AmountWithdrawn(Guid AccountId, decimal Amount);

public sealed record TransferRequested(Guid FromAccountId, Guid ToAccountId, decimal Amount);

public sealed record AccountDto(Guid AccountId, decimal Balance);

public sealed record CustomerView(Guid CustomerId, string Name);

public sealed class Account : Stratara.Abstractions.Domain.IAggregate
{
    public Account()
    {
    }

    public Account(Guid id, string ownerName, decimal initialBalance)
    {
        Id = id;
        OwnerName = ownerName;
        Balance = initialBalance;
    }

    public Guid Id { get; set; }

    public string OwnerName { get; set; } = string.Empty;

    public decimal Balance { get; set; }

    public void Deposit(decimal amount) => Balance += amount;

    public void Withdraw(decimal amount) => Balance -= amount;
}

public sealed record OpenAccountCommand(Guid AccountId, string OwnerName, decimal InitialBalance) : IQuery<Guid>;

public sealed record DepositCommand(Guid AccountId, decimal Amount) : ICommand;

public sealed record WithdrawCommand(Guid AccountId, decimal Amount) : ICommand;

public sealed record MyCommand(Guid Id) : IQuery<Guid>;

public sealed record UpdateCustomer(Guid CustomerId, string Name) : ICommand;

public sealed record GetBalanceQuery(Guid AccountId) : IQuery<decimal>;

public interface IAccountRepository
{
    Task<Account> GetAsync(Guid accountId, CancellationToken cancellationToken = default);

    Task SaveAsync(Account account, CancellationToken cancellationToken = default);
}

public sealed class InMemoryAccountRepository : IAccountRepository
{
    public Task<Account> GetAsync(Guid accountId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new Account());

    public Task SaveAsync(Account account, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Account Get(Guid accountId) => new();

    public void Save(Account account)
    {
    }
}

public interface IAccountBalanceStore
{
    Task UpsertAsync(Guid accountId, decimal balance, CancellationToken cancellationToken = default);

    Task AddAsync(Guid accountId, decimal amount, CancellationToken cancellationToken = default);

    Task SubtractAsync(Guid accountId, decimal amount, CancellationToken cancellationToken = default);
}

public interface IAccountQueryStore
{
    Task<decimal> GetBalanceAsync(Guid accountId, CancellationToken cancellationToken = default);
}

public sealed class PlatformAdminCrossTenantAuthorizer : Stratara.Abstractions.Multitenancy.ICrossTenantAuthorizer
{
    public ValueTask<bool> IsCrossTenantAllowedAsync(
        Stratara.Contracts.Session.SessionContext session,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
}

public interface IAppMarker;

public class AppWriteDbContext(DbContextOptions<AppWriteDbContext> options)
    : Stratara.EventSourcing.EntityFrameworkCore.WriteStore.WriteDbContext<AppWriteDbContext>(options);

public class AppReadDbContext(DbContextOptions<AppReadDbContext> options)
    : Stratara.EventSourcing.EntityFrameworkCore.ReadStore.ReadDbContext<AppReadDbContext>(options);

public class AppIdentityDbContext(DbContextOptions<AppIdentityDbContext> options)
    : DbContext(options), Stratara.EventSourcing.EntityFrameworkCore.Abstractions.IIdentityDbContext
{
    Task<int> Stratara.EventSourcing.EntityFrameworkCore.Abstractions.IDbContext.SaveChangesAsync(
        CancellationToken token) => SaveChangesAsync(token);
}

public sealed record AccountSnapshot(Guid AccountId, decimal Balance);

public sealed record PlaceOrder(Guid OrderId) : ICommand;

public sealed record SimCardDto(Guid SimId, string Msisdn);

public sealed class OrderPlacedV1ToV2 : Stratara.Abstractions.EventSourcing.IEventUpcaster
{
    public string SourceEventTypeName => "OrderPlacedV1";

    public string TargetEventTypeName => "OrderPlacedV2";

    public System.Text.Json.Nodes.JsonNode Upcast(System.Text.Json.Nodes.JsonNode payload) => payload;
}

public sealed class OrderPlacedV2ToV3 : Stratara.Abstractions.EventSourcing.IEventUpcaster
{
    public string SourceEventTypeName => "OrderPlacedV2";

    public string TargetEventTypeName => "OrderPlacedV3";

    public System.Text.Json.Nodes.JsonNode Upcast(System.Text.Json.Nodes.JsonNode payload) => payload;
}

public sealed class LoggingBehavior<TRequest> : Stratara.Abstractions.Mediator.IPipelineBehavior<TRequest>
    where TRequest : Stratara.Abstractions.Mediator.IRequest
{
    public Task HandleAsync(TRequest request, Func<Task> next, CancellationToken cancellationToken) => next();
}

public sealed class AccountBalanceProjection : Stratara.Projections.Abstractions.IProjection;

public sealed class TransferSaga : Stratara.Sagas.Abstractions.ISaga;

public sealed class OpenAccountValidator : Stratara.Abstractions.Validation.IValidator<OpenAccountCommand>
{
    public ValueTask<Stratara.Abstractions.Validation.ValidationResult> ValidateAsync(
        OpenAccountCommand instance,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Stratara.Abstractions.Validation.ValidationResult.Success);
}

public sealed class LoggingBehavior<TRequest, TResult> : Stratara.Abstractions.Mediator.IPipelineBehavior<TRequest, TResult>
    where TRequest : Stratara.Abstractions.Mediator.IRequest<TResult>
{
    public Task<TResult> HandleAsync(TRequest request, Func<Task<TResult>> next, CancellationToken cancellationToken) => next();
}

public sealed class MyAuthorizationProvider : Stratara.Abstractions.Authorization.IAuthorizationProvider
{
    public Task<bool> IsInRoleAsync(string role, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
