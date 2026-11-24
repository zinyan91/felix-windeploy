using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.Services;

public interface IImageInspector
{
    Task<WindowsImageInspection> InspectAsync(string path, CancellationToken cancellationToken = default);
}
