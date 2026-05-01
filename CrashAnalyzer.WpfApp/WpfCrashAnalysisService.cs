using System.IO;
using System.Net.Http;
using System.Text.Json;
using CrashAnalyzer.Core.Llm;
using CrashAnalyzer.Core.RAG;
using CrashAnalyzer.Core.Services;

namespace CrashAnalyzer.WpfApp;

public sealed class WpfCrashAnalysisService
{
    private const string SettingsFileName = "crashanalyzer.settings.json";

    private readonly CrashAnalysisService _coreService = new();
    private RagCrashAnalyzer? _ragAnalyzer;
    private string? _loadedDumpPath;
    private string? _loadedPdbPath;
    private string? _lastAnalysis;

    public string AnalyzeDump(string dumpPath)
    {
        var settings = LoadSettings();
        var analysis = _coreService.AnalyzeDump(dumpPath, _loadedPdbPath, settings.SymbolizerPath);
        _lastAnalysis = analysis;
        _loadedDumpPath = dumpPath;

        _ragAnalyzer = CreateRagAnalyzer();
        if (_ragAnalyzer is null)
        {
            return analysis + Environment.NewLine + Environment.NewLine +
                $"Prompt is unavailable. Check {SettingsFileName}.";
        }

        try
        {
            _ragAnalyzer.IndexCrashLogAsync(dumpPath, analysis).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            analysis += Environment.NewLine + Environment.NewLine + $"Failed to index dump for prompt Q&A: {ex.Message}";
        }

        return analysis;
    }

    public string SetPdbPath(string pdbPath)
    {
        if (string.IsNullOrWhiteSpace(pdbPath))
        {
            _loadedPdbPath = null;
            return "PDB cleared.";
        }

        if (!File.Exists(pdbPath))
        {
            return $"PDB file not found: {pdbPath}";
        }

        _loadedPdbPath = pdbPath;
        return $"PDB loaded: {pdbPath}";
    }

    public string? ReanalyzeLoadedDump()
    {
        if (string.IsNullOrWhiteSpace(_loadedDumpPath))
        {
            return null;
        }

        var settings = LoadSettings();
        var refreshed = _coreService.AnalyzeDump(_loadedDumpPath, _loadedPdbPath, settings.SymbolizerPath);
        _lastAnalysis = refreshed;

        if (_ragAnalyzer is not null)
        {
            try
            {
                _ragAnalyzer.IndexCrashLogAsync(_loadedDumpPath, refreshed).GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        return refreshed;
    }

    public async Task<string> AskAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return "Enter a question first.";
        }

        if (string.IsNullOrWhiteSpace(_loadedDumpPath))
        {
            return "Open and analyze a dump file first.";
        }

        if (!string.IsNullOrWhiteSpace(_lastAnalysis))
        {
            var quick = TryAnswerDirectly(question, _lastAnalysis);
            if (!string.IsNullOrWhiteSpace(quick))
            {
                return quick;
            }
        }

        if (_ragAnalyzer is null)
        {
            return $"Prompt is unavailable. Check {SettingsFileName}.";
        }

        var llmServiceError = await CheckLlmServiceAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(llmServiceError))
        {
            return llmServiceError;
        }

        try
        {
            var fullQuestion = string.IsNullOrWhiteSpace(_loadedPdbPath)
                ? question
                : $"PDB path available for source mapping context: {_loadedPdbPath}{Environment.NewLine}Question: {question}";

            return await _ragAnalyzer.AskAsync(fullQuestion, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"Prompt query failed: {ex.Message}";
        }
    }

    private static RagCrashAnalyzer? CreateRagAnalyzer()
    {
        var settings = LoadSettings();

        if (!Uri.TryCreate(settings.LlmBaseUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(settings.LlmModel) || string.IsNullOrWhiteSpace(settings.EmbeddingModel))
        {
            return null;
        }

        var httpClient = new HttpClient
        {
            BaseAddress = uri,
            Timeout = TimeSpan.FromSeconds(Math.Max(30, settings.LlmTimeoutSeconds))
        };

        return new RagCrashAnalyzer(
            new EmbeddingClient(httpClient, settings.EmbeddingModel),
            new LlmClient(httpClient, settings.LlmModel),
            new VectorStore());
    }

    private static async Task<string?> CheckLlmServiceAsync(CancellationToken cancellationToken)
    {
        var settings = LoadSettings();
        if (!Uri.TryCreate(settings.LlmBaseUrl, UriKind.Absolute, out var uri))
        {
            return $"Invalid LLM service URL in {SettingsFileName}: '{settings.LlmBaseUrl}'.";
        }

        using var httpClient = new HttpClient
        {
            BaseAddress = uri,
            Timeout = TimeSpan.FromSeconds(5)
        };

        try
        {
            using var response = await httpClient.GetAsync("/api/tags", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return $"LLM service is not ready at {settings.LlmBaseUrl} (HTTP {(int)response.StatusCode} {response.ReasonPhrase}).";
            }

            return null;
        }
        catch (TaskCanceledException)
        {
            return $"LLM service check timed out at {settings.LlmBaseUrl}.";
        }
        catch (HttpRequestException ex)
        {
            return $"Cannot reach LLM service at {settings.LlmBaseUrl}: {ex.Message}";
        }
    }

    private static PromptSettings LoadSettings()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, SettingsFileName);
        if (!File.Exists(settingsPath))
        {
            return PromptSettings.Default;
        }

