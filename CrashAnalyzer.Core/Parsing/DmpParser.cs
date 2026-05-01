using System.Globalization;
using System.IO;
using System.Reflection;
using CrashAnalyzer.Core.Models;
using Microsoft.Diagnostics.Runtime;

namespace CrashAnalyzer.Core.Parsing;

public sealed class DmpParser
{
    public ParsedCrash Parse(string dumpFilePath, string? pdbFilePath = null, string? symbolizerPath = null)
    {
        if (string.IsNullOrWhiteSpace(dumpFilePath))
        {
            throw new ArgumentException("Dump file path is required.", nameof(dumpFilePath));
        }

        var parsed = new ParsedCrash
        {
            CrashType = "Dmp",
            RawText = $"Dump file: {dumpFilePath}"
        };

        using var dataTarget = DataTarget.LoadDump(dumpFilePath);
        ExtractClrData(dataTarget, parsed);
        var modules = ExtractModules(dataTarget, parsed, dumpFilePath);
        ExtractNativeExceptionStream(dumpFilePath, parsed);
        InferFaultingModule(parsed, modules);
        ResolveSourceLocation(parsed, modules, pdbFilePath, symbolizerPath);
        BuildConditions(parsed);

        return parsed;
    }

    private static void ExtractNativeExceptionStream(string dumpFilePath, ParsedCrash parsed)
    {
        var nativeException = MinidumpExceptionReader.Read(dumpFilePath);
        if (nativeException is null)
        {
            return;
        }

        parsed.ExceptionCode ??= $"0x{nativeException.Value.ExceptionCode:X8}";
        parsed.ExceptionFlags ??= $"0x{nativeException.Value.ExceptionFlags:X8}";
        parsed.FaultAddress ??= $"0x{nativeException.Value.ExceptionAddress:X}";
        parsed.FaultingThreadId ??= nativeException.Value.ThreadId.ToString(CultureInfo.InvariantCulture);

        if (string.IsNullOrWhiteSpace(parsed.ExceptionType))
        {
            parsed.ExceptionType = nativeException.Value.ExceptionCode switch
            {
                0xC0000005 => "AccessViolationException",
                0xC0000094 => "DivideByZeroException",
                0xC000001D => "IllegalInstructionException",
                0x80000003 => "BreakpointException",
                _ => null
            };
        }
    }

    private static void ExtractClrData(DataTarget dataTarget, ParsedCrash parsed)
    {
        var clrInfo = dataTarget.ClrVersions.FirstOrDefault();
        if (clrInfo is null)
        {
            return;
        }

        var runtime = clrInfo.CreateRuntime();
        var faultingThread = runtime.Threads.FirstOrDefault(t => t.CurrentException is not null)
            ?? runtime.Threads.FirstOrDefault();

        if (faultingThread is null)
        {
            return;
        }

        parsed.FaultingThreadId = faultingThread.OSThreadId.ToString(CultureInfo.InvariantCulture);
        parsed.ComApartment = GetComApartment(faultingThread);

        ExtractStackTraces(faultingThread, parsed);
        ExtractRegisters(faultingThread, parsed);

        var currentException = faultingThread.CurrentException;
        if (currentException is not null)
        {
            parsed.ExceptionType = currentException.Type?.Name;

            parsed.HResult ??= TryGetHexProperty(currentException, "HResult")
                ?? TryGetHexProperty(currentException, "HRESULT");

            parsed.ExceptionCode ??= TryGetHexProperty(currentException, "Code")
                ?? TryGetHexProperty(currentException, "ExceptionCode")
                ?? parsed.HResult;

            parsed.FaultAddress ??= TryGetHexProperty(currentException, "Address")
                ?? TryGetHexProperty(currentException, "InstructionPointer");
        }

        var hresults = runtime.Threads
            .Select(t => t.CurrentException)
            .Where(e => e is not null)
            .Select(e => TryGetHexProperty(e!, "HResult") ?? TryGetHexProperty(e!, "HRESULT"))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct()
            .ToArray();

        if (hresults.Length > 0)
        {
            parsed.HResult = string.Join(", ", hresults);
        }
    }

