using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Eddms.Wpf;

/// <summary>A node of the Smart Folder tree shown in the TreeView.</summary>
public sealed class FolderNode : INotifyPropertyChanged
{
    private bool _isExpanded;

    public long FolderId { get; init; }
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public int OwnCount { get; init; }
    public ObservableCollection<FolderNode> Children { get; } = new();

    /// <summary>Documents in this folder plus all sub-folders.</summary>
    public int TotalCount => OwnCount + Children.Sum(c => c.TotalCount);
    public string CountLabel => TotalCount > 0 ? $"({TotalCount})" : "";
    public string Icon => Children.Count > 0 ? "📁" : "📄";

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
