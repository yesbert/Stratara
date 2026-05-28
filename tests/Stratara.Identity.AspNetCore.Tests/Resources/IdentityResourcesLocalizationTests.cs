using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Stratara.Identity.AspNetCore.Resources;

namespace Stratara.Identity.AspNetCore.Tests.Resources;

public class IdentityResourcesLocalizationTests
{
    private const string LockoutKey = "Identity.SignIn.Lockout";
    private const string InvalidCredentialsKey = "Identity.SignIn.InvalidCredentials";

    [Fact]
    public void Localizer_WithEnglishCulture_ReturnsEnglishResourceText()
    {
        var localizer = BuildLocalizer();

        using (UseCulture("en"))
        {
            var text = localizer[LockoutKey].Value;
            Assert.Contains("locked", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Localizer_WithGermanCulture_ReturnsGermanResourceText()
    {
        var localizer = BuildLocalizer();

        using (UseCulture("de"))
        {
            var text = localizer[LockoutKey].Value;
            Assert.Contains("gesperrt", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Localizer_FallsBackToEnglishForUnsupportedCulture()
    {
        var localizer = BuildLocalizer();

        using (UseCulture("fr"))
        {
            var text = localizer[InvalidCredentialsKey].Value;
            Assert.Contains("invalid", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Localizer_GermanOverridesAreDistinctFromEnglish()
    {
        var localizer = BuildLocalizer();

        string english;
        string german;
        using (UseCulture("en"))
        {
            english = localizer[InvalidCredentialsKey].Value;
        }
        using (UseCulture("de"))
        {
            german = localizer[InvalidCredentialsKey].Value;
        }

        Assert.NotEqual(english, german);
    }

    private static IStringLocalizer<IdentityResources> BuildLocalizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization();
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IStringLocalizer<IdentityResources>>();
    }

    private static CultureScope UseCulture(string name) => new(name);

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previousUiCulture;
        private readonly CultureInfo _previousCulture;

        public CultureScope(string name)
        {
            _previousUiCulture = CultureInfo.CurrentUICulture;
            _previousCulture = CultureInfo.CurrentCulture;
            var culture = new CultureInfo(name);
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentUICulture = _previousUiCulture;
            CultureInfo.CurrentCulture = _previousCulture;
        }
    }
}
