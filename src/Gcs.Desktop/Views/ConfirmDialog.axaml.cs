using Avalonia.Controls;

namespace Gcs.Desktop.Views;

/// <summary>A yes/no question with consequences; see <see cref="Services.DialogConfirmationService"/>.</summary>
public sealed partial class ConfirmDialog : Window
{
    /// <summary>For the XAML loader and the designer.</summary>
    public ConfirmDialog()
        : this("Confirm", string.Empty, "Confirm")
    {
    }

    public ConfirmDialog(string title, string message, string confirmText)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        ConfirmButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
        Opened += (_, _) => CancelButton.Focus();
    }
}
