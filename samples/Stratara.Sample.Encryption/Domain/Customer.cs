using Stratara.Sample.Encryption.Crypto;

namespace Stratara.Sample.Encryption.Domain;

public sealed class Customer
{
    public Customer(Guid tenantId, string name, string socialSecurityNumber)
    {
        TenantId = tenantId;
        Name = name;
        SocialSecurityNumber = socialSecurityNumber;
    }

    public Guid TenantId { get; }

    public string Name { get; }

    [Encrypted]
    public string SocialSecurityNumber { get; }
}
