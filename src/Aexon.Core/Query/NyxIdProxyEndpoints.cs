namespace Aexon.Core.Query;

/// <summary>
/// Resolves NyxID proxy endpoints for supported chat providers.
/// </summary>
public static class NyxIdProxyEndpoints
{
    public static Uri Resolve(string nyxIdBaseUrl, AiProvider provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nyxIdBaseUrl);
        if (!Uri.TryCreate(nyxIdBaseUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var baseUri))
            throw new ArgumentException("NyxID base URL must be an absolute URL.", nameof(nyxIdBaseUrl));

        var relativePath = provider switch
        {
            AiProvider.OpenAI => "/api/v1/proxy/s/llm-openai/",
            AiProvider.Anthropic => "/api/v1/proxy/s/llm-anthropic/",
            _ => throw new NotSupportedException($"NyxID routing does not support provider '{provider}'."),
        };

        return new Uri(baseUri, relativePath);
    }
}
