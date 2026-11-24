using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.Services;

public interface ISourceScanner
{
    IAsyncEnumerable<SourceCandidate> ScanAsync(CancellationToken cancellationToken = default);
}

public sealed class NativeSourceScanner(ITargetSource targetSource) : ISourceScanner
{
    public async IAsyncEnumerable<SourceCandidate> ScanAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var roots = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady && !drive.Name.Equals(@"X:\", StringComparison.OrdinalIgnoreCase))
            .Select(drive => drive.RootDirectory.FullName)
            .ToArray();

        string[] peRoots;
        try
        {
            peRoots = (await targetSource.LoadAsync(cancellationToken))
                .Where(disk => disk.HoldsPeMedia)
                .SelectMany(disk => disk.Partitions)
                .Where(partition => !string.IsNullOrWhiteSpace(partition.Letter))
                .Select(partition => partition.Letter.TrimEnd(':', '\\') + @":\")
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            peRoots = [];
        }

        var channel = Channel.CreateUnbounded<SourceCandidate>(new UnboundedChannelOptions { SingleWriter = true });
        var producer = Task.Run(async () =>
        {
            try
            {
                await foreach (var candidate in SourceSearch.ScanAsync(roots, peRoots, cancellationToken))
                {
                    await channel.Writer.WriteAsync(candidate, cancellationToken);
                }
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return item;
        }

        await producer;
    }
}
