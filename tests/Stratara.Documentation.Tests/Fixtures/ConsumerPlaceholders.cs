using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
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
