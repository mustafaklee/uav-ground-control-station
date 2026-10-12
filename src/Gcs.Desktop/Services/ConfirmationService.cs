using Avalonia.Controls;
using Gcs.Desktop.Views;

namespace Gcs.Desktop.Services;

/// <summary>Asks the operator a yes/no question. An interface so view models can be tested with a scripted answer.</summary>
public interface IConfirmationService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
}

/// <summary>
/// A modal <see cref="ConfirmDialog"/>. The confirm button is not the default button: pressing Enter by reflex must not
/// arm a vehicle, the operator has to click it (Escape cancels).
/// </summary>
public sealed class DialogConfirmationService(Func<Window?> owner) : IConfirmationService
{
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var dialog = new ConfirmDialog(title, message, confirmText);
        return owner() is { } window && await dialog.ShowDialog<bool?>(window) == true;
    }
}
