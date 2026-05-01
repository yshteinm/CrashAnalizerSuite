using System.Collections.Generic;

namespace CrashAnalyzer.Core.Models;

public sealed class ParsedCrash
{
    public string? CrashType;
    public string? ExceptionType;
    public string? ExceptionCode;
    public string? ExceptionFlags;
    public string? FaultAddress;
    public string? FaultingModule;
    public string? FaultingModuleOffset;
    public string? SourceFunction;
    public string? SourceLocation;
    public string? FaultingThreadId;
    public List<string> ManagedStack = [];
    public List<string> NativeStack = [];
    public Dictionary<string, string> Registers = [];
    public List<string> Modules = [];
    public List<string> Conditions = [];
    public string? ComApartment;
    public string? HResult;
    public string? RawText;
}
