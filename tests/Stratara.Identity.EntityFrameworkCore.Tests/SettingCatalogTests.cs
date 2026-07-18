using Stratara.Abstractions.Settings;
using Xunit;

namespace Stratara.Identity.EntityFrameworkCore.Tests;

public class SettingCatalogTests
{
    [Fact]
    public void Declared_settings_are_listed_and_queryable()
    {
        var catalog = new SettingCatalog();
        catalog.Add(new SettingDefinition("Ui.Theme", "system"), new SettingDefinition("Smtp.Password", IsEncrypted: true));

        Assert.Equal(2, catalog.All.Count);
        Assert.True(catalog.Contains("Ui.Theme"));
        Assert.Equal("system", catalog.GetOrNull("Ui.Theme")?.DefaultValue);
        Assert.Null(catalog.GetOrNull("Unknown"));
    }

    [Fact]
    public void Redeclaring_a_name_throws()
    {
        var catalog = new SettingCatalog();
        catalog.Add(new SettingDefinition("Ui.Theme"));

        var ex = Assert.Throws<ArgumentException>(() => catalog.Add(new SettingDefinition("Ui.Theme", "dark")));
        Assert.Contains("Ui.Theme", ex.Message);
    }

    [Fact]
    public void Empty_names_are_rejected()
    {
        var catalog = new SettingCatalog();
        Assert.Throws<ArgumentException>(() => catalog.Add(new SettingDefinition(" ")));
    }
}
