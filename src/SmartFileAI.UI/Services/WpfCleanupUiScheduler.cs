using System.Windows.Threading;

namespace SmartFileAI.UI.Services;

public sealed class WpfCleanupUiScheduler(Dispatcher dispatcher) : ICleanupUiScheduler
{
    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        => dispatcher.InvokeAsync(action, DispatcherPriority.Background, cancellationToken).Task;
}
