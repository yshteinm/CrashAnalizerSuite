using System.Net.Http.Json;

namespace CrashAnalyzer.Core.Llm;

public sealed class LlmClient(HttpClient httpClient, string model)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly string _model = model;

    public async Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var request = new GenerateRequest(_model, prompt, Stream: false);
        using var response = await _httpClient.PostAsJsonAsync("/api/generate", request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<GenerateResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Generate response was empty.");

        return payload.Response ?? string.Empty;
    }

    private sealed record GenerateRequest(string Model, string Prompt, bool Stream);

    private sealed record GenerateResponse
    {
        public string? Response { get; init; }
    }
}