    private static List<ModuleRange> ExtractModules(DataTarget dataTarget, ParsedCrash parsed, string dumpFilePath)
    {
        var ranges = new List<ModuleRange>();

        foreach (var module in dataTarget.EnumerateModules())
        {
            var moduleName = module.FileName;
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                continue;
            }

            var version = module.Version?.ToString();

            parsed.Modules.Add(string.IsNullOrWhiteSpace(version)
                ? moduleName
                : $"{moduleName} ({version})");

            var imageBase = TryGetULongProperty(module, "ImageBase");
            var fileSize = TryGetULongProperty(module, "FileSize") ?? TryGetULongProperty(module, "ImageSize");
            if (imageBase.HasValue && fileSize.HasValue && fileSize.Value > 0)
            {
                ranges.Add(new ModuleRange(moduleName, imageBase.Value, imageBase.Value + fileSize.Value));
            }
        }

        if (ranges.Count == 0)
        {
            foreach (var module in MinidumpModuleReader.Read(dumpFilePath))
            {
                if (!parsed.Modules.Contains(module.ModulePath, StringComparer.OrdinalIgnoreCase))
                {
                    parsed.Modules.Add(module.ModulePath);
                }

                ranges.Add(new ModuleRange(module.ModulePath, module.BaseAddress, module.BaseAddress + module.Size));
            }
        }

