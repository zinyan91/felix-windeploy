using Felix.Partition.Services;
using Felix.Partition.Services.Native;
using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.Services;

public sealed class NativeTargetSource : ITargetSource
{
    public Task<IReadOnlyList<TargetDiskFacts>> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<TargetDiskFacts>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = NativeStorageReader.Read();
            StorageSnapshotRules.Check(snapshot);
            return snapshot.Disks.Select(Map).ToArray();
        }, cancellationToken);

    internal static TargetDiskFacts Map(StorageDisk disk)
    {
        var partitions = disk.Partitions.Select(partition =>
        {
            var gptType = partition.GptType.Trim('{', '}');
            return new TargetPartitionFacts(
                partition.Number,
                partition.Offset,
                partition.Size,
                partition.Letter,
                partition.Label,
                partition.FileSystem,
                partition.Guid,
                partition.VolumeSerial,
                partition.IsSystem || gptType.Equals("C12A7328-F81F-11D2-BA4B-00A0C93EC93B", StringComparison.OrdinalIgnoreCase),
                gptType.Equals("E3C9E316-0B5C-4DB8-817D-F92DF00215AE", StringComparison.OrdinalIgnoreCase),
                gptType.Equals("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC", StringComparison.OrdinalIgnoreCase) ||
                    partition.GptName.Contains("Recovery", StringComparison.OrdinalIgnoreCase) ||
                    partition.Label.Contains("Recovery", StringComparison.OrdinalIgnoreCase),
                partition.IsActive,
                partition.Encryption.Equals("Encrypted", StringComparison.OrdinalIgnoreCase),
                partition.Unallocated,
                partition.HasWindows,
                partition.Free);
        }).ToArray();
        return new TargetDiskFacts(
            disk.Number,
            disk.Id,
            disk.Serial,
            disk.Model,
            disk.Bus,
            disk.Style,
            disk.Size,
            disk.HoldsPeMedia,
            disk.Partitions.Any(partition => partition.IsBoot || partition.HasBootManager),
            disk.IsReadOnly,
            disk.IsOffline,
            disk.IsDynamic,
            IsVirtual(disk.Model),
            partitions);
    }

    private static bool IsVirtual(string model) =>
        model.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("VHD", StringComparison.OrdinalIgnoreCase);
}
