using System.IO;
using System.Runtime.InteropServices;
using Felix.WindowSetup.Core;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Felix.WindowSetup.Services;

public interface IHardwareProfileService
{
    Task<HardwareSnapshot> ProbeAsync(CancellationToken cancellationToken = default);
}

public sealed class NativeHardwareProfileService(ITargetSource targetSource) : IHardwareProfileService
{
    public async Task<HardwareSnapshot> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var disksTask = targetSource.LoadAsync(cancellationToken);

        var (cpuName, clockMhz, logicalThreads, physicalCores) = await Task.Run(ReadCpuInfo, cancellationToken);
        var totalRamBytes = ReadTotalRamBytes();
        var (gpuName, gpuVramBytes) = ReadPrimaryGpu();
        var (board, bios) = ReadBoardAndBios();
        var firmware = FirmwareDetector.Current();
        var secureBoot = HardwareCapabilityDetector.DetectSecureBoot(firmware);
        var (hasTpm20, tpmSummary) = ReadTpmStatus();
        var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        IReadOnlyList<TargetDiskFacts> rawDisks;
        try
        {
            rawDisks = await disksTask;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            rawDisks = [];
        }

        var diskSummaries = await Task.Run(() =>
        {
            var mapped = rawDisks
                .Where(d => !d.HoldsPeMedia)
                .Select(d => new HardwareDiskSummary(
                    d.Number,
                    d.Model,
                    d.Bus,
                    DetectIsSolidState(d),
                    d.Size,
                    d.Style))
                .ToArray();

            if (mapped.Length > 0)
            {
                return mapped;
            }

            var styleFallback = firmware == FirmwareMode.UefiGpt ? "GPT" : "MBR";
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .Select((d, idx) =>
                {
                    var rootTrimmed = d.Name.TrimEnd('\\');
                    return new HardwareDiskSummary(
                        idx,
                        string.IsNullOrWhiteSpace(d.VolumeLabel) ? rootTrimmed : $"{rootTrimmed} ({d.VolumeLabel})",
                        "Fixed",
                        QueryNoSeekPenalty($@"\\.\{rootTrimmed}"),
                        d.TotalSize,
                        styleFallback);
                })
                .ToArray();
        }, cancellationToken);

