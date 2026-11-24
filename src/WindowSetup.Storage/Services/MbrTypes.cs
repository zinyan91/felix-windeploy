namespace Felix.Partition.Services;

public static class MbrTypes
{
    private static readonly byte[] Data = [0x07, 0x0B, 0x0C, 0x0E, 0x06];

    public static bool IsData(byte type) => Data.Contains(type) || Data.Contains(Unhide(type));
    public static bool IsHiddenData(byte type) => Data.Contains(Unhide(type)) && type != Unhide(type);
    public static byte Hide(byte type) => Data.Contains(type) ? (byte)(type | 0x10) : type;
    public static byte Unhide(byte type) => Data.Contains((byte)(type & 0xEF)) ? (byte)(type & 0xEF) : type;

    public static string Name(byte type) => Unhide(type) switch
    {
        0x07 => "IFS", 0x0B => "FAT32", 0x0C => "FAT32 XINT13", 0x06 => "FAT16", 0x0E => "FAT16 XINT13",
        0x27 => "Recovery", 0x83 => "Linux", 0x82 => "Linux swap", _ => "Unknown"
    };
}
