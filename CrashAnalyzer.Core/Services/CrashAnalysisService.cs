using System.Text;
using CrashAnalyzer.Core.Models;
using CrashAnalyzer.Core.Parsing;

namespace CrashAnalyzer.Core.Services;

public sealed class CrashAnalysisService
{
    public string AnalyzeDump(string dumpPath, string? pdbPath = null, string? symbolizerPath = null)
    {
        if (string.IsNullOrWhiteSpace(dumpPath))
        {
            return "Dump path is required.";
        }

        if (!File.Exists(dumpPath))
        {
            return $"Dump file not found: {dumpPath}";
        }

        try
        {
            var parsed = new DmpParser().Parse(dumpPath, pdbPath, symbolizerPath);
            return Format(parsed, dumpPath);
        }
        catch (Exception ex)
        {
            return $"Failed to analyze dump '{dumpPath}'.{Environment.NewLine}{ex.Message}";
        }
    }

    private static string Format(ParsedCrash crash, string dumpPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Dump: {dumpPath}");
        builder.AppendLine($"CrashType: {crash.CrashType ?? "Unknown"}");
        builder.AppendLine($"ExceptionType: {crash.ExceptionType ?? "Unknown"}");
        builder.AppendLine($"ExceptionCode: {crash.ExceptionCode ?? "Unknown"}");
        builder.AppendLine($"ExceptionFlags: {crash.ExceptionFlags ?? "Unknown"}");
        builder.AppendLine($"HResult: {crash.HResult ?? "Unknown"}");
        builder.AppendLine($"FaultAddress: {crash.FaultAddress ?? "Unknown"}");
        builder.AppendLine($"FaultingModule: {crash.FaultingModule ?? "Unknown"}");
        builder.AppendLine($"FaultingModuleOffset: {crash.FaultingModuleOffset ?? "Unknown"}");
        builder.AppendLine($"SourceFunction: {crash.SourceFunction ?? "Unknown"}");
        builder.AppendLine($"SourceLocation: {crash.SourceLocation ?? "Unknown"}");
        builder.AppendLine($"FaultingThreadId: {crash.FaultingThreadId ?? "Unknown"}");
        builder.AppendLine($"ComApartment: {crash.ComApartment ?? "Unknown"}");

        if (crash.Conditions.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Conditions:");
            foreach (var condition in crash.Conditions)
            {
                builder.AppendLine($"  - {condition}");
            }
        }

        if (crash.ManagedStack.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Managed stack:");
            foreach (var frame in crash.ManagedStack)
            {
                builder.AppendLine($"  {frame}");
            }
        }

        if (crash.NativeStack.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Native stack:");
            foreach (var frame in crash.NativeStack)
            {
                builder.AppendLine($"  {frame}");
            }
        }

        if (crash.Registers.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Registers:");
            foreach (var register in crash.Registers)
            {
                builder.AppendLine($"  {register.Key} = {register.Value}");
            }
        }

        if (crash.Modules.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"Modules ({crash.Modules.Count}):");
            foreach (var module in crash.Modules)
            {
                builder.AppendLine($"  {module}");
            }
        }

        return builder.ToString();
    }
}
