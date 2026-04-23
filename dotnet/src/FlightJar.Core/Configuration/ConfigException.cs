namespace FlightJar.Core.Configuration;

public sealed class ConfigException : Exception
{
    public ConfigException(string message)
        : base(message) { }
}