        return new HardwareSnapshot(
            cpuName,
            physicalCores,
            logicalThreads,
            clockMhz,
            arch,
            HardwareCapabilityDetector.CurrentCpu(),
            totalRamBytes,
            gpuName,
            gpuVramBytes,
            board,
            bios,
            firmware,
            secureBoot.SecureBootEnabled,
            hasTpm20,
            tpmSummary,
            diskSummaries);
    }

    private static (string Name, int MaxClockMhz, int Threads, int Cores) ReadCpuInfo()
    {
        var name = "Unknown CPU";
        var mhz = 0;
        var threads = 0;

        try
        {
            using var cpKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor");
            if (cpKey is not null)
            {
                var subNames = cpKey.GetSubKeyNames();
                threads = subNames.Length;
                if (subNames.Length > 0)
                {
                    using var first = cpKey.OpenSubKey(subNames[0]);
                    if (first?.GetValue("ProcessorNameString") is string s && !string.IsNullOrWhiteSpace(s))
                    {
                        name = s.Trim();
                    }
                    if (first?.GetValue("~MHz") is int m && m > 0)
                    {
                        mhz = m;
                    }
                }
            }
        }
        catch
        {
        }

        if (threads <= 0)
        {
            threads = Environment.ProcessorCount;
        }

        var physicalCores = ReadPhysicalCoreCount();
        if (physicalCores <= 0 || physicalCores > threads)
        {
            physicalCores = threads;
        }

        return (name, mhz, threads, physicalCores);
    }

    private static int ReadPhysicalCoreCount()
    {
        uint size = 0;
        GetLogicalProcessorInformationEx(0, nint.Zero, ref size);
        if (size == 0)
        {
            return 0;
        }

        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buffer, ref size))
            {
                return 0;
            }

            var count = 0;
            var offset = 0;
            while (offset < size)
            {
                var entrySize = Marshal.ReadInt32(buffer, offset + 4);
                if (entrySize <= 0)
                {
                    break;
                }
                count++;
                offset += entrySize;
            }
            return count;
        }
        catch
        {
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static long ReadTotalRamBytes()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? unchecked((long)status.TotalPhys) : 0;
    }

    private static (string Name, long? VramBytes) ReadPrimaryGpu()
    {
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey is not null)
            {
                string? fallbackName = null;
                foreach (var subName in classKey.GetSubKeyNames().Where(n => n.All(char.IsDigit)).OrderBy(n => n, StringComparer.Ordinal))
                {
                    using var gpuKey = classKey.OpenSubKey(subName);
                    var desc = gpuKey?.GetValue("DriverDesc") as string ?? gpuKey?.GetValue("HardwareInformation.AdapterString") as string;
                    if (string.IsNullOrWhiteSpace(desc))
                    {
                        continue;
                    }

                    long? vram = gpuKey?.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long l when l > 0 => l,
                        int i when i > 0 => unchecked((uint)i),
                        byte[] bytes when bytes.Length == 8 => BitConverter.ToInt64(bytes, 0),
                        byte[] bytes when bytes.Length == 4 => BitConverter.ToUInt32(bytes, 0),
                        _ => null
                    };

                    if (vram is > 0 && !desc.Contains("Basic Display", StringComparison.OrdinalIgnoreCase))
                    {
                        return (desc.Trim(), vram);
                    }

                    fallbackName ??= desc.Trim();
                }

                if (!string.IsNullOrWhiteSpace(fallbackName))
                {
                    return (fallbackName, null);
                }
            }
        }
        catch
        {
        }

        return ("Microsoft Basic Display Adapter", null);
    }

    private static (string Mainboard, string BiosVersion) ReadBoardAndBios()
    {
        try
        {
            using var biosKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var vendor = (biosKey?.GetValue("BaseBoardManufacturer") as string
                ?? biosKey?.GetValue("SystemManufacturer") as string ?? "").Trim();
            var product = (biosKey?.GetValue("BaseBoardProduct") as string
                ?? biosKey?.GetValue("SystemProductName") as string ?? "").Trim();
            var board = string.Join(" ", new[] { vendor, product }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(board))
            {
                board = "Unknown";
            }

            var biosVendor = (biosKey?.GetValue("BIOSVendor") as string ?? "").Trim();
            var biosVer = (biosKey?.GetValue("BIOSVersion") as string ?? "").Trim();
            var bios = string.Join(" ", new[] { biosVendor, biosVer }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return (board, bios);
        }
        catch
        {
            return ("Unknown", "");
        }
    }

    private static (bool HasTpm20, string Summary) ReadTpmStatus()
    {
        try
        {
            var rc = Tbsi_GetDeviceInfo(16, out var info);
            if (rc == 0)
            {
                return info.TpmVersion switch
                {
                    2 => (true, "TPM 2.0"),
                    1 => (false, "TPM 1.2"),
                    _ => (false, $"TPM v{info.TpmVersion}")
                };
            }
        }
        catch
        {
        }

        return (false, "");
    }

    private static bool DetectIsSolidState(TargetDiskFacts disk)
    {
        if (disk.Bus.Equals("NVMe", StringComparison.OrdinalIgnoreCase)
            || disk.Model.Contains("SSD", StringComparison.OrdinalIgnoreCase)
            || disk.Model.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (disk.Model.Contains("HDD", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return QueryNoSeekPenalty($@"\\.\PhysicalDrive{disk.Number}");
    }

    private static bool QueryNoSeekPenalty(string devicePath)
    {
        const uint ioctlStorageQueryProperty = 0x002D1400;
        const byte storageDeviceSeekPenaltyProperty = 7;

        try
        {
            using var handle = CreateFile(
                devicePath,
                0,
                FileShare.ReadWrite,
                nint.Zero,
                FileMode.Open,
                0,
                nint.Zero);
            if (handle.IsInvalid)
            {
                return false;
            }

            var query = new byte[12];
            query[0] = storageDeviceSeekPenaltyProperty;
            var output = new byte[12];
            if (DeviceIoControl(handle, ioctlStorageQueryProperty, query, query.Length, output, output.Length, out _, nint.Zero))
            {
                return output[8] == 0;
            }
        }
        catch
        {
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TpmDeviceInfo
    {
        public uint StructVersion;
        public uint TpmVersion;
        public uint TpmInterfaceType;
        public uint TpmImpRevision;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, nint buffer, ref uint returnedLength);

    [DllImport("tbs.dll")]
    private static extern uint Tbsi_GetDeviceInfo(uint size, out TpmDeviceInfo info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        nint securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        byte[] inBuffer,
        int inBufferSize,
        byte[] outBuffer,
        int outBufferSize,
        out uint bytesReturned,
        nint overlapped);
}
