using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gcs.Desktop.Services;

namespace Gcs.Desktop.ViewModels;

/// <summary>
/// The sign-in screen. Empty fields are reported next to the field instead of greying out the button, so the operator
/// sees why nothing happens. The server's own message ("locked for a few minutes", "too many requests") is shown as is.
/// </summary>
public sealed partial class LoginViewModel(IAuthSession session, Uri apiBaseUrl) : ObservableObject
{
    /// <summary>Raised after a successful sign-in; the app then opens the main window.</summary>
    public event EventHandler? SignedIn;

    public string ServerAddress { get; } = apiBaseUrl.ToString();

    /// <summary>The version shown on the sign-in screen, from the assembly (e.g. "1.0.0").</summary>
    public string Version { get; } = typeof(LoginViewModel).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "dev";

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string? _usernameError;

    [ObservableProperty]
    private string? _passwordError;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Show the password in clear text while the operator checks what they typed.</summary>
    [ObservableProperty]
    private bool _isPasswordVisible;

    partial void OnUsernameChanged(string value) => UsernameError = null;

    partial void OnPasswordChanged(string value)
    {
        if (value.Length > 0)
        {
            PasswordError = null;
        }
    }

    [RelayCommand]
    private async Task SignInAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = null;
        UsernameError = Username.Trim().Length == 0 ? "Enter your username." : null;
        PasswordError = Password.Length == 0 ? "Enter your password." : null;
        if (UsernameError is not null || PasswordError is not null)
        {
            return;
        }

        try
        {
            await session.LoginAsync(Username.Trim(), Password, cancellationToken);
            Password = string.Empty; // do not keep the password in memory longer than needed
            IsPasswordVisible = false;
            SignedIn?.Invoke(this, EventArgs.Empty);
        }
        catch (ApiProblemException ex)
        {
            ErrorMessage = ex.Message;
            Password = string.Empty;
            PasswordError = null; // the server's message says what went wrong; "enter your password" would be noise
        }
        catch (HttpRequestException ex)
        {
            ErrorMessage = $"Cannot reach the backend at {ServerAddress}: {ex.Message}";
        }
    }
}
