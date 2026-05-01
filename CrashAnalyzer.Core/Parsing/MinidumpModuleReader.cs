using System.Buffers.Binary;

namespace CrashAnalyzer.Core.Parsing;

internal static class MinidumpModuleReader
{
    private const uint MinidumpSignature = 0x504D444D; // 'MDMP'
    private const uint ModuleListStreamType = 4;

    public static IReadOnlyList<NativeModuleInfo> Read(string dumpFilePath)
    {
        if (string.IsNullOrWhiteSpace(dumpFilePath) || !File.Exists(dumpFilePath))
        {
            return [];
        }

        using var stream = File.OpenRead(dumpFilePath);
        using var reader = new BinaryReader(stream);

        if (stream.Length < 32)
        {
            return [];
        }

        var signature = reader.ReadUInt32();
        if (signature != MinidumpSignature)
        {
            return [];
        }

        _ = reader.ReadUInt32(); // version
        var streamCount = reader.ReadUInt32();
        var streamDirectoryRva = reader.ReadUInt32();

        if (streamCount == 0 || streamDirectoryRva == 0)
        {
            return [];
        }

        uint moduleListRva = 0;
        uint moduleListSize = 0;

        for (var i = 0; i < streamCount; i++)
        {
            var dirOffset = streamDirectoryRva + (uint)(i * 12);
            if (dirOffset + 12 > stream.Length)
            {
                return [];
            }

            stream.Position = dirOffset;
            var streamType = reader.ReadUInt32();
            var dataSize = reader.ReadUInt32();
            var dataRva = reader.ReadUInt32();

            if (streamType == ModuleListStreamType)
            {
                moduleListRva = dataRva;
                moduleListSize = dataSize;
                break;
            }
        }

        if (moduleListRva == 0 || moduleListSize < 4 || moduleListRva + moduleListSize > stream.Length)
        {
            return [];
        }

        stream.Position = moduleListRva;
        var moduleCount = reader.ReadUInt32();
        var results = new List<NativeModuleInfo>((int)Math.Min(moduleCount, 1024));

        const int moduleRecordSize = 108;

        for (var i = 0; i < moduleCount; i++)
        {
            var recordOffset = moduleListRva + 4 + (uint)(i * moduleRecordSize);
            if (recordOffset + moduleRecordSize > stream.Length)
            {
                break;
            }

            stream.Position = recordOffset;
            var baseAddressBytes = reader.ReadBytes(8);
            if (baseAddressBytes.Length < 8)
            {
                break;
            }

            var baseAddress = BinaryPrimitives.ReadUInt64LittleEndian(baseAddressBytes);
            var size = reader.ReadUInt32();
            _ = reader.ReadUInt32(); // checksum
            _ = reader.ReadUInt32(); // timestamp
            var moduleNameRva = reader.ReadUInt32();

            var modulePath = ReadMinidumpString(reader, stream, moduleNameRva);
            if (string.IsNullOrWhiteSpace(modulePath) || size == 0)
            {
                continue;
            }

            results.Add(new NativeModuleInfo(modulePath, baseAddress, size));
        }

        return results;
    }

    private static string? ReadMinidumpString(BinaryReader reader, FileStream stream, uint rva)
    {
        if (rva == 0 || rva + 4 > stream.Length)
        {
            return null;
        }

        var previous = stream.Position;
        try
        {
            stream.Position = rva;
            var byteCount = reader.ReadUInt32();
            if (byteCount == 0 || rva + 4 + byteCount > stream.Length)
            {
                return null;
            }

            var bytes = reader.ReadBytes((int)byteCount);
            if (bytes.Length == 0)
            {
                return null;
            }

            return System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }
        finally
        {
            stream.Position = previous;
        }
    }
}

internal readonly record struct NativeModuleInfo(string ModulePath, ulong BaseAddress, uint Size);
