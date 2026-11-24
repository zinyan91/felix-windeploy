using System.Buffers.Binary;

namespace Felix.Partition.Services.Native;

public sealed record LayoutEntry(int Number, long Offset, long Length, Guid GptType, Guid GptId, ulong GptAttributes,
    byte MbrType, bool MbrBoot, bool IsLogical = false, string GptName = "");

public sealed record DriveLayout(string Style, Guid GptDiskId, uint MbrSignature, IReadOnlyList<LayoutEntry> Entries,
    long ExtendedOffset = 0, long ExtendedSize = 0)
{
    /// <summary>MBR only: where each EBR of the chain sits, first one at the extended start.</summary>
    public IReadOnlyList<long> EbrOffsets { get; init; } = [];
}

/// <summary>Decodes DRIVE_LAYOUT_INFORMATION_EX (x64 layout) returned by IOCTL_DISK_GET_DRIVE_LAYOUT_EX.</summary>
public static class DriveLayoutParser
{
    private const int HeaderSize = 48;
    private const int EntrySize = 144;

    // Extended-partition containers hold the logical partitions; they are not volumes themselves.
    private static readonly byte[] MbrContainers = [0x05, 0x0F, 0x85];

    public static DriveLayout Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize) throw new InvalidOperationException("Partition_Error_Snapshot");
        var style = BinaryPrimitives.ReadUInt32LittleEndian(data) switch { 0 => "MBR", 1 => "GPT", 2 => "RAW", _ => "Unknown" };
        var count = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        if (count < 0 || data.Length < HeaderSize + (long)count * EntrySize) throw new InvalidOperationException("Partition_Error_Snapshot");
        var diskId = style == "GPT" ? new Guid(data.Slice(8, 16)) : Guid.Empty;
        var signature = style == "MBR" ? BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) : 0;
        var entries = new List<LayoutEntry>();
        long extendedOffset = 0, extendedSize = 0;
        var ebrs = new SortedSet<long>();
        for (var i = 0; i < count; i++)
        {
            var e = data.Slice(HeaderSize + i * EntrySize, EntrySize);
            var offset = BinaryPrimitives.ReadInt64LittleEndian(e[8..]);
            var length = BinaryPrimitives.ReadInt64LittleEndian(e[16..]);
            var number = BinaryPrimitives.ReadInt32LittleEndian(e[24..]);
            var info = e[32..];
            if (style == "GPT")
            {
                if (length <= 0) continue;
                entries.Add(new(number, offset, length, new Guid(info[..16]), new Guid(info.Slice(16, 16)),
                    BinaryPrimitives.ReadUInt64LittleEndian(info[32..]), 0, false,
                    GptName: System.Text.Encoding.Unicode.GetString(info.Slice(40, 72)).TrimEnd('\0')));
            }
            else if (style == "MBR")
            {
                var type = info[0];
                if (length <= 0 || type == 0) continue;
                // The outermost container is the extended partition; the EBRs nested inside carry the same types.
                if (MbrContainers.Contains(type))
                {
                    ebrs.Add(offset);
                    if (extendedSize == 0 || offset < extendedOffset) { extendedOffset = offset; extendedSize = length; }
                    continue;
                }
                if (number <= 0) continue;
                entries.Add(new(number, offset, length, Guid.Empty, Guid.Empty, 0, type, info[1] != 0));
            }
        }
        if (extendedSize > 0)
            entries = entries.Select(e => e.Offset >= extendedOffset && e.Offset + e.Length <= extendedOffset + extendedSize
                ? e with { IsLogical = true } : e).ToList();
        // A blank disk is reported as MBR with no signature and no entries; Windows calls it RAW (not initialized).
        if (style == "MBR" && signature == 0 && entries.Count == 0) style = "RAW";
        return new(style, diskId, signature, entries, extendedOffset, extendedSize) { EbrOffsets = ebrs.ToList() };
    }
}
