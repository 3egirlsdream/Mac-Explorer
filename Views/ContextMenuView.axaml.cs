using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MacExplorer.Models;

namespace MacExplorer.Views;

public partial class ContextMenuView : UserControl
{
    private readonly List<IDisposable> _disposables = new();

    public ContextMenuView()
    {
        InitializeComponent();
    }
}
