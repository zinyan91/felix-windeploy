using System.Buffers.Binary;

namespace Felix.Partition.Services;

public sealed record NtfsVolumeSize(ulong Serial, long Sectors, long Clusters, int BytesPerSector, int BytesPerCluster)
{
    public static NtfsVolumeSize Read(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 96) throw Invalid();
        var result = new NtfsVolumeSize(BinaryPrimitives.ReadUInt64LittleEndian(buffer),
            BinaryPrimitives.ReadInt64LittleEndian(buffer[8..]), BinaryPrimitives.ReadInt64LittleEndian(buffer[16..]),
            BinaryPrimitives.ReadInt32LittleEndian(buffer[40..]), BinaryPrimitives.ReadInt32LittleEndian(buffer[44..]));
        result.Validate();
        return result;
    }

    public void Validate()
    {
        if (Sectors <= 0 || Clusters <= 0 || BytesPerSector is not (512 or 1024 or 2048 or 4096) ||
            BytesPerCluster < BytesPerSector || BytesPerCluster > 2 * 1048576 ||
            (BytesPerCluster & (BytesPerCluster - 1)) != 0 ||
            Clusters > Sectors / (BytesPerCluster / BytesPerSector)) throw Invalid();
    }

    public void RequireWithin(long partitionBytes)
    {
        Validate();
        var maximum = partitionBytes / BytesPerSector;
        if (partitionBytes <= 0 || partitionBytes % BytesPerSector != 0 || Sectors > maximum ||
            maximum - Sectors > BytesPerCluster / BytesPerSector) throw Invalid();
    }

    public void RequireExtended(NtfsVolumeSize before, long partitionBytes)
    {
        RequireWithin(partitionBytes);
        before.Validate();
        if (Serial != before.Serial || BytesPerSector != before.BytesPerSector || BytesPerCluster != before.BytesPerCluster ||
            Sectors <= before.Sectors || Clusters <= before.Clusters) throw Invalid();
    }
    private static InvalidOperationException Invalid() => new("Partition_Error_MergeNtfsSize");
}
