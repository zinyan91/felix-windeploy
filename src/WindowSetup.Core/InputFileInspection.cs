using System.Xml;
using System.Xml.Linq;

namespace Felix.WindowSetup.Core;

public sealed record InputFileInspection(
    bool ImageExists,
    bool AnswerFileExists,
    bool AnswerFileIsValid,
    string? AnswerFileError,
    IReadOnlyList<int>? SwmMissingParts = null,
    bool WindowsDriversFolderExists = true,
    bool WinPeDriversFolderExists = true,
    bool ExtraWinPeExists = true,
    bool ExtraWinPeOnTarget = false,
    bool ExtraWinPeIsRemovable = false,
    bool ExtraWinPeIsBitLocker = false,
    bool FirstDesktopSourceExists = true,
    bool FirstDesktopZipProgramExists = true);

public static class WindowSetupInputFiles
{
    public static InputFileInspection Inspect(
        string imagePath,
        string? answerFilePath,
        string? windowsDriversFolder = null,
        string? winPeDriversFolder = null,
        string? extraWinPePath = null,
        FirstDesktopAppOptions? firstDesktopApp = null)
    {
        var kind = SourceImages.KindOf(imagePath);
        var swmMissing = Array.Empty<int>();
        var imageExists = File.Exists(imagePath);
        if (kind == SourceImageKind.Swm && File.Exists(imagePath))
        {
            var directory = Path.GetDirectoryName(imagePath);
            var set = SourceImages.ResolveSwm(
                imagePath,
                directory is null ? [imagePath] : Directory.EnumerateFiles(directory, "*.swm"));
            swmMissing = set.MissingParts.ToArray();
            imageExists = set.IsComplete;
        }

        var windowsDriversExists = string.IsNullOrWhiteSpace(windowsDriversFolder)
            || Directory.Exists(windowsDriversFolder);
        var winPeDriversExists = string.IsNullOrWhiteSpace(winPeDriversFolder)
            || Directory.Exists(winPeDriversFolder);
        var extraWinPeExists = string.IsNullOrWhiteSpace(extraWinPePath)
            || File.Exists(extraWinPePath);
        var firstDesktopSourceExists = true;
        var firstDesktopZipProgramExists = true;
        if (firstDesktopApp is { Mode: not FirstDesktopAppMode.None })
        {
            var src = firstDesktopApp.SourcePath;
            firstDesktopSourceExists = !string.IsNullOrWhiteSpace(src) && File.Exists(src);
            if (firstDesktopApp.Mode == FirstDesktopAppMode.ZipArchive && firstDesktopSourceExists)
            {
                var exes = ZipProgramInspector.ListExecutables(src!);
                firstDesktopZipProgramExists = !string.IsNullOrWhiteSpace(firstDesktopApp.ProgramInsideZip)
                    && exes.Contains(firstDesktopApp.ProgramInsideZip, StringComparer.OrdinalIgnoreCase);
            }
        }

        if (string.IsNullOrWhiteSpace(answerFilePath))
        {
            return new InputFileInspection(
                imageExists,
                true,
                true,
                null,
                swmMissing,
                windowsDriversExists,
                winPeDriversExists,
                extraWinPeExists,
                false,
                false,
                false,
                firstDesktopSourceExists,
                firstDesktopZipProgramExists);
        }

        if (!File.Exists(answerFilePath))
        {
            return new InputFileInspection(
                imageExists,
                false,
                false,
                "The selected answer file does not exist.",
                swmMissing,
                windowsDriversExists,
                winPeDriversExists,
                extraWinPeExists,
                false,
                false,
                false,
                firstDesktopSourceExists,
                firstDesktopZipProgramExists);
        }

        try
        {
            using var stream = File.OpenRead(answerFilePath);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            var valid = string.Equals(document.Root?.Name.LocalName, "unattend", StringComparison.OrdinalIgnoreCase);
            return valid
                ? new InputFileInspection(
                    imageExists,
                    true,
                    true,
                    null,
                    swmMissing,
                    windowsDriversExists,
                    winPeDriversExists,
                    extraWinPeExists,
                    false,
                    false,
                    false,
                    firstDesktopSourceExists,
                    firstDesktopZipProgramExists)
                : new InputFileInspection(
                    imageExists,
                    true,
                    false,
                    "The root element must be unattend.",
                    swmMissing,
                    windowsDriversExists,
                    winPeDriversExists,
                    extraWinPeExists,
                    false,
                    false,
                    false,
                    firstDesktopSourceExists,
                    firstDesktopZipProgramExists);
        }
        catch (XmlException exception)
        {
            return new InputFileInspection(
                imageExists,
                true,
                false,
                exception.Message,
                swmMissing,
                windowsDriversExists,
                winPeDriversExists,
                extraWinPeExists,
                false,
                false,
                false,
                firstDesktopSourceExists,
                firstDesktopZipProgramExists);
        }
    }
}
