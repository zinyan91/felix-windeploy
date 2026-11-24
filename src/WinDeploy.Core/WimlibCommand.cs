using System.Globalization;

namespace Felix.WindowSetup.Core;

public enum BackupArchiveFormat
{
    Wim,
    Esd,
    Swm
}

public sealed record WimlibCommand(
    string ExecutablePath,
    IReadOnlyList<string> Arguments);

public static class WimlibCommandBuilder
{
    public static WimlibCommand CreateVerify(string executablePath, string imagePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("Choose the wimlib-imagex executable.", nameof(executablePath));
        }
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            throw new ArgumentException("Choose a Windows image to verify.", nameof(imagePath));
        }

        var arguments = new List<string> { "verify", imagePath };
        if (SourceImages.KindOf(imagePath) == SourceImageKind.Swm)
        {
            arguments.Add($"--ref={SwmRefGlob(imagePath)}");
        }
        return new WimlibCommand(executablePath, arguments);
    }

    public static WimlibCommand CreateApply(
        string executablePath,
        string imagePath,
        int imageIndex,
        string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("Choose the wimlib-imagex executable.", nameof(executablePath));
        }
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            throw new ArgumentException("Choose a Windows image to apply.", nameof(imagePath));
        }
        if (imageIndex <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageIndex), "Choose a valid Windows image index.");
        }
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("Choose a target directory.", nameof(targetDirectory));
        }

        var arguments = new List<string>
        {
            "apply",
            imagePath,
            imageIndex.ToString(CultureInfo.InvariantCulture),
            targetDirectory
        };
        if (SourceImages.KindOf(imagePath) == SourceImageKind.Swm)
        {
            arguments.Add($"--ref={SwmRefGlob(imagePath)}");
        }
        return new WimlibCommand(executablePath, arguments);
    }

    public static WimlibCommand CreateCapture(
        string executablePath,
        string sourceDirectory,
        string destinationWimPath,
        string imageName,
        string? imageDescription = null,
        BackupArchiveFormat format = BackupArchiveFormat.Wim,
        bool snapshot = false,
        string? configFilePath = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("Choose the wimlib-imagex executable.", nameof(executablePath));
        }
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            throw new ArgumentException("Choose a source directory to capture.", nameof(sourceDirectory));
        }
        if (string.IsNullOrWhiteSpace(destinationWimPath))
        {
            throw new ArgumentException("Choose a destination image path.", nameof(destinationWimPath));
        }
        if (string.IsNullOrWhiteSpace(imageName))
        {
            throw new ArgumentException("Provide an image name.", nameof(imageName));
        }

        var arguments = new List<string>
        {
            "capture",
            sourceDirectory,
            destinationWimPath,
            imageName
        };
        if (!string.IsNullOrWhiteSpace(imageDescription))
        {
            arguments.Add(imageDescription);
        }

        if (format == BackupArchiveFormat.Esd)
        {
            arguments.Add("--compress=LZMS");
            arguments.Add("--solid");
        }
        else
        {
            arguments.Add("--compress=LZX");
        }

        if (snapshot)
        {
            arguments.Add("--snapshot");
        }

        if (!string.IsNullOrWhiteSpace(configFilePath))
        {
            arguments.Add($"--config={configFilePath}");
        }

        return new WimlibCommand(executablePath, arguments);
    }

    public static WimlibCommand CreateSplit(
        string executablePath,
        string sourceWimPath,
        string destinationSwmPath,
        int partSizeMiB = 3800)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("Choose the wimlib-imagex executable.", nameof(executablePath));
        }
        if (string.IsNullOrWhiteSpace(sourceWimPath))
        {
            throw new ArgumentException("Choose a source WIM file to split.", nameof(sourceWimPath));
        }
        if (string.IsNullOrWhiteSpace(destinationSwmPath))
        {
            throw new ArgumentException("Choose a destination SWM path.", nameof(destinationSwmPath));
        }
        if (partSizeMiB <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(partSizeMiB), "Split size must be positive.");
        }

        return new WimlibCommand(
            executablePath,
            [
                "split",
                sourceWimPath,
                destinationSwmPath,
                partSizeMiB.ToString(CultureInfo.InvariantCulture)
            ]);
    }

    public static string SwmRefGlob(string firstSwmPath)
    {
        var directory = Path.GetDirectoryName(firstSwmPath);
        var stem = Path.GetFileNameWithoutExtension(firstSwmPath);
        var pattern = stem + "*.swm";
        return string.IsNullOrEmpty(directory) ? pattern : Path.Combine(directory, pattern);
    }
}
