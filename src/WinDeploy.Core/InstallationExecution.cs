using System.Text.Json;

namespace Felix.WindowSetup.Core;

public enum InstallRunStage
{
    Preflight,
    VerifySource,
    StageDrivers,
    PrepareTarget,
    ApplyWindows,
    InjectDrivers,
    ConfigureBoot,
    ConfigureRecovery,
    VerifyInstallation,
    Completed,
    Failed
}

public sealed record StagedDriverArtifacts(
    string? BackedUpDriversDirectory,
    string? WifiProfilePath,
    string? PrinterBackupPath,
    int StagedDriverCount);

public enum InstallStageState
{
    Pending,
    Running,
    Completed,
    Failed
}

public sealed record InstallProgressUpdate(
    InstallRunStage Stage,
    InstallStageState State,
    string Message,
    int Percent);

public sealed record PreparedInstallation(
    WindowSetupRequest Request,
    string RunDirectory,
    IReadOnlyList<string> TemporaryAccessPaths,
    IReadOnlyList<string> TemporaryFiles);

public sealed record EngineExecutionResult(
    int ExitCode,
    string Output);

public sealed record InstallationVerificationResult(
    bool WindowsFilesPresent,
    bool LoaderPresent,
    bool BootStorePresent,
    IReadOnlyList<string> MissingFiles,
    IReadOnlyList<string>? MissingBootEntries = null)
{
    public IReadOnlyList<string> MissingBootEntryIdentifiers => MissingBootEntries ?? [];
    public bool Succeeded => WindowsFilesPresent && LoaderPresent && BootStorePresent &&
        MissingFiles.Count == 0 && MissingBootEntryIdentifiers.Count == 0;
}

public sealed record InstallationOutcome(
    bool Succeeded,
    bool WriteStarted,
    string RunDirectory,
    string? Error,
    InstallationVerificationResult? Verification,
    string? ExportedLogDirectory = null);

public interface IInstallationPlatform
{
    Task<EngineExecutionResult> VerifySourceAsync(
        WindowSetupRequest request,
        string runDirectory,
        IProgress<InstallProgressUpdate>? progress,
        CancellationToken cancellationToken) =>
        Task.FromResult(new EngineExecutionResult(0, ""));

    Task<StagedDriverArtifacts?> StageDriversAndNetworkAsync(
        WindowSetupRequest request,
        string runDirectory,
        IProgress<InstallProgressUpdate>? progress,
        CancellationToken cancellationToken) =>
        Task.FromResult<StagedDriverArtifacts?>(null);

    Task<PreparedInstallation> PrepareTargetAsync(
        WindowSetupRequest request,
        string runDirectory,
        IProgress<InstallProgressUpdate>? progress,
        CancellationToken cancellationToken);

    Task<EngineExecutionResult> ApplyWindowsAsync(
        PreparedInstallation installation,
        IProgress<InstallProgressUpdate>? progress,
        CancellationToken cancellationToken);

