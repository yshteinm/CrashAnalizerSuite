namespace CrashAnalyzer.Core.Parsing;

public static class CrashTypeDetector
{
    public static string Detect(string? filePath, string? content)
    {
        if (IsDmp(filePath))
        {
            return "Dmp";
        }

        var text = content ?? string.Empty;

        if (Contains(text, "Exception Type:"))
        {
            return "IOS";
        }

        if (Contains(text, "signal") && Contains(text, "pid:"))
        {
            return "Android";
        }

        if (Contains(text, "Unhandled exception"))
        {
            return "DotNet";
        }

        if (Contains(text, "SIGSEGV") || Contains(text, "Access violation"))
        {
            return "Cpp";
        }

        if (Contains(text, "java.lang."))
        {
            return "JVM";
        }

        return "Unknown";
    }

    private static bool IsDmp(string? filePath)
        => string.Equals(Path.GetExtension(filePath), ".dmp", StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string source, string value)
        => source.Contains(value, StringComparison.OrdinalIgnoreCase);
}
