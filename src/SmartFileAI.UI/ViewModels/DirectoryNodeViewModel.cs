using System.Collections.ObjectModel;
using System.IO;
using System.Security;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartFileAI.UI.ViewModels;

public partial class DirectoryNodeViewModel : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _fullPath = string.Empty;
    [ObservableProperty] private bool _isDrive;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string? _loadError;

    private bool _isExpanded;
    private bool _isLoadingChildren;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value) _ = LoadChildrenAsync();
        }
    }

    public string Icon => IsDrive ? "💽" : "📁";
    public ObservableCollection<DirectoryNodeViewModel> Children { get; } = new();
    public bool HasLoadedChildren { get; private set; }

    public DirectoryNodeViewModel() { }

    public DirectoryNodeViewModel(string name, string fullPath, bool isDrive)
    {
        Name = name;
        FullPath = fullPath;
        IsDrive = isDrive;

        if (!string.IsNullOrWhiteSpace(FullPath))
        {
            Children.Add(CreatePlaceholder("Loading..."));
        }
    }

    private static DirectoryNodeViewModel CreatePlaceholder(string name) => new()
    {
        Name = name,
        FullPath = string.Empty,
        IsDrive = false
    };

    private async Task LoadChildrenAsync()
    {
        if (HasLoadedChildren || _isLoadingChildren || string.IsNullOrWhiteSpace(FullPath) || Name == "Loading...") return;

        _isLoadingChildren = true;
        LoadError = null;

        try
        {
            if (!Directory.Exists(FullPath))
            {
                LoadError = $"目录不存在: {FullPath}";
                await ReplaceChildrenAsync(Array.Empty<DirectoryNodeViewModel>(), "[目录不存在]");
                return;
            }

            List<DirectoryNodeViewModel> children = await Task.Run(() =>
            {
                var result = new List<DirectoryNodeViewModel>();
                foreach (var subDir in new DirectoryInfo(FullPath).EnumerateDirectories())
                {
                    try
                    {
                        if ((subDir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        result.Add(new DirectoryNodeViewModel(subDir.Name, subDir.FullName, false));
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException or SecurityException)
                    {
                    }
                }

                return result
                    .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Children.Clear();
                foreach (var child in children) Children.Add(child);
                HasLoadedChildren = true;
            });
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException or SecurityException)
        {
            LoadError = ex.Message;
            await ReplaceChildrenAsync(Array.Empty<DirectoryNodeViewModel>(), "[无法访问]");
        }
        finally
        {
            _isLoadingChildren = false;
        }
    }

    private async Task ReplaceChildrenAsync(IEnumerable<DirectoryNodeViewModel> children, string? emptyPlaceholder = null)
    {
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Children.Clear();
            foreach (var child in children) Children.Add(child);
            if (Children.Count == 0 && !string.IsNullOrWhiteSpace(emptyPlaceholder))
                Children.Add(CreatePlaceholder(emptyPlaceholder));
        });
    }
}
