using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SmartFileAI.UI.ViewModels;

public sealed class CleanupCandidateCollection : ObservableCollection<CleanupCandidateRowViewModel>
{
    // WPF CollectionView cannot consume multi-item Add notifications. One Reset per small batch is supported.
    public void AddBatch(IReadOnlyList<CleanupCandidateRowViewModel> rows)
    {
        if (rows.Count == 0) return;
        CheckReentrancy();
        foreach (var row in rows) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
