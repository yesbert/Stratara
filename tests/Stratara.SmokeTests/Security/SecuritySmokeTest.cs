using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Stratara.Infrastructure.Security.Cryptography;
using Stratara.Infrastructure.Security.KeyManagement;
using Stratara.Infrastructure.Security.Serialization;
using Stratara.Abstractions.Security;

namespace Stratara.SmokeTests.Security;

// Dummy KeyStore for testing (returns a fixed key until revoked)

// Example record with property-level encryption
public record Person(
    [EncryptData] string Name,
    int Age
);

// Example record with class-level encryption
[EncryptData(DataSensitivityLevel.TenantScoped)]
public record SecretNote(string Title, string Content);

public static class SecuritySmokeTest
{
    public static async Task RunAsync()
    {
        var devEnvironment = new HostingEnvironment { EnvironmentName = Environments.Development };
        var keyStore = new DummyKeyStore(devEnvironment);
        var encryptionFactory = new AesGcmEncryptionFactory();
        var serializer = new SecureJsonSerializer(keyStore, encryptionFactory);

        // Always use the same tenant and user IDs for encryption and decryption
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // ---- Test 1: Property-level ----
        var p = new Person("Alice", 30);
        var jsonEncrypted = await serializer.SerializeAsync(p, tenantId, userId);
        Console.WriteLine("Encrypted Person JSON:");
        Console.WriteLine(jsonEncrypted);

        var p2 = await serializer.DeserializeAsync<Person>(jsonEncrypted, tenantId, userId);
        Console.WriteLine($"Decrypted Person: Name={p2?.Name}, Age={p2?.Age}");

        // ---- Test 2: Class-level ----
        var note = new SecretNote("Private", "This is top secret");
        var noteEncrypted = await serializer.SerializeAsync(note, tenantId, userId);
        Console.WriteLine("Encrypted Note JSON:");
        Console.WriteLine(noteEncrypted);

        var note2 = await serializer.DeserializeAsync<SecretNote>(noteEncrypted, tenantId, userId);
        Console.WriteLine($"Decrypted Note: Title={note2?.Title}, Content={note2?.Content}");

        // ---- Equality Checks ----
        Console.WriteLine($"Person equal? {JsonSerializer.Serialize(p) == JsonSerializer.Serialize(p2)}");
        Console.WriteLine($"Note equal? {JsonSerializer.Serialize(note) == JsonSerializer.Serialize(note2)}");

        // ---- Test 3: Key revoked ----
        await keyStore.RevokeAsync("dummy-key-id");

        var revokedNote = await serializer.DeserializeAsync<SecretNote>(noteEncrypted, tenantId, userId);
        Console.WriteLine($"After revocation: {revokedNote?.Title ?? "null"} / {revokedNote?.Content ?? "null"}");
    }
}