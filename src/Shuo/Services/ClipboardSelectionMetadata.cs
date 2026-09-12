using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Shuo.Services;

internal static class ClipboardSelectionMetadata
{
    // Chromium/Electron serialize web copy metadata as a Pickle map of UTF-16 strings.
    // The copy operation can explicitly say it copied a line from an empty selection.
    internal static bool IsEmptySelection(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return false;
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (payloadLength > data.Length - 4) return false;
        var bytes = data[..((int)payloadLength + 4)].ToArray();
        var offset = 4;
        if (!ReadLength(out var count) || count > 128) return false;
        for (var index = 0; index < count; index++)
        {
            if (!ReadString(out _) || !ReadString(out var value)) return false;
            try
            {
                using var json = JsonDocument.Parse(value);
                if (json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("isFromEmptySelection", out var empty)
                    && empty.ValueKind == JsonValueKind.True) return true;
            }
            catch (JsonException) { }
        }
        return false;

        bool ReadLength(out int value)
        {
            value = 0;
            if (offset > bytes.Length - 4) return false;
            value = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
            offset += 4;
            return value >= 0;
        }

        bool ReadString(out string value)
        {
            value = "";
            if (!ReadLength(out var length) || length > (bytes.Length - offset) / 2) return false;
            value = Encoding.Unicode.GetString(bytes, offset, length * 2);
            offset += (length * 2 + 3) & ~3;
            return offset <= bytes.Length;
        }
    }
}