    Task InjectDriversAndNetworkAsync(
        PreparedInstallation installation,
        StagedDriverArtifacts? staged,
        IProgress<InstallProgressUpdate>? progress,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task ConfigureBootAsync(
        PreparedInstallation installation,
        IProgress<InstallProgressUpdate>? progress,
        CancellationToken cancellationToken);

    Task ConfigureRecoveryAsync(
        PreparedInstallation installation,
        IProgress<InstallProgressUpdate>? progress,
        CancellationToken cancellationToken);

    Task<InstallationVerificationResult> VerifyAsync(
        PreparedInstallation installation,
        IProgress<InstallProgressUpdate>? progress,
        CancellationToken cancellationToken);

    Task CleanupAsync(
        PreparedInstallation installation,
        bool succeeded);

    Task<IReadOnlyList<BcdEntry>> ReadBootEntriesAsync(
        PreparedInstallation installation,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BcdEntry>>([]);

    Task<string?> ExportRunLogAsync(
        PreparedInstallation installation,
        string runDirectory) =>
        Task.FromResult<string?>(null);
}

public enum SafeStopDecision
{
    Continue,
    AbortInstall
}

public sealed class InstallExecutionControl
{
    private volatile bool safeStopRequested;

    public bool IsSafeStopRequested => safeStopRequested;
    public void RequestSafeStop() => safeStopRequested = true;
    public void ClearSafeStop() => safeStopRequested = false;

    public Func<InstallRunStage, bool, Task<SafeStopDecision>>? OnSafeStopBoundaryAsync { get; init; }
}

public sealed class WindowInstallationRunner(IInstallationPlatform platform)
{
    public async Task<InstallationOutcome> RunAsync(
        WindowSetupRequest request,
        string journalRoot,
        IProgress<InstallProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default,
        InstallExecutionControl? control = null)
    {
        var runDirectory = Path.Combine(journalRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        var journal = new InstallJournal(runDirectory, request);
        PreparedInstallation? prepared = null;
        var writeStarted = false;
        IReadOnlyList<BcdEntry> beforeBootEntries = [];
        string? exportedLogDirectory = null;

        async Task CheckSafeStopAsync(InstallRunStage nextStage)
        {
            if (control is { IsSafeStopRequested: true, OnSafeStopBoundaryAsync: { } handler })
            {
                var decision = await handler(nextStage, writeStarted);
                if (decision == SafeStopDecision.AbortInstall)
                {
                    throw new OperationCanceledException("Safe stop requested by user.");
                }
                control.ClearSafeStop();
            }
        }

        try
        {
            Report(InstallRunStage.Preflight, InstallStageState.Running, "Checking source and target", 3);
            var preflight = WindowSetupPreflight.Validate(request);
            if (!preflight.CanProceed)
            {
                throw new InvalidOperationException(string.Join(
                    Environment.NewLine,
                    preflight.Issues.Where(issue => issue.IsBlocking).Select(issue => issue.Message)));
            }
            await journal.UpdateAsync(InstallRunStage.Preflight, InstallStageState.Completed, false, null);
            Report(InstallRunStage.Preflight, InstallStageState.Completed, "Preflight passed", 8);
            cancellationToken.ThrowIfCancellationRequested();

            if ((request.Options ?? new()).VerifyImage)
            {
                await CheckSafeStopAsync(InstallRunStage.VerifySource);
                await journal.UpdateAsync(InstallRunStage.VerifySource, InstallStageState.Running, false, null);
                Report(InstallRunStage.VerifySource, InstallStageState.Running, "Verifying Windows image", 8);
                var verify = await platform.VerifySourceAsync(request, runDirectory, progress, cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(runDirectory, "verify.log"), verify.Output, CancellationToken.None);
                if (verify.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Image verification failed (wimlib exit code {verify.ExitCode}).");
                }
                await journal.UpdateAsync(InstallRunStage.VerifySource, InstallStageState.Completed, false, null);
                Report(InstallRunStage.VerifySource, InstallStageState.Completed, "Windows image verified", 18);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var driverOptions = request.Options?.Drivers;
            var firstBootOptions = request.Options?.FirstBoot;
            StagedDriverArtifacts? stagedDrivers = null;
            if (driverOptions?.HasBackupOrStagingWork == true || firstBootOptions?.HasPreFormatStagingWork == true)
            {
                await CheckSafeStopAsync(InstallRunStage.StageDrivers);
                await journal.UpdateAsync(InstallRunStage.StageDrivers, InstallStageState.Running, false, null);
                Report(InstallRunStage.StageDrivers, InstallStageState.Running, "Staging drivers, network, and First Boot payloads", 19);
                stagedDrivers = await platform.StageDriversAndNetworkAsync(request, runDirectory, progress, cancellationToken);
                await journal.UpdateAsync(InstallRunStage.StageDrivers, InstallStageState.Completed, false, null);
                Report(InstallRunStage.StageDrivers, InstallStageState.Completed, "Pre-format staging completed", 20);
                cancellationToken.ThrowIfCancellationRequested();
            }

            await CheckSafeStopAsync(InstallRunStage.PrepareTarget);
            writeStarted = true;
            await journal.UpdateAsync(InstallRunStage.PrepareTarget, InstallStageState.Running, true, null);
            Report(InstallRunStage.PrepareTarget, InstallStageState.Running, "Preparing target partitions", 20);
            prepared = await platform.PrepareTargetAsync(request, runDirectory, progress, CancellationToken.None);
            journal.UseRequest(prepared.Request);
            if (request.Target.Mode == InstallTargetMode.ExistingPartition)
            {
                beforeBootEntries = await platform.ReadBootEntriesAsync(prepared, CancellationToken.None);
            }
            await journal.UpdateAsync(InstallRunStage.PrepareTarget, InstallStageState.Completed, true, null);
            Report(InstallRunStage.PrepareTarget, InstallStageState.Completed, "Target is ready", 26);

            await CheckSafeStopAsync(InstallRunStage.ApplyWindows);
            await journal.UpdateAsync(InstallRunStage.ApplyWindows, InstallStageState.Running, true, null);
            Report(InstallRunStage.ApplyWindows, InstallStageState.Running, "Applying Windows image", 28);
            var engine = await platform.ApplyWindowsAsync(prepared, progress, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(runDirectory, "engine.log"), engine.Output, CancellationToken.None);
            if (engine.ExitCode != 0)
            {
                throw new InvalidOperationException($"wimlib-imagex exited with code {engine.ExitCode}.");
            }
            await journal.UpdateAsync(InstallRunStage.ApplyWindows, InstallStageState.Completed, true, null);
            Report(InstallRunStage.ApplyWindows, InstallStageState.Completed, "Windows image applied", 72);

            if (driverOptions?.HasInjectionOrFirstBootWork == true
                || stagedDrivers is not null
                || firstBootOptions?.HasOfflineWork == true
                || request.Options?.Bloatware?.HasWork == true
                || WindowsBackupMetadata.IsInPlaceBackupEdition(request.Source.Edition))
            {
                await CheckSafeStopAsync(InstallRunStage.InjectDrivers);
                await journal.UpdateAsync(InstallRunStage.InjectDrivers, InstallStageState.Running, true, null);
                Report(InstallRunStage.InjectDrivers, InstallStageState.Running, "Injecting drivers and staging First Boot payloads", 73);
                await platform.InjectDriversAndNetworkAsync(prepared, stagedDrivers, progress, CancellationToken.None);
                await journal.UpdateAsync(InstallRunStage.InjectDrivers, InstallStageState.Completed, true, null);
                Report(InstallRunStage.InjectDrivers, InstallStageState.Completed, "Drivers and First Boot payloads staged", 75);
            }

            await CheckSafeStopAsync(InstallRunStage.ConfigureBoot);
            await journal.UpdateAsync(InstallRunStage.ConfigureBoot, InstallStageState.Running, true, null);
            Report(InstallRunStage.ConfigureBoot, InstallStageState.Running, "Configuring boot files", 76);
            await platform.ConfigureBootAsync(prepared, progress, CancellationToken.None);
            await journal.UpdateAsync(InstallRunStage.ConfigureBoot, InstallStageState.Completed, true, null);
            Report(InstallRunStage.ConfigureBoot, InstallStageState.Completed, "Boot files configured", 84);

            var shouldConfigureRecovery = (prepared.Request.Options ?? new()).CreateRecovery
                && (prepared.Request.Target.RecoveryPartition.Identity != "none"
                    || (prepared.Request.Target.Mode == InstallTargetMode.ExistingPartition
                        && prepared.Request.Options?.BootAndRecovery?.CreateNewRecovery == true));
            if (shouldConfigureRecovery)
            {
                await journal.UpdateAsync(InstallRunStage.ConfigureRecovery, InstallStageState.Running, true, null);
                Report(InstallRunStage.ConfigureRecovery, InstallStageState.Running, "Configuring Windows Recovery", 86);
                await platform.ConfigureRecoveryAsync(prepared, progress, CancellationToken.None);
                await journal.UpdateAsync(InstallRunStage.ConfigureRecovery, InstallStageState.Completed, true, null);
                Report(InstallRunStage.ConfigureRecovery, InstallStageState.Completed, "Windows Recovery configured", 91);
            }

            await journal.UpdateAsync(InstallRunStage.VerifyInstallation, InstallStageState.Running, true, null);
            Report(InstallRunStage.VerifyInstallation, InstallStageState.Running, "Verifying Windows and boot files", 93);
            var verification = await platform.VerifyAsync(prepared, progress, CancellationToken.None);
            if (beforeBootEntries.Count > 0)
            {
                var afterBootEntries = await platform.ReadBootEntriesAsync(prepared, CancellationToken.None);
                var device = $"partition={prepared.Request.Target.WindowsPartition.AccessPath}";
                var missing = BootMenu.Missing(beforeBootEntries, afterBootEntries, device);
                if (missing.Count > 0)
                {
                    verification = verification with
                    {
                        MissingBootEntries = missing.Select(entry => entry.Identifier).ToArray()
                    };
                }
            }
            await File.WriteAllTextAsync(
                Path.Combine(runDirectory, "verification.json"),
                JsonSerializer.Serialize(verification, InstallJournal.JsonOptions),
                CancellationToken.None);
            if (!verification.Succeeded)
            {
                throw new InvalidOperationException(
                    "Installation verification failed: " +
                    string.Join(", ", verification.MissingFiles.Concat(verification.MissingBootEntryIdentifiers)));
            }
            await journal.UpdateAsync(InstallRunStage.Completed, InstallStageState.Completed, true, null);
            Report(InstallRunStage.Completed, InstallStageState.Completed, "Windows installation completed", 100);
            exportedLogDirectory = await platform.ExportRunLogAsync(prepared, runDirectory);
            await platform.CleanupAsync(prepared, true);
            return new(true, true, runDirectory, null, verification, exportedLogDirectory);
        }
        catch (Exception exception)
        {
            try
            {
                await journal.UpdateAsync(InstallRunStage.Failed, InstallStageState.Failed, writeStarted, exception.Message);
            }
            catch (Exception journalError)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(runDirectory, "journal-error.txt"),
                    journalError.ToString(),
                    CancellationToken.None);
            }
            Report(InstallRunStage.Failed, InstallStageState.Failed, exception.Message, 100);
            if (prepared is not null)
            {
                try
                {
                    exportedLogDirectory = await platform.ExportRunLogAsync(prepared, runDirectory);
                }
                catch
                {
                }
                try
                {
                    await platform.CleanupAsync(prepared, false);
                }
                catch (Exception cleanupError)
                {
                    await File.WriteAllTextAsync(
                        Path.Combine(runDirectory, "cleanup-error.txt"),
                        cleanupError.ToString(),
                        CancellationToken.None);
                }
            }
            return new(false, writeStarted, runDirectory, exception.Message, null, exportedLogDirectory);
        }

        void Report(InstallRunStage stage, InstallStageState state, string message, int percent) =>
            progress?.Report(new(stage, state, message, percent));
    }
}

public sealed class InstallJournal
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true
    };

