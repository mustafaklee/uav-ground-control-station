using Avalonia.Controls;

namespace Gcs.Desktop.Views;

public sealed partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        Opened += (_, _) => UsernameBox.Focus();
    }
}
