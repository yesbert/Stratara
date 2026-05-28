using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Stratara.Infrastructure.Security.Cryptography;
using Stratara.Infrastructure.Security.Serialization;
using Stratara.Abstractions.Security;

namespace Stratara.Benchmarks.Security;

[MemoryDiagnoser]
[SimpleJob]
public class SecureJsonSerializerBenchmark
{
    private readonly Guid _tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly Guid _userId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private string _baselineClassJson = string.Empty;

    private string _baselineFieldJson = string.Empty;
    private string _baselineNoEncryptionJson = string.Empty;
    private PersonClassEncrypted _classEncrypted = null!;
    private string _encryptedClassJson = string.Empty;

    private string _encryptedFieldJson = string.Empty;
    private PersonFieldEncrypted _fieldEncrypted = null!;
    private PersonNoEncryption _noEncryption = null!;
    private string _secureNoEncryptionJson = string.Empty;

    private ISecureJsonSerializer _secureSerializer = null!;

    [GlobalSetup]
    public void Setup()
    {
        var keyStore = new InMemoryKeyStore();
        var encryptionFactory = new AesGcmEncryptionFactory();
        _secureSerializer = new SecureJsonSerializer(keyStore, encryptionFactory);

        _fieldEncrypted = new PersonFieldEncrypted
        {
            Id = 42,
            FirstName = "Ada",
            LastName = "Lovelace",
            Email = "ada@example.com",
            Age = 36
        };

        _classEncrypted = new PersonClassEncrypted
        {
            Id = 99,
            FirstName = "Alan",
            LastName = "Turing",
            Email = "alan@example.com",
            Age = 41
        };

        _noEncryption = new PersonNoEncryption
        {
            Id = 7,
            FirstName = "Grace",
            LastName = "Hopper",
            Email = "grace@example.com",
            Age = 85
        };

        // Baseline JSON using System.Text.Json
        _baselineFieldJson = JsonSerializer.Serialize(_fieldEncrypted);
        _baselineClassJson = JsonSerializer.Serialize(_classEncrypted);
        _baselineNoEncryptionJson = JsonSerializer.Serialize(_noEncryption);

        // Pre-encrypt to use in deserialize benchmarks
        _encryptedFieldJson = _secureSerializer.SerializeAsync(_fieldEncrypted, _tenantId, _userId).GetAwaiter().GetResult();
        _encryptedClassJson = _secureSerializer.SerializeAsync(_classEncrypted, _tenantId, _userId).GetAwaiter().GetResult();
        _secureNoEncryptionJson = _secureSerializer.SerializeAsync(_noEncryption, _tenantId, _userId).GetAwaiter().GetResult();
    }

    // ---------------- Baseline (no encryption) ----------------

    [Benchmark(Description = "Baseline Serialize (STJ) - field model")]
    public string Baseline_Field_Serialize()
        => JsonSerializer.Serialize(_fieldEncrypted);

    [Benchmark(Description = "Baseline Deserialize (STJ) - field model")]
    public PersonFieldEncrypted? Baseline_Field_Deserialize()
        => JsonSerializer.Deserialize<PersonFieldEncrypted>(_baselineFieldJson);

    [Benchmark(Description = "Baseline Serialize (STJ) - class model")]
    public string Baseline_Class_Serialize()
        => JsonSerializer.Serialize(_classEncrypted);

    [Benchmark(Description = "Baseline Deserialize (STJ) - class model")]
    public PersonClassEncrypted? Baseline_Class_Deserialize()
        => JsonSerializer.Deserialize<PersonClassEncrypted>(_baselineClassJson);

    // ---------------- Secure serializer ----------------

    [Benchmark(Description = "Secure Serialize - field-level encryption")]
    public async Task<string> Secure_Field_Serialize()
        => await _secureSerializer.SerializeAsync(_fieldEncrypted, _tenantId, _userId);

    [Benchmark(Description = "Secure Deserialize - field-level encryption")]
    public async Task<PersonFieldEncrypted?> Secure_Field_Deserialize()
        => await _secureSerializer.DeserializeAsync<PersonFieldEncrypted>(_encryptedFieldJson, _tenantId, _userId);

    [Benchmark(Description = "Secure Serialize - class-level encryption")]
    public async Task<string> Secure_Class_Serialize()
        => await _secureSerializer.SerializeAsync(_classEncrypted, _tenantId, _userId);

    [Benchmark(Description = "Secure Deserialize - class-level encryption")]
    public async Task<PersonClassEncrypted?> Secure_Class_Deserialize()
        => await _secureSerializer.DeserializeAsync<PersonClassEncrypted>(_encryptedClassJson, _tenantId, _userId);

    [Benchmark(Description = "Secure Serialize - no encryption")]
    public async Task<string> Secure_None_Serialize()
        => await _secureSerializer.SerializeAsync(_noEncryption, _tenantId, _userId);

    [Benchmark(Description = "Secure Deserialize - no encryption")]
    public async Task<PersonNoEncryption?> Secure_None_Deserialize()
        => await _secureSerializer.DeserializeAsync<PersonNoEncryption>(_secureNoEncryptionJson, _tenantId, _userId);

    // ---------------- Fixtures ----------------

    private sealed class InMemoryKeyStore : IKeyStore
    {
        private readonly Dictionary<string, byte[]> _keys = new();

        public ValueTask<string> EnsureKeyAsync(DataSensitivityLevel level, Guid? tenantId, Guid? userId, CancellationToken cancellationToken = default)
        {
            // For benchmarking, generate stable key per sensitivity level
            var keyId = $"kid-{level}";
            if (!_keys.ContainsKey(keyId))
            {
                var key = new byte[32]; // AES-256
                // Deterministic but unique per level: fill with level code repeated
                var fill = (byte)((int)level & 0xFF);
                for (var i = 0; i < key.Length; i++)
                {
                    key[i] = (byte)(fill + i);
                }

                _keys[keyId] = key;
            }

            return ValueTask.FromResult(keyId);
        }

        public ValueTask<byte[]?> GetDataEncryptionKeyAsync(string keyId, CancellationToken cancellationToken = default)
        {
            _keys.TryGetValue(keyId, out var key);
            // Return a copy to mimic keystore behavior and avoid accidental mutation
            return ValueTask.FromResult(key is null ? null : key.ToArray());
        }

        public ValueTask RevokeAsync(string keyId, CancellationToken cancellationToken = default)
        {
            _keys.Remove(keyId);
            return ValueTask.CompletedTask;
        }
    }

    public sealed class PersonFieldEncrypted
    {
        public int Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;

        [EncryptData] public string Email { get; set; } = string.Empty;

        public int Age { get; set; }
    }

    [EncryptData(DataSensitivityLevel.TenantScoped)]
    public sealed class PersonClassEncrypted
    {
        public int Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public int Age { get; set; }
    }

    public sealed class PersonNoEncryption
    {
        public int Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public int Age { get; set; }
    }
}