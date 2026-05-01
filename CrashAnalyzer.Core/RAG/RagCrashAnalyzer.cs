using System.Text;
using CrashAnalyzer.Core.Llm;
using CrashAnalyzer.Core.Models;
using CrashAnalyzer.Core.Parsing;

namespace CrashAnalyzer.Core.RAG;

public sealed class RagCrashAnalyzer(EmbeddingClient embeddingClient, LlmClient llmClient, VectorStore vectorStore)
{
    private readonly EmbeddingClient _embeddingClient = embeddingClient;
    private readonly LlmClient _llmClient = llmClient;
    private readonly VectorStore _vectorStore = vectorStore;

    public async Task IndexFileAsync(
        string id,
        string filePath,
        int chunkSize = 1200,
        int chunkOverlap = 200,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Input file was not found.", filePath);
        }

        string text;
        if (string.Equals(Path.GetExtension(filePath), ".dmp", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = new DmpParser().Parse(filePath);
            text = BuildParsedCrashText(parsed);
        }
        else
        {
            text = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        }

        await IndexTextChunksAsync(id, text, chunkSize, chunkOverlap, cancellationToken).ConfigureAwait(false);
    }

    public async Task IndexCrashLogAsync(string id, string crashLogText, CancellationToken cancellationToken = default)
    {
        await IndexTextChunksAsync(id, crashLogText, chunkSize: 1200, chunkOverlap: 200, cancellationToken).ConfigureAwait(false);
    }

    public async Task IndexParsedCrashAsync(string id, ParsedCrash parsedCrash, CancellationToken cancellationToken = default)
    {
        var content = BuildParsedCrashText(parsedCrash);
        await IndexTextChunksAsync(id, content, chunkSize: 1200, chunkOverlap: 200, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> AskAsync(string question, int topK = 3, CancellationToken cancellationToken = default)
    {
        var questionEmbedding = await _embeddingClient.EmbedAsync(question, cancellationToken).ConfigureAwait(false);
        var matches = _vectorStore.Search(questionEmbedding, topK);

        var context = string.Join("\n\n---\n\n", matches.Select(m => m.Content));
        var prompt = $"""
You are a crash analysis assistant.
Use only the provided context to answer.
If the answer is not in the context, say so.

Context:
{context}

Question:
{question}
""";

        return await _llmClient.GenerateAsync(prompt, cancellationToken).ConfigureAwait(false);
    }

    private async Task IndexTextChunksAsync(string id, string text, int chunkSize, int chunkOverlap, CancellationToken cancellationToken)
    {
        var index = 0;
        foreach (var chunk in ChunkText(text, chunkSize, chunkOverlap))
        {
            var chunkId = $"{id}:chunk:{index}";
            var embedding = await _embeddingClient.EmbedAsync(chunk, cancellationToken).ConfigureAwait(false);
            _vectorStore.Add(chunkId, chunk, embedding);
            index++;
        }
    }

    private static IEnumerable<string> ChunkText(string text, int chunkSize, int chunkOverlap)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        chunkSize = Math.Max(200, chunkSize);
        chunkOverlap = Math.Max(0, Math.Min(chunkOverlap, chunkSize / 2));
        var step = chunkSize - chunkOverlap;

        for (var start = 0; start < text.Length; start += step)
        {
            var length = Math.Min(chunkSize, text.Length - start);
            yield return text.Substring(start, length);

            if (start + length >= text.Length)
            {
                yield break;
            }
        }
    }

    private static string BuildParsedCrashText(ParsedCrash crash)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"CrashType: {crash.CrashType}");
        builder.AppendLine($"ExceptionType: {crash.ExceptionType}");
        builder.AppendLine($"ExceptionCode: {crash.ExceptionCode}");
        builder.AppendLine($"FaultAddress: {crash.FaultAddress}");
        builder.AppendLine($"FaultingThreadId: {crash.FaultingThreadId}");
        builder.AppendLine($"ComApartment: {crash.ComApartment}");
        builder.AppendLine($"HResult: {crash.HResult}");

        if (crash.ManagedStack.Count > 0)
        {
            builder.AppendLine("ManagedStack:");
            foreach (var frame in crash.ManagedStack)
            {
                builder.AppendLine(frame);
            }
        }

        if (crash.NativeStack.Count > 0)
        {
            builder.AppendLine("NativeStack:");
            foreach (var frame in crash.NativeStack)
            {
                builder.AppendLine(frame);
            }
        }

        if (crash.Registers.Count > 0)
        {
            builder.AppendLine("Registers:");
            foreach (var register in crash.Registers)
            {
                builder.AppendLine($"{register.Key}={register.Value}");
            }
        }

        if (crash.Modules.Count > 0)
        {
            builder.AppendLine("Modules:");
            foreach (var module in crash.Modules)
            {
                builder.AppendLine(module);
            }
        }

        if (!string.IsNullOrWhiteSpace(crash.RawText))
        {
            builder.AppendLine("RawText:");
            builder.AppendLine(crash.RawText);
        }

        return builder.ToString();
    }
}
