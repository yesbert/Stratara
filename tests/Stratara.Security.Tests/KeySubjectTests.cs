using Stratara.Abstractions.Security;
using Xunit;

namespace Stratara.Security.Tests;

public class KeySubjectTests
{
    private const string Tenant = "0b1e2a3c-0000-7000-8000-000000000001";
    private const string User = "0b1e2a3c-0000-7000-8000-000000000002";

    [Theory]
    [InlineData(DataSensitivityLevel.None)]
    [InlineData(DataSensitivityLevel.UserScoped)]
    [InlineData(DataSensitivityLevel.TenantScoped)]
    [InlineData(DataSensitivityLevel.Confidential)]
    public void A_tenant_covers_every_level_that_names_it(DataSensitivityLevel level)
    {
        var tenant = KeySubject.Tenant(Tenant);

        Assert.True(tenant.Covers(new KeyScope(level, Tenant)));
        Assert.True(tenant.Covers(new KeyScope(level, Tenant.ToUpperInvariant(), User)));
        Assert.False(tenant.Covers(new KeyScope(level, "another", User)));
        Assert.False(tenant.Covers(new KeyScope(level, null, Tenant)));
    }

    [Theory]
    [InlineData(DataSensitivityLevel.None, false)]
    [InlineData(DataSensitivityLevel.UserScoped, true)]
    [InlineData(DataSensitivityLevel.TenantScoped, false)]
    [InlineData(DataSensitivityLevel.Confidential, false)]
    public void A_user_covers_only_the_user_level_that_names_it_within_a_tenant(DataSensitivityLevel level, bool covered)
    {
        var user = KeySubject.User(User);

        Assert.Equal(covered, user.Covers(new KeyScope(level, Tenant, User)));
        Assert.Equal(covered, user.Covers(new KeyScope(level, Tenant, User.ToUpperInvariant())));
        Assert.False(user.Covers(new KeyScope(level, User, "another")));
    }

    [Theory]
    [InlineData(DataSensitivityLevel.UserScoped)]
    [InlineData(DataSensitivityLevel.TenantScoped)]
    [InlineData(DataSensitivityLevel.Confidential)]
    public void A_user_covers_every_level_that_names_them_and_no_tenant_of_theirs(DataSensitivityLevel level)
    {
        var user = KeySubject.User(User);

        Assert.True(user.Covers(new KeyScope(level, null, User)));
        Assert.True(user.Covers(new KeyScope(level, Guid.Empty.ToString("D"), User.ToUpperInvariant())));
        Assert.False(user.Covers(new KeyScope(level, Guid.Empty.ToString("D"), "another")));
        Assert.False(user.Covers(new KeyScope(level, Guid.Empty.ToString("D"))));
    }

    [Fact]
    public void An_empty_id_names_no_subject()
    {
        Assert.Throws<ArgumentException>(() => KeySubject.Tenant(" "));
        Assert.Throws<ArgumentException>(() => KeySubject.User(""));
    }

    [Fact]
    public void Subjects_are_equal_when_their_ids_differ_only_in_case()
    {
        var lower = KeySubject.User(User);
        var upper = KeySubject.User(User.ToUpperInvariant());

        Assert.Equal(lower, upper);
        Assert.Equal(lower.GetHashCode(), upper.GetHashCode());
        Assert.Single(new HashSet<KeySubject> { lower, upper });
        Assert.NotEqual(KeySubject.Tenant(User), lower);
    }

    [Fact]
    public void The_default_value_covers_nothing_and_is_refused_by_the_stores()
    {
        var none = default(KeySubject);

        Assert.Equal(string.Empty, none.Id);
        Assert.False(none.Covers(new KeyScope(DataSensitivityLevel.Confidential)));
        Assert.False(none.Covers(new KeyScope(DataSensitivityLevel.UserScoped)));
        Assert.Throws<ArgumentException>(() => none.ThrowIfDefault("subject"));
    }

    [Fact]
    public async Task The_file_store_refuses_to_mark_the_default_value()
    {
        var store = TestSupport.NewKeyStore(TestSupport.NewOptions(TestSupport.NewKekBase64()));

        await Assert.ThrowsAsync<ArgumentException>(async () => await store.MarkSubjectErasedAsync(default));

        Assert.NotNull(await store.GetOrCreateCurrentKeyAsync(new KeyScope(DataSensitivityLevel.Confidential)));
    }

    [Fact]
    public void The_refusal_names_the_scope_and_is_an_invalid_operation()
    {
        var scope = new KeyScope(DataSensitivityLevel.TenantScoped, Tenant, User);

        InvalidOperationException refusal = new KeyScopeErasedException(scope);

        Assert.Equal(scope, ((KeyScopeErasedException)refusal).Scope);
        Assert.Contains(Tenant, refusal.Message, StringComparison.Ordinal);
    }
}
