namespace Stratara.Sample.OutboxWorker.Domain;

public sealed class Account(Guid id, string ownerName, decimal initialBalance, DateTimeOffset openedAt)
{
    public Guid Id { get; } = id;
    public string OwnerName { get; } = ownerName;
    public decimal Balance { get; private set; } = initialBalance;
    public DateTimeOffset OpenedAt { get; } = openedAt;

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
