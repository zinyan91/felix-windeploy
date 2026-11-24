using System.Collections.ObjectModel;
using Felix.WindowSetup.Core;
using Felix.WindowSetup.Services;

namespace Felix.WindowSetup.ViewModels;

public sealed record SourceCandidateRow(
    SourceCandidate Candidate,
    int? EditionCount,
    string SummaryText = "")
{
    public string FileName => string.IsNullOrWhiteSpace(Path.GetFileName(Candidate.Path))
        ? Candidate.Path
        : Path.GetFileName(Candidate.Path);

    public string DirectoryPath => Path.GetDirectoryName(Candidate.Path) ?? Candidate.Path;

    public string KindLabel => Candidate.Kind.ToString().ToUpperInvariant();
}

public sealed class FindSourceViewModel(
    ISourceScanner scanner,
    IImageInspector inspector) : ObservableObject
{
    private CancellationTokenSource? cancellation;
    private bool isScanning;
    private string? errorMessage;
    private SourceCandidateRow? selected;

    public ObservableCollection<SourceCandidateRow> Results { get; } = [];

    public bool IsScanning
    {
        get => isScanning;
        private set
        {
            if (SetField(ref isScanning, value))
            {
                Raise(nameof(IsEmpty));
            }
        }
    }

    public string? ErrorMessage
    {
        get => errorMessage;
        private set
        {
            if (SetField(ref errorMessage, value))
            {
                Raise(nameof(HasError));
                Raise(nameof(IsEmpty));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsEmpty => !IsScanning && !HasError && Results.Count == 0;

    public SourceCandidateRow? Selected
    {
        get => selected;
        set
        {
            if (SetField(ref selected, value))
            {
                Raise(nameof(CanUse));
            }
        }
    }

    public bool CanUse => Selected is not null;

    public async Task StartAsync()
    {
        Cancel();
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        Results.Clear();
        Selected = null;
        ErrorMessage = null;
        IsScanning = true;

        try
        {
            await foreach (var candidate in scanner.ScanAsync(token))
            {
                int? count = null;
                var summary = string.Empty;
                try
                {
                    var inspection = await inspector.InspectAsync(candidate.Path, token);
                    var osEditions = inspection.Editions.Where(e => !e.IsWindowsPe).ToList();
                    count = osEditions.Count;
                    if (osEditions.Count > 0)
                    {
                        var first = osEditions[0];
                        var buildStr = first.Version?.Split('.').LastOrDefault();
                        var verText = int.TryParse(buildStr, out var buildNum)
                            ? $"{SourceImages.ReleaseName(buildNum) ?? buildStr} ({buildNum}{(first.ServicePackBuild is int sp ? $".{sp}" : string.Empty)})"
                            : first.Version;
                        summary = string.Join(" · ", new[] { first.Name, first.Architecture, verText }
                            .Where(s => !string.IsNullOrWhiteSpace(s)));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                }

                if (count is > 0)
                {
                    Results.Add(new SourceCandidateRow(candidate, count, summary));
                    Selected ??= Results[0];
                    Raise(nameof(IsEmpty));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsScanning = false;
            cancellation.Dispose();
            cancellation = null;
            Raise(nameof(IsEmpty));
        }
    }

    public void Cancel() => cancellation?.Cancel();
}