        return ranges;
    }

    private static void InferFaultingModule(ParsedCrash parsed, List<ModuleRange> modules)
    {
        if (string.IsNullOrWhiteSpace(parsed.FaultAddress))
        {
            return;
        }

        var faultAddress = TryParseHexAddress(parsed.FaultAddress);
        if (faultAddress is null)
        {
            return;
        }

        foreach (var module in modules)
        {
            if (faultAddress.Value >= module.StartAddress && faultAddress.Value < module.EndAddress)
            {
                var moduleName = Path.GetFileName(module.ModulePath);
                parsed.FaultingModule = moduleName;
                var offset = faultAddress.Value - module.StartAddress;
                parsed.FaultingModuleOffset = $"{moduleName}+0x{offset:X}";
                break;
            }
        }
    }

    private static ulong? TryParseHexAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var hex = value.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[2..];
        }

        return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static void BuildConditions(ParsedCrash parsed)
    {
        if (!string.IsNullOrWhiteSpace(parsed.ExceptionCode))
        {
            var description = parsed.ExceptionCode switch
            {
                "0xC0000005" => "Access violation (invalid memory read/write).",
                "0x80000003" => "Breakpoint exception.",
                "0xC000001D" => "Illegal instruction.",
                "0xC0000094" => "Integer divide by zero.",
                "0xC0000409" => "Stack buffer overrun / fast-fail.",
                "0xE0434352" => ".NET managed exception propagated into native boundary.",
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(description))
            {
                parsed.Conditions.Add(description);
            }
        }

        if (string.IsNullOrWhiteSpace(parsed.ExceptionCode) && parsed.ManagedStack.Count == 0 && parsed.NativeStack.Count > 0)
        {
            parsed.Conditions.Add("Likely native crash. Exception code stream is unavailable in this dump reader path.");
        }

        if (!string.IsNullOrWhiteSpace(parsed.FaultAddress) && parsed.FaultAddress.Equals("0x0", StringComparison.OrdinalIgnoreCase))
        {
            parsed.Conditions.Add("Fault address is null; likely null pointer dereference.");
        }

        var hasResolvedLocation =
            !string.IsNullOrWhiteSpace(parsed.SourceLocation)
            && !string.Equals(parsed.SourceLocation, "Unknown", StringComparison.OrdinalIgnoreCase);

        var hasResolvedModuleOffset =
            !string.IsNullOrWhiteSpace(parsed.FaultingModuleOffset)
            && !string.Equals(parsed.FaultingModuleOffset, "Unknown", StringComparison.OrdinalIgnoreCase);

        if (parsed.ManagedStack.Count == 0
            && parsed.NativeStack.Count == 0
            && !hasResolvedLocation
            && !hasResolvedModuleOffset)
        {
            parsed.Conditions.Add("No stack frames were extracted from the dump. Dump may be too minimal or symbols are unavailable.");
        }
    }

    private static void ExtractStackTraces(ClrThread thread, ParsedCrash parsed)
    {
        foreach (var frame in thread.EnumerateStackTrace(includeContext: true))
        {
            var frameText = BuildFrameText(frame);
            if (string.IsNullOrWhiteSpace(frameText))
            {
                continue;
            }

            var kind = frame.Kind.ToString();
            if (kind.Contains("Managed", StringComparison.OrdinalIgnoreCase))
            {
                parsed.ManagedStack.Add(frameText);
            }
            else
            {
                parsed.NativeStack.Add(frameText);
            }
        }

        if (string.IsNullOrWhiteSpace(parsed.FaultAddress) && parsed.NativeStack.Count > 0)
        {
            parsed.FaultAddress = parsed.NativeStack[0];
        }
    }

    private static void ExtractRegisters(ClrThread thread, ParsedCrash parsed)
    {
        var registerContainer = TryGetPropertyValue(thread, "Registers")
            ?? TryGetPropertyValue(thread, "RegisterSet")
            ?? TryGetPropertyValue(thread, "Context");

        if (registerContainer is null)
        {
            return;
        }

        if (registerContainer is System.Collections.IEnumerable collection && registerContainer is not string)
        {
            foreach (var item in collection)
            {
                if (item is null)
                {
                    continue;
                }

                var name = TryGetPropertyValue(item, "RegisterName")?.ToString()
                    ?? TryGetPropertyValue(item, "Name")?.ToString();

                var value = TryGetPropertyValue(item, "Value");
                var valueText = ToHexOrString(value);

                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(valueText))
                {
                    parsed.Registers[name] = valueText;
                }
            }

            if (parsed.Registers.Count > 0)
            {
                return;
            }
        }

        var properties = registerContainer.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var property in properties)
        {
            var value = property.GetValue(registerContainer);
            var valueText = ToHexOrString(value);
            if (!string.IsNullOrWhiteSpace(valueText))
            {
                parsed.Registers[property.Name] = valueText;
            }
        }
    }

    private static string? GetComApartment(ClrThread thread)
    {
        var apartmentState = TryGetPropertyValue(thread, "ApartmentState")?.ToString();
        if (!string.IsNullOrWhiteSpace(apartmentState))
        {
            return apartmentState;
        }

        var isSta = TryGetPropertyValue(thread, "IsSTA") as bool?;
        if (isSta == true)
        {
            return "STA";
        }

        var isMta = TryGetPropertyValue(thread, "IsMTA") as bool?;
        if (isMta == true)
        {
            return "MTA";
        }

        return null;
    }

    private static string BuildFrameText(ClrStackFrame frame)
    {
        var methodName = frame.Method?.Signature ?? frame.Method?.Name ?? frame.FrameName ?? "<unknown>";
        return $"0x{frame.InstructionPointer:X} {methodName}";
    }

    private static object? TryGetPropertyValue(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        return property?.GetValue(instance);
    }

    private static string? TryGetHexProperty(object instance, string propertyName)
    {
        var value = TryGetPropertyValue(instance, propertyName);
        return ToHexOrString(value, preferHex: true);
    }

    private static ulong? TryGetULongProperty(object instance, string propertyName)
    {
        var value = TryGetPropertyValue(instance, propertyName);
        return value switch
        {
            byte v => v,
            ushort v => v,
            uint v => v,
            ulong v => v,
            int v when v >= 0 => (ulong)v,
            long v when v >= 0 => (ulong)v,
            _ => null
        };
    }

    private static string? ToHexOrString(object? value, bool preferHex = false)
    {
        return value switch
        {
            null => null,
            byte v => preferHex ? $"0x{v:X2}" : v.ToString(CultureInfo.InvariantCulture),
            short v => preferHex ? $"0x{v:X}" : v.ToString(CultureInfo.InvariantCulture),
            ushort v => preferHex ? $"0x{v:X}" : v.ToString(CultureInfo.InvariantCulture),
            int v => preferHex ? $"0x{v:X}" : v.ToString(CultureInfo.InvariantCulture),
            uint v => preferHex ? $"0x{v:X}" : v.ToString(CultureInfo.InvariantCulture),
            long v => preferHex ? $"0x{v:X}" : v.ToString(CultureInfo.InvariantCulture),
            ulong v => preferHex ? $"0x{v:X}" : v.ToString(CultureInfo.InvariantCulture),
            nint v => preferHex ? $"0x{v:X}" : v.ToString(CultureInfo.InvariantCulture),
            nuint v => preferHex ? $"0x{v:X}" : v.ToString(CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    private sealed record ModuleRange(string ModulePath, ulong StartAddress, ulong EndAddress);

    private static void ResolveSourceLocation(ParsedCrash parsed, List<ModuleRange> modules, string? pdbFilePath, string? configuredSymbolizerPath)
    {
        if (string.IsNullOrWhiteSpace(parsed.FaultAddress))
        {
            return;
        }

        var faultAddress = TryParseHexAddress(parsed.FaultAddress);
        if (!faultAddress.HasValue)
        {
            return;
        }

        var module = modules.FirstOrDefault(m => faultAddress.Value >= m.StartAddress && faultAddress.Value < m.EndAddress);
        if (module is null && !string.IsNullOrWhiteSpace(pdbFilePath))
        {
            var pdbBaseName = Path.GetFileNameWithoutExtension(pdbFilePath);
            module = modules.FirstOrDefault(m =>
                string.Equals(Path.GetFileNameWithoutExtension(m.ModulePath), pdbBaseName, StringComparison.OrdinalIgnoreCase));
        }

        if (module is null)
        {
            return;
        }

        var exePath = module.ModulePath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return;
        }

        var relativeAddress = faultAddress.Value - module.StartAddress;
        var symbolizerPath = !string.IsNullOrWhiteSpace(configuredSymbolizerPath)
            ? configuredSymbolizerPath
            : FindLlvmSymbolizerPath();
        if (string.IsNullOrWhiteSpace(symbolizerPath) || !File.Exists(symbolizerPath))
        {
            parsed.Conditions.Add("llvm-symbolizer was not found. Install Visual C++ tools to enable source mapping.");
            return;
        }

        var symbolized = RunSymbolizer(symbolizerPath, exePath, relativeAddress, pdbFilePath);
        if (string.IsNullOrWhiteSpace(symbolized))
        {
            return;
        }

        var lines = symbolized
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Where(l => !l.Contains("not compiled with support for DIA", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (lines.Length >= 1)
        {
            parsed.SourceFunction = lines[0];
        }

        if (lines.Length >= 2)
        {
            parsed.SourceLocation = lines[1];
        }

        if (!string.IsNullOrWhiteSpace(pdbFilePath) && string.IsNullOrWhiteSpace(parsed.SourceLocation))
        {
            parsed.Conditions.Add("PDB provided but source line could not be resolved. Verify PDB matches the crashing binary build.");
        }
    }

    private static string? RunSymbolizer(string symbolizerPath, string exePath, ulong relativeAddress, string? pdbFilePath)
    {
        var debugDir = !string.IsNullOrWhiteSpace(pdbFilePath) ? Path.GetDirectoryName(pdbFilePath) : null;

        var withDia = BuildSymbolizerArgs(exePath, relativeAddress, debugDir, useDia: true);
        var result = ExecuteSymbolizer(symbolizerPath, withDia);
        if (!string.IsNullOrWhiteSpace(result) && !result.Contains("not compiled with support for DIA", StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        var withoutDia = BuildSymbolizerArgs(exePath, relativeAddress, debugDir, useDia: false);
        return ExecuteSymbolizer(symbolizerPath, withoutDia);
    }

    private static string BuildSymbolizerArgs(string exePath, ulong relativeAddress, string? debugDir, bool useDia)
    {
        var args = new List<string>();

        if (!string.IsNullOrWhiteSpace(debugDir))
        {
            args.Add("--debug-file-directory");
            args.Add(Quote(debugDir));
        }

        args.Add("--obj");
        args.Add(Quote(exePath));
        args.Add("--relative-address");
        args.Add($"0x{relativeAddress:X}");

        if (useDia)
        {
            args.Add("--dia");
        }

        return string.Join(" ", args);
    }

    private static string? ExecuteSymbolizer(string symbolizerPath, string arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = symbolizerPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            var output = proc.StandardOutput.ReadToEnd();
            var error = proc.StandardError.ReadToEnd();
            proc.WaitForExit(3000);

            if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
            {
                return string.IsNullOrWhiteSpace(error) ? null : error;
            }

            return string.IsNullOrWhiteSpace(output) ? error : output;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindLlvmSymbolizerPath()
    {
        var fromPath = FindInPath("llvm-symbolizer.exe");
        if (!string.IsNullOrWhiteSpace(fromPath))
        {
            return fromPath;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles) || !Directory.Exists(programFiles))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(programFiles, "llvm-symbolizer.exe", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? FindInPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(segment, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static string Quote(string value)
        => value.Contains(' ') ? $"\"{value}\"" : value;
}
