namespace Ticketing.Api.Tests.Infrastructure;

/// <summary>
/// Tags each test with the lab it belongs to, so a single lab can be run on its own:
///   dotnet test --filter "Lab=1"
/// </summary>
public static class Lab
{
    public const string Trait = "Lab";
}
