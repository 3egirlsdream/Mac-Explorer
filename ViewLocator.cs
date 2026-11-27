using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CommunityToolkit.Mvvm.ComponentModel;
using MacExplorer.ViewModels;
using MacExplorer.Views;

namespace MacExplorer;

public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param) => param switch
    {
        null => null,
        FileListViewModel => new FileListView(),
        AiViewModel => new AiView(),
        _ => new TextBlock
        {
            Text = "Not Found: " + param.GetType().FullName?.Replace("ViewModel", "View", StringComparison.Ordinal)
        }
    };

    public bool Match(object? data)
    {
        return data is ObservableObject;
    }
}
