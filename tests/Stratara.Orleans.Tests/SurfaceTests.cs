using System.Reflection;

namespace Stratara.Orleans.Tests;

public class SurfaceTests
{
    private const string RemindableInterface = "Orleans.IRemindable";

    [Fact]
    public void NoPublicType_ImplementsIRemindable()
    {
        var assembly = Assembly.Load("Stratara.Orleans");

        var offenders = assembly
            .GetExportedTypes()
            .Where(type => type.GetInterfaces().Any(contract => contract.FullName == RemindableInterface))
            .Select(type => type.FullName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Timers are reached through Stratara's own port; these public types expose {RemindableInterface}: " +
            string.Join(", ", offenders));
    }
}
