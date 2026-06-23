using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MacExplorer.ViewModels;

namespace MacExplorer.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public FileListViewModel FileList { get; } = null!;

    public MainWindowViewModel()
    {
    }

    public MainWindowViewModel(FileListViewModel fileList) : this()
    {
        FileList = fileList;
    }
}
