using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.Services;

public interface ITargetSource
{
    Task<IReadOnlyList<TargetDiskFacts>> LoadAsync(CancellationToken cancellationToken = default);
}
