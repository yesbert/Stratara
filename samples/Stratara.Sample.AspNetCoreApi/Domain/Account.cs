namespace Stratara.Sample.AspNetCoreApi.Domain;

public sealed class Account
{
    public Guid Id { get; }
    public string OwnerName { get; }
    public decimal Balance { get; private set; }
    public DateTimeOffset OpenedAt { get; }

    public Account(Guid id, string ownerName, decimal initialBalance, DateTimeOffset openedAt)
    {
        Id = id;
        OwnerName = ownerName;
        Balance = initialBalance;
        OpenedAt = openedAt;
    }

    public void Deposit(decimal amount) => Balance += amount;

    public void Withdraw(decimal amount)
    {
        if (amount > Balance)
        {
            throw new InsufficientBalanceException(Id, Balance, amount);
        }
        Balance -= amount;
    }
}
