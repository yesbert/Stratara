using System.Globalization;
using System.Security.Cryptography;
using Stratara.Sample.Encryption.Crypto;
using Stratara.Sample.Encryption.Domain;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");

Console.WriteLine("=== Stratara Encryption ===");
Console.WriteLine();
Console.WriteLine("Stratara seals [EncryptData] fields with AES-GCM and binds the authentication");
Console.WriteLine("tag to the tenant id via Associated Data (AAD). A ciphertext only opens when the");
Console.WriteLine("decryption is attempted under the same tenant identity that sealed it — by the");
Console.WriteLine("cryptography, not by query filtering.");
Console.WriteLine();

var masterKey = RandomNumberGenerator.GetBytes(32);
var encryptor = new TenantAwareEncryptor(masterKey);

var tenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
var tenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

var alice = new Customer(tenantA, "Alice (Tenant A)", "123-45-6789");
var bob = new Customer(tenantB, "Bob (Tenant B)", "123-45-6789");

Console.WriteLine("--- Seal the same SSN under two different tenants ---");
var aliceSealed = encryptor.Encrypt(alice.SocialSecurityNumber, alice.TenantId);
var bobSealed = encryptor.Encrypt(bob.SocialSecurityNumber, bob.TenantId);
Console.WriteLine($"  Plaintext (both):  '{alice.SocialSecurityNumber}'");
Console.WriteLine($"  Alice ciphertext:  {Convert.ToHexString(aliceSealed.Ciphertext)} (tag {Convert.ToHexString(aliceSealed.Tag)[..8]}…)");
Console.WriteLine($"  Bob   ciphertext:  {Convert.ToHexString(bobSealed.Ciphertext)} (tag {Convert.ToHexString(bobSealed.Tag)[..8]}…)");
Console.WriteLine("  Same plaintext, same master key, but different ciphertexts — fresh nonces every seal.");
Console.WriteLine();

Console.WriteLine("--- Read each customer back under its own tenant context ---");
Console.WriteLine($"  Alice: '{encryptor.Decrypt(aliceSealed, tenantA)}'  (tenant A reads tenant A — OK)");
Console.WriteLine($"  Bob:   '{encryptor.Decrypt(bobSealed, tenantB)}'  (tenant B reads tenant B — OK)");
Console.WriteLine();

Console.WriteLine("--- Cross-tenant attack: take Alice's row from the DB, try to decrypt as tenant B ---");
try
{
    var stolen = encryptor.Decrypt(aliceSealed, tenantB);
    Console.WriteLine($"  Recovered: '{stolen}' — this should not happen.");
}
catch (CryptographicException ex)
{
    Console.WriteLine($"  CAUGHT: {ex.GetType().Name} — {ex.Message}");
    Console.WriteLine("  AES-GCM rejected the authentication tag because the AAD (tenant id) does not match.");
    Console.WriteLine("  This holds even with the correct master key. The tenant binding is mathematical.");
}
Console.WriteLine();

Console.WriteLine("--- Same attack, the other direction ---");
try
{
    encryptor.Decrypt(bobSealed, tenantA);
}
catch (CryptographicException)
{
    Console.WriteLine("  CAUGHT (tenant A trying to read Bob's row).");
}
Console.WriteLine();

Console.WriteLine("In production this is wired automatically: Stratara's EF Core value converter calls");
Console.WriteLine("the encryptor on every read/write of an [EncryptData]-marked property, with the AAD");
Console.WriteLine("supplied by ISessionContextProvider.Current.TenantId. A row filtered out by query but");
Console.WriteLine("leaked through a DB-level mistake still cannot be opened from another tenant's session.");
Console.WriteLine();
Console.WriteLine("Done.");
