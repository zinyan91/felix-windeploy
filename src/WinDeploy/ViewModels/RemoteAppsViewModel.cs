using System.Collections.ObjectModel;
using Felix.WindowSetup.Core;

namespace Felix.WindowSetup.ViewModels;

public sealed class RemoteAppItemViewModel : ObservableObject
{
    private readonly Action onChanged;
    private bool isEnabled;
    private string password;

    public RemoteAppItemViewModel(
        RemoteSupportAppKind kind,
        bool isEnabled,
        string password,
        Action onChanged)
    {
        Kind = kind;
        this.isEnabled = isEnabled;
        this.password = password;
        this.onChanged = onChanged;
    }

    public RemoteSupportAppKind Kind { get; }
    public string DisplayName => RemoteSupportCatalog.DisplayName(Kind);
    public bool RequiresPassword => RemoteSupportCatalog.RequiresPassword(Kind);

    public bool IsEnabled
    {
        get => isEnabled;
        set
        {
            if (SetField(ref isEnabled, value))
            {
                if (isEnabled && RequiresPassword && string.IsNullOrWhiteSpace(password))
                {
                    password = RemotePasswordPolicy.Generate();
                    Raise(nameof(Password));
                }

                Raise(nameof(ShowPasswordEditor));
                Raise(nameof(ShowTeamViewerNote));
                Raise(nameof(ValidationError));
                onChanged();
            }
        }
    }

    public string Password
    {
        get => password;
        set
        {
            if (SetField(ref password, value))
            {
                Raise(nameof(ValidationError));
                onChanged();
            }
        }
    }

    public bool ShowPasswordEditor => IsEnabled && RequiresPassword;
    public bool ShowTeamViewerNote => IsEnabled && !RequiresPassword;

    public string? ValidationError
    {
        get
        {
            if (!IsEnabled || !RequiresPassword)
            {
                return null;
            }

            var (_, error) = RemotePasswordPolicy.Validate(Password);
            return error;
        }
    }

    public void RegeneratePassword()
    {
        if (RequiresPassword)
        {
            Password = RemotePasswordPolicy.Generate();
        }
    }
}

public sealed class RemoteAppsViewModel : ObservableObject
{
    private static readonly RemoteSupportAppKind[] AllKinds =
    [
        RemoteSupportAppKind.AnyDesk,
        RemoteSupportAppKind.RustDesk,
        RemoteSupportAppKind.TeamViewerPortable,
        RemoteSupportAppKind.UltraViewer
    ];

    public RemoteAppsViewModel(IReadOnlyList<RemoteAppSelection>? initial = null)
    {
        var map = (initial ?? []).ToDictionary(a => a.Kind);
        foreach (var kind in AllKinds)
        {
            map.TryGetValue(kind, out var existing);
            var enabled = existing?.Enabled ?? false;
            var password = existing?.Password ?? "";
            if (enabled && RemoteSupportCatalog.RequiresPassword(kind) && string.IsNullOrWhiteSpace(password))
            {
                password = RemotePasswordPolicy.Generate();
            }

            Items.Add(new RemoteAppItemViewModel(kind, enabled, password, OnItemChanged));
        }
    }

    public ObservableCollection<RemoteAppItemViewModel> Items { get; } = [];

    public bool CanApply => Items.All(item => item.ValidationError is null);

    public IReadOnlyList<RemoteAppSelection> BuildSelections() =>
        Items.Select(item => new RemoteAppSelection(
            item.Kind,
            item.IsEnabled,
            item.RequiresPassword ? item.Password : "")).ToList();

    private void OnItemChanged()
    {
        Raise(nameof(CanApply));
    }
}
