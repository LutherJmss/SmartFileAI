namespace SmartFileAI.UI.Services;

public interface ICleanupUiScheduler
{
    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}
