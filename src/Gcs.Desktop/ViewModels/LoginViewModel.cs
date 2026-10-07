using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gcs.Desktop.Services;

namespace Gcs.Desktop.ViewModels;

/// <summary>The sign-in screen. Shows the server's own message ("locked for a few minutes", "too many requests").</summary>
public sealed partial class LoginViewModel(IAuthSession session, Uri apiBaseUrl) : ObservableObject
{
    /// <summary>Raised after a successful sign-in; the app then opens the main window.</summary>
    public event EventHandler? SignedIn;

    public string ServerAddress { get; } = apiBaseUrl.ToString();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _username = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _password = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    private bool CanSignIn() => Username.Trim().Length > 0 && Password.Length > 0;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = null;
        try
        {
            await session.LoginAsync(Username.Trim(), Password, cancellationToken);
            Password = string.Empty; // do not keep the password in memory longer than needed
            SignedIn?.Invoke(this, EventArgs.Empty);
        }
        catch (ApiProblemException ex)
        {
            ErrorMessage = ex.Message;
            Password = string.Empty;
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Cannot reach the backend at {ServerAddress}: {ex.Message}";
        }
    }
}
