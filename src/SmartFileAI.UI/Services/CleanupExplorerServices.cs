using Microsoft.Extensions.DependencyInjection;
using SmartFileAI.Core.Interfaces;
using SmartFileAI.Database;
using SmartFileAI.FileOperations.Cleanup;
using SmartFileAI.UI.ViewModels;

namespace SmartFileAI.UI.Services;

public static class CleanupExplorerServices
{
    public static IServiceCollection AddCleanupExplorer(this IServiceCollection services, string dbPath,
        ICleanupUiScheduler scheduler)
    {
        services.AddSingleton<ICleanupIndexReader>(new SqliteCleanupIndexReader(dbPath));
        services.AddSingleton(scheduler);
        services.AddTransient<ICleanupAnalyzer, RuleBasedCleanupAnalyzer>();
        // Non-disposable transient VMs are owned by each window, not retained in the root container's disposal list.
        services.AddTransient<CleanupCandidatesViewModel>();
        return services;
    }
}
