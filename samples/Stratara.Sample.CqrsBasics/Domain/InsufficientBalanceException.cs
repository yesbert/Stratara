namespace Stratara.Sample.CqrsBasics.Domain;

public sealed class InsufficientBalanceException(Guid accountId, decimal balance, decimal attemptedAmount)
    : InvalidOperationException(
        $"Account {accountId} has balance {balance:C}; cannot withdraw {attemptedAmount:C}.")
{
    public Guid AccountId { get; } = accountId;
    public decimal Balance { get; } = balance;
    public decimal AttemptedAmount { get; } = attemptedAmount;
}