    private readonly string path;
    private WindowSetupRequest request;

    public InstallJournal(string runDirectory, WindowSetupRequest request)
    {
        path = Path.Combine(runDirectory, "journal.json");
        this.request = request;
    }

    public void UseRequest(WindowSetupRequest request) => this.request = request;

    public async Task UpdateAsync(
        InstallRunStage stage,
        InstallStageState state,
        bool writeStarted,
        string? error)
    {
        var document = new
        {
            UpdatedUtc = DateTime.UtcNow,
            Stage = stage.ToString(),
            State = state.ToString(),
            WriteStarted = writeStarted,
            Error = error,
            Source = new
            {
                request.Source.ImagePath,
                request.Source.ImageIndex,
                request.Source.Edition,
                request.Source.Architecture,
                request.Source.Version
            },
            Target = new
            {
                request.Target.Mode,
                request.Target.Firmware,
                request.Target.DiskNumber,
                request.Target.DiskId,
                request.Target.DiskSerial,
                request.Target.DiskSize,
                WindowsPartitionIdentity = request.Target.WindowsPartition.Identity,
                SystemPartitionIdentity = request.Target.EfiSystemPartition.Identity,
                RecoveryPartitionIdentity = request.Target.RecoveryPartition.Identity
            },
            Unattended = !string.IsNullOrWhiteSpace(request.AutounattendPath)
        };
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, JsonOptions), CancellationToken.None);
        File.Move(temporary, path, true);
    }
}
