namespace CrashAnalyzer.Core.Llm;

public static class DumpAnalysisPrompt
{
    public const string Template = """
You are a senior Windows debugging engineer. Analyze the following minidump data and produce:
1. Root cause
2. Faulting function
3. Exception code meaning
4. COM apartment issues
5. HRESULT interpretation
6. Native vs managed stack correlation
7. Likely fix
8. Confidence (0–100%)
""";
}
