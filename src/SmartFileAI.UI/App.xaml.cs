using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartFileAI.Core.Interfaces;
using SmartFileAI.Database;
using SmartFileAI.FileOperations;
using SmartFileAI.Scanner;
using SmartFileAI.UI.ViewModels;
using SmartFileAI.UI.Views;

namespace SmartFileAI.UI;

public partial class App : Application
{
    private readonly IHost _host;
    private readonly string _dbPath;

    public App()
    {
        _dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartFileAI", "smartfile.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IFileRepository>(_ => new FileRepository(_dbPath));
                services.AddSingleton<IFileScanner, NormalScanner>();
                services.AddSingleton<IFileOperationService, FileOperationService>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            await using (var context = new SmartFileAIDbContext(_dbPath))
                await context.InitializeDatabaseAsync();
            await _host.StartAsync();
            _host.Services.GetRequiredService<MainWindow>().Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"SmartFile AI 启动失败：\n{ex.Message}", "启动错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try { await _host.StopAsync(TimeSpan.FromSeconds(5)); }
        catch { }
        _host.Dispose();
        base.OnExit(e);
    }
}
