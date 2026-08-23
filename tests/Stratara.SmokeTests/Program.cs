using Stratara.SmokeTests.Architecture;
using Stratara.SmokeTests.Security;

TierLayeringCheck.Run();

await SecuritySmokeTest.RunAsync();
