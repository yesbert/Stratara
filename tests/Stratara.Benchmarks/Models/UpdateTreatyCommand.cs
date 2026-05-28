namespace Stratara.Benchmarks.Models;

public record UpdateTreatyCommand
{
    public Guid TreatyId { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public string Currency { get; init; } = string.Empty;
    public decimal PremiumEstimate { get; init; }
    public string CedentName { get; init; } = string.Empty;
    public long SourceVersion { get; init; }
}