using Microsoft.Win32.SafeHandles;

namespace Felix.Partition.Services.Native;

internal static class NativeNtfsSize
{
    public static NtfsVolumeSize Read(SafeFileHandle volume) =>
        NtfsVolumeSize.Read(Win32.Query(volume, 0x00090064, initialSize: 128)
            ?? throw new InvalidOperationException("Partition_Error_MergeNtfsSize"));
}
