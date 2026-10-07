using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Gcs.Desktop.Services;

/// <summary>Asks the operator a yes/no question. An interface so view models can be tested with a scripted answer.</summary>
public interface IConfirmationService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
}

/// <summary>
/// A small modal dialog. The confirm button is not the default button: pressing Enter by reflex must not arm a vehicle,
/// the operator has to click it (Escape cancels).
/// </summary>
public sealed class DialogConfirmationService(Func<Window?> owner) : IConfirmationService
{
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var confirm = new Button { Content = confirmText, Background = new SolidColorBrush(Color.Parse("#C0392B")), Foreground = Brushes.White };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#16212C")),
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, FontSize = 14 },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, confirm },
                    },
                },
            },
        };
        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);

        return owner() is { } window && await dialog.ShowDialog<bool?>(window) == true;
    }
}