        try
        {
            var json = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize<PromptSettings>(json);
            if (settings is null)
            {
                return PromptSettings.Default;
            }

            return new PromptSettings
            {
                LlmBaseUrl = string.IsNullOrWhiteSpace(settings.LlmBaseUrl) ? PromptSettings.Default.LlmBaseUrl : settings.LlmBaseUrl,
                LlmModel = string.IsNullOrWhiteSpace(settings.LlmModel) ? PromptSettings.Default.LlmModel : settings.LlmModel,
                EmbeddingModel = string.IsNullOrWhiteSpace(settings.EmbeddingModel) ? PromptSettings.Default.EmbeddingModel : settings.EmbeddingModel,
                LlmTimeoutSeconds = settings.LlmTimeoutSeconds <= 0 ? PromptSettings.Default.LlmTimeoutSeconds : settings.LlmTimeoutSeconds,
                SymbolizerPath = string.IsNullOrWhiteSpace(settings.SymbolizerPath) ? PromptSettings.Default.SymbolizerPath : settings.SymbolizerPath
            };
        }
        catch
        {
            return PromptSettings.Default;
        }
    }

    private sealed class PromptSettings
    {
        public string LlmBaseUrl { get; init; } = "http://localhost:11434";
        public string LlmModel { get; init; } = "llama3.1";
        public string EmbeddingModel { get; init; } = "nomic-embed-text";
        public int LlmTimeoutSeconds { get; init; } = 300;
        public string SymbolizerPath { get; init; } = "C:\\Program Files\\Microsoft Visual Studio\\18\\Community\\VC\\Tools\\MSVC\\14.50.35717\\bin\\Hostx86\\x86\\llvm-symbolizer.exe";

        public static PromptSettings Default { get; } = new()
        {
            LlmBaseUrl = "http://localhost:11434",
            LlmModel = "llama3.1",
            EmbeddingModel = "nomic-embed-text",
            LlmTimeoutSeconds = 300,
            SymbolizerPath = "C:\\Program Files\\Microsoft Visual Studio\\18\\Community\\VC\\Tools\\MSVC\\14.50.35717\\bin\\Hostx86\\x86\\llvm-symbolizer.exe"
        };
    }

    private static string? TryAnswerDirectly(string question, string analysis)
    {
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(analysis))
        {
            return null;
        }

        var q = question.ToLowerInvariant();
        if (!q.Contains("where") && !q.Contains("line") && !q.Contains("source") && !q.Contains("why"))
        {
            return null;
        }

        string? sourceFunction = null;
        string? sourceLocation = null;
        string? exceptionCode = null;
        string? faultAddress = null;

        foreach (var line in analysis.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("SourceFunction:", StringComparison.OrdinalIgnoreCase))
            {
                sourceFunction = line["SourceFunction:".Length..].Trim();
            }
            else if (line.StartsWith("SourceLocation:", StringComparison.OrdinalIgnoreCase))
            {
                sourceLocation = line["SourceLocation:".Length..].Trim();
            }
            else if (line.StartsWith("ExceptionCode:", StringComparison.OrdinalIgnoreCase))
            {
                exceptionCode = line["ExceptionCode:".Length..].Trim();
            }
            else if (line.StartsWith("FaultAddress:", StringComparison.OrdinalIgnoreCase))
            {
                faultAddress = line["FaultAddress:".Length..].Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(sourceLocation) && !sourceLocation.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            var reason = exceptionCode switch
            {
                "0xC0000005" => "Access violation (likely null/invalid pointer dereference).",
                _ => string.IsNullOrWhiteSpace(exceptionCode) ? "Crash reason from exception code is unavailable." : $"Exception code {exceptionCode}."
            };

            return $"Crash happened at {sourceLocation} in {sourceFunction ?? "<unknown>"}.{Environment.NewLine}FaultAddress: {faultAddress ?? "Unknown"}.{Environment.NewLine}Why: {reason}";
        }

        return null;
    }

}
