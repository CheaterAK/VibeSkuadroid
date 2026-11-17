using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.App.Droid.Core.Engine;
using Skua.App.Droid.Core.Hosting;
using System.Collections.ObjectModel;

namespace Skua.App.Droid.Core.ViewModels;

/// <summary>
/// Login form. The remembered password is never loaded into this ViewModel or the view: when one is stored the
/// password box stays empty and the host reads the vault at login time.
/// </summary>
public sealed partial class LoginViewModel : ObservableObject
{
    /// <summary>Picker entry meaning "let the engine choose".</summary>
    public static readonly ServerChoice AutomaticServer = new("Automatic (least full)", 0, 0, true, false, 0, null);

    private readonly AppServices _services;
    private readonly EngineHost _host;
    private CancellationTokenSource? _loginCts;

    public LoginViewModel(AppServices services)
    {
        _services = services;
        _host = services.Host;
        _account = _host.Settings.Current.AccountName ?? string.Empty;
        _rememberMe = _host.Settings.Current.RememberPassword;
        Servers.Add(AutomaticServer);
        if (_host.Settings.Current.Server is string saved)
            Servers.Add(new ServerChoice(saved, 0, 0, true, false, 0, null));
        _selectedServer = Servers[^1];
        RefreshStoredPassword();
    }

    public ObservableCollection<ServerChoice> Servers { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string _account;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string _password = string.Empty;

    [ObservableProperty]
    private ServerChoice? _selectedServer;

    [ObservableProperty]
    private bool _rememberMe;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private bool _isWorking;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private bool _hasStoredPassword;

    [ObservableProperty]
    private bool _loadingServers;

    public bool CanRemember => _host.Vault.IsAvailable;

    public string PasswordHint => HasStoredPassword ? "Saved password (leave empty to use it)" : "Password";

    partial void OnAccountChanged(string value) => RefreshStoredPassword();

    partial void OnHasStoredPasswordChanged(bool value) => OnPropertyChanged(nameof(PasswordHint));

    private void RefreshStoredPassword() => HasStoredPassword = _host.HasRememberedPassword(Account);

    private bool CanLogin() => !IsWorking && !string.IsNullOrWhiteSpace(Account) && (Password.Length > 0 || HasStoredPassword);

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        ErrorText = null;
        IsWorking = true;
        _loginCts = new CancellationTokenSource();
        string password = Password;
        try
        {
            string? server = SelectedServer is null || ReferenceEquals(SelectedServer, AutomaticServer) ? null : SelectedServer.Name;
            await _host.LoginAsync(Account.Trim(), password, server, RememberMe, _loginCts.Token);
            Password = string.Empty;
        }
        catch (EngineException e)
        {
            ErrorText = e.Message;
        }
        catch (OperationCanceledException)
        {
            ErrorText = "Login cancelled.";
        }
        finally
        {
            _loginCts.Dispose();
            _loginCts = null;
            IsWorking = false;
            RefreshStoredPassword();
        }
    }

    [RelayCommand]
    private void CancelLogin() => _loginCts?.Cancel();

    [RelayCommand]
    private async Task LoadServersAsync()
    {
        if (LoadingServers)
            return;
        LoadingServers = true;
        ErrorText = null;
        try
        {
            IReadOnlyList<ServerChoice> list = await _host.Engine.GetServersAsync();
            string? keep = SelectedServer?.Name;
            Servers.Clear();
            Servers.Add(AutomaticServer);
            foreach (ServerChoice s in list)
                Servers.Add(s);
            SelectedServer = Servers.FirstOrDefault(s => s.Name == keep) ?? AutomaticServer;
            StatusText = $"{list.Count} servers, {list.Count(s => s.Online)} online.";
        }
        catch (EngineException e)
        {
            ErrorText = e.Message;
        }
        finally
        {
            LoadingServers = false;
        }
    }

    [RelayCommand]
    private void ForgetPassword()
    {
        _host.ForgetPassword();
        RememberMe = false;
        RefreshStoredPassword();
        LoginCommand.NotifyCanExecuteChanged();
    }

    internal void Apply(EngineSnapshot s)
    {
        StatusText = s.Connection switch
        {
            ConnectionPhase.LoggingIn or ConnectionPhase.Connecting => s.StatusText,
            ConnectionPhase.Failed => null,
            _ => StatusText,
        };
        if (s.Connection == ConnectionPhase.Failed && !IsWorking && s.StatusText is not null)
            ErrorText = s.StatusText;
    }
}
