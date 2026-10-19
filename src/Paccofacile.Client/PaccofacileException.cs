using System.Net;
using System.Text.Json;

namespace Paccofacile.Client;

/// <summary>Raised when the API answers with a non-success HTTP status.</summary>
public sealed class PaccofacileException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string Endpoint { get; }
    public string RawBody { get; }
    public JsonElement? Body { get; }

    public PaccofacileException(HttpStatusCode status, string endpoint, string rawBody, JsonElement? body)
        : base(BuildMessage(status, endpoint, body, rawBody))
    {
        StatusCode = status;
        Endpoint = endpoint;
        RawBody = rawBody;
        Body = body;
    }

    private static string BuildMessage(HttpStatusCode status, string endpoint, JsonElement? body, string raw)
    {
        var details = body.HasValue ? JsonHelpers.ExtractErrorMessages(body.Value) : null;
        if (string.IsNullOrWhiteSpace(details)) details = raw.Length > 600 ? raw[..600] + "…" : raw;
        return $"Paccofacile API {(int)status} {status} on '{endpoint}': {details}";
    }
}
