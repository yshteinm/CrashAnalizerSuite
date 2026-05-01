using System.Net.Http.Json;

namespace CrashAnalyzer.Core.Llm;

public sealed class EmbeddingClient(HttpClient httpClient, string model)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly string _model = model;

    public async Task<float[]> EmbedAsync(string input, CancellationToken cancellationToken = default)
    {
        var request = new EmbeddingRequest(_model, input);
        using var response = await _httpClient.PostAsJsonAsync("/api/embeddings", request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Embedding response was empty.");

        return payload.Embedding?.ToArray() ?? [];
    }

    private sealed record EmbeddingRequest(string Model, string Prompt);

    private sealed record EmbeddingResponse
    {
        public List<float>? Embedding { get; init; }
    }
}
