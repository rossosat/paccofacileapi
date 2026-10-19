namespace Paccofacile.Client;

/// <summary>Credentials and environment for the Paccofacile.it PRO API.</summary>
public sealed class PaccofacileOptions
{
    public const string LiveBaseUrl = "https://paccofacile.tecnosogima.cloud/live/v1/service/";
    public const string SandboxBaseUrl = "https://paccofacile.tecnosogima.cloud/sandbox/v1/service/";

    /// <summary>"live" or "sandbox".</summary>
    public string Mode { get; set; } = "live";

    /// <summary>Bearer token (e.g. "3589|xxxx").</summary>
    public string Token { get; set; } = "";

    /// <summary>Api key (the "$2y$10$..." string).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Customer account number.</summary>
    public string AccountNumber { get; set; } = "";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    public string BaseUrl => string.Equals(Mode, "sandbox", StringComparison.OrdinalIgnoreCase) ? SandboxBaseUrl : LiveBaseUrl;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Token)) throw new ArgumentException("Paccofacile token is missing.");
        if (string.IsNullOrWhiteSpace(ApiKey)) throw new ArgumentException("Paccofacile api key is missing.");
        if (string.IsNullOrWhiteSpace(AccountNumber)) throw new ArgumentException("Paccofacile account number is missing.");
    }
}
