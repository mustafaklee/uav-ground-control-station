using Avalonia.Threading;

namespace Gcs.Desktop.Services;

/// <summary>
/// Runs work on the UI thread. UI controls may only be touched from that thread, but SignalR raises events on
/// background threads. An interface so view model tests can run the work immediately instead.
/// </summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
