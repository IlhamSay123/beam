using System.Windows;
using System.Windows.Controls;
using Beam.Server.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Beam.App;

public partial class ExpandedPanel : UserControl
{
    private readonly HistoryViewModel _viewModel;

    public ExpandedPanel()
    {
        InitializeComponent();

        var index = App.ServerApp.Services.GetRequiredService<HistoryIndex>();
        _viewModel = new HistoryViewModel(index);
        HistoryList.ItemsSource = _viewModel.Entries;
    }

    private void ExpandedPanel_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        var index = App.ServerApp.Services.GetRequiredService<HistoryIndex>();
        var store = App.ServerApp.Services.GetRequiredService<ItemStore>();

        foreach (var path in (string[])e.Data.GetData(DataFormats.FileDrop)!)
        {
            OutboxQueue.QueueFile(index, store, path);
        }

        _viewModel.Refresh();
    }

    private void HistoryList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryEntryViewModel entry)
        {
            return;
        }

        if (entry.IsText && entry.TextContent is not null)
        {
            Clipboard.SetText(entry.TextContent);
        }
        else
        {
            var index = App.ServerApp.Services.GetRequiredService<HistoryIndex>();
            var item = index.Get(entry.Id);
            if (item?.FilePath is not null)
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.FilePath}\"");
            }
        }
    }
}
