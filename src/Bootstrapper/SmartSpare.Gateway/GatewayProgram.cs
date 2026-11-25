namespace SmartSpare.Gateway;

/// <summary>Names of the gateway's HTTP clients.</summary>
public static class GatewayClients
{
    public const string Identity = "identity";
}

/// <summary>Entry point; public so tests can use <c>WebApplicationFactory&lt;GatewayProgram&gt;</c> via this marker.</summary>
public sealed class GatewayProgram
{
    private GatewayProgram()
    {
    }
}
