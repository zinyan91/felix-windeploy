using System.Collections.ObjectModel;
using Felix.WindowSetup.Core;
using Felix.WindowSetup.Services;

namespace Felix.WindowSetup.ViewModels;

public sealed class SourceViewModel(IImageInspector inspector, TargetViewModel? target = null) : ObservableObject
{
    private string imagePath = "";
    private string editablePath = "";
    private WindowsImageEdition? edition;
    private bool isValid;
    private bool isInspecting;
    private string? error;

    public string ImagePath
    {
        get => imagePath;
        private set
        {
            if (SetField(ref imagePath, value))
            {
                EditablePath = value;
            }
        }
    }

    public string EditablePath
    {
        get => editablePath;
        set => SetField(ref editablePath, value);
    }

    public async Task CommitEditablePathAsync(CancellationToken cancellationToken = default)
    {
        var candidate = NormalizePathInput(EditablePath);
        if (string.Equals(candidate, ImagePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        await SelectImageAsync(candidate, cancellationToken);
    }

    public async Task<bool> PasteClipboardTextAsync(string? rawText, CancellationToken cancellationToken = default)
    {
        var candidate = NormalizePathInput(rawText);
        if (string.IsNullOrWhiteSpace(candidate) || (!File.Exists(candidate) && SourceImages.KindOf(candidate) is null))
        {
            return false;
        }

        await SelectImageAsync(candidate, cancellationToken);
        return true;
    }

    private static string NormalizePathInput(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        var firstLine = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return firstLine.Trim().Trim('"');
    }

    public ObservableCollection<WindowsImageEdition> Editions { get; } = [];

    public WindowsImageEdition? Edition
    {
        get => edition;
        set
        {
            if (SetField(ref edition, value))
            {
                Raise(nameof(IsValid));
            }
        }
    }

    public bool IsValid
    {
        get => isValid && Edition is not null;
        private set => SetField(ref isValid, value);
    }

    public bool IsInspecting
    {
        get => isInspecting;
        private set => SetField(ref isInspecting, value);
    }

    public string? Error
    {
        get => error;
        private set
        {
            if (SetField(ref error, value))
            {
                Raise(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public async Task SelectImageAsync(string path, CancellationToken cancellationToken = default)
    {
        ImagePath = path;
        Editions.Clear();
        Edition = null;
        IsValid = false;
        Error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IsInspecting = true;
        try
        {
            var inspection = await inspector.InspectAsync(path, cancellationToken);
            foreach (var item in inspection.Editions)
            {
                Editions.Add(item);
            }
            Edition = WindowsImageEditions.PickDefault(inspection.Editions);
            IsValid = Editions.Count > 0;
            target?.UpdateSetupFiles(SetupFileLetters.Of(path, AppContext.BaseDirectory, null));
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            IsValid = false;
        }
        finally
        {
            IsInspecting = false;
        }
    }
}
