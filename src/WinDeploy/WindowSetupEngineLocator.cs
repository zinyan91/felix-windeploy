namespace Felix.WindowSetup;

public static class WindowSetupEngineLocator
{
    private const string ExecutableName = "wimlib-imagex.exe";
    private const string LibraryName = "libwim-15.dll";

    public static string Find()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "Tools", "wimlib", "x64", ExecutableName),
            Path.Combine(baseDirectory, ExecutableName),
            Path.Combine(@"X:\Apps\WinDeploy\Tools\wimlib\x64", ExecutableName),
            Path.Combine(@"Y:\Apps\WinDeploy\Tools\wimlib\x64", ExecutableName)
        };
        return candidates.FirstOrDefault(IsCompleteEngine)
            ?? throw new FileNotFoundException(
                $"{ExecutableName} and {LibraryName} were not found under Tools\\wimlib\\x64 beside Felix WinDeploy.");
    }

    private static bool IsCompleteEngine(string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(executablePath);
        return !string.IsNullOrEmpty(directory) && File.Exists(Path.Combine(directory, LibraryName));
    }
}
