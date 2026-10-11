using System.ComponentModel;
using System.Windows;
using SmartFileAI.UI.ViewModels;

namespace SmartFileAI.UI.Views;

public partial class CleanupCandidatesWindow : Window
{
    private readonly CleanupCandidatesViewModel _viewModel;
    public CleanupCandidatesWindow(CleanupCandidatesViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }
    private void Window_Closing(object? sender, CancelEventArgs args) => _viewModel.OnWindowClosing();
}
