using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SmartFileAI.UI.ViewModels;

namespace SmartFileAI.UI.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is DirectoryNodeViewModel node
            && !string.IsNullOrWhiteSpace(node.FullPath)
            && !string.Equals(node.Name, "Loading...", StringComparison.Ordinal))
        {
            _viewModel.SelectedNode = node;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e) => _viewModel.CancelScan();
}
