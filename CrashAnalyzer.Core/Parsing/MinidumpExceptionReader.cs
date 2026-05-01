using System.Buffers.Binary;

namespace CrashAnalyzer.Core.Parsing;

internal static class MinidumpExceptionReader
{
    private const uint MinidumpSignature = 0x504D444D; // 'MDMP'
    private const uint ExceptionStreamType = 6;

    public static NativeExceptionInfo? Read(string dumpFilePath)
    {
        if (string.IsNullOrWhiteSpace(dumpFilePath) || !File.Exists(dumpFilePath))
        {
            return null;
        }

        using var stream = File.OpenRead(dumpFilePath);
        using var reader = new BinaryReader(stream);

        if (stream.Length < 32)
        {
            return null;
        }

        var signature = reader.ReadUInt32();
        if (signature != MinidumpSignature)
        {
            return null;
        }

        _ = reader.ReadUInt32(); // version
        var streamCount = reader.ReadUInt32();
        var streamDirectoryRva = reader.ReadUInt32();

        if (streamCount == 0 || streamDirectoryRva == 0)
        {
            return null;
        }

        for (var i = 0; i < streamCount; i++)
        {
            var dirOffset = streamDirectoryRva + (uint)(i * 12);
            if (dirOffset + 12 > stream.Length)
            {
                return null;
            }

            stream.Position = dirOffset;
            var streamType = reader.ReadUInt32();
            var dataSize = reader.ReadUInt32();
            var dataRva = reader.ReadUInt32();

            if (streamType != ExceptionStreamType || dataSize < 168)
            {
                continue;
            }

            if (dataRva + dataSize > stream.Length)
            {
                return null;
            }

            stream.Position = dataRva;
            var threadId = reader.ReadUInt32();
            _ = reader.ReadUInt32(); // alignment

            var exceptionCode = reader.ReadUInt32();
            var exceptionFlags = reader.ReadUInt32();
            _ = reader.ReadUInt64(); // exception record pointer

            var exceptionAddressBytes = reader.ReadBytes(8);
            if (exceptionAddressBytes.Length < 8)
            {
                return null;
            }

            var exceptionAddress = BinaryPrimitives.ReadUInt64LittleEndian(exceptionAddressBytes);

            return new NativeExceptionInfo(threadId, exceptionCode, exceptionFlags, exceptionAddress);
        }

        return null;
    }
}

internal readonly record struct NativeExceptionInfo(
    uint ThreadId,
    uint ExceptionCode,
    uint ExceptionFlags,
    ulong ExceptionAddress);
