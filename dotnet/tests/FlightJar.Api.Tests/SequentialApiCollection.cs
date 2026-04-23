namespace FlightJar.Api.Tests;

/// <summary>
/// Both API test classes mutate process env vars (<c>BEAST_HOST</c> / <c>BEAST_PORT</c>)
/// to redirect the hosted consumer. Running them in a shared xUnit collection
/// serialises them so the env-var writes don't race.
/// </summary>
[CollectionDefinition("SequentialApi", DisableParallelization = true)]
public sealed class SequentialApiCollection;
