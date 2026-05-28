namespace Stratara.Benchmarks.Models;

public record Treaty
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset EffectiveFrom { get; set; }
    public long TreatyNumber { get; set; }
    public string TreatyCode { get; set; } = string.Empty;
    public string TreatyType { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string Currency { get; set; } = "EUR";
    public decimal PremiumEstimate { get; set; }
    public string PaymentFrequency { get; set; } = "Annual";
    public string CedentName { get; set; } = string.Empty;
    public string BrokerName { get; set; } = string.Empty;
    public List<string> Layers { get; set; } = new();
    public List<string> CoveredRisks { get; set; } = new();
    public string Status { get; set; } = "Draft";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastModifiedAt { get; set; }
}