using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Beam.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace Beam.App;

public partial class ExpandedPanel : UserControl
{
    private readonly HistoryViewModel _viewModel;
    private bool _isSendingFiles;
    private Task _activeQueue = Task.CompletedTask;
    private Task _refreshTask = Task.CompletedTask;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _refreshAgain;
    private bool _closed;
    public Task PendingTransfers => _activeQueue;
    public bool IsQueuing => _isSendingFiles;
    public event EventHandler<TransferActivity>? TransferQueued;
    private HistoryEntryViewModel? SelectedEntry => HistoryList.SelectedItem as HistoryEntryViewModel;
    public ExpandedPanel()
    {
        InitializeComponent();
        _viewModel = new HistoryViewModel(App.ServerApp.Services.GetRequiredService<HistoryIndex>());
        HistoryList.ItemsSource = _viewModel.Entries;
        SizeChanged += (_, _) => DetailsColumn.Width = ActualWidth < 700 ? new GridLength(220) : new GridLength(260);
        SystemParameters.StaticPropertyChanged += MotionPreferenceChanged;
        RefreshHistory();
    }
    public void RefreshHistory() => _ = RefreshHistoryAsync();
    public Task RefreshHistoryAsync()
    {
        if (_closed) return Task.CompletedTask;
        _refreshAgain = true;
        if (!_refreshTask.IsCompleted) return _refreshTask;
        _refreshTask = RefreshHistoryCoreAsync();
        return _refreshTask;
    }
    private async Task RefreshHistoryCoreAsync()
    {
        try
        {
            while (_refreshAgain && !_closed)
            {
                _refreshAgain = false;
                string? selectedId = null;
                await _viewModel.RefreshAsync(_lifetime.Token, () => selectedId = SelectedEntry?.Id);
                if (_closed) return;
                if (selectedId is not null) HistoryList.SelectedItem = _viewModel.Entries.FirstOrDefault(e => e.Id == selectedId);
                ItemCount.Text = $"{_viewModel.Entries.Count} items";
                EmptyState.Visibility = _viewModel.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                EmptyLabel.Text = _viewModel.TotalCount == 0 ? "No transfers yet" : "No matching items";
                UpdatePreview();
                _ = UpdateActivityThumbnailAsync();
            }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception error) when (IsExpectedError(error)) { if (!_closed) TransferStatus.Text = error.Message; }
    }
    public void ShowActivity(TransferActivity activity)
    {
        ActivityStatus.Text = activity.Status;
        ActivityDetail.Text = activity.Detail;
        ActivityDetail.ToolTip = activity.Detail;
        ActivityStatus.Foreground = new SolidColorBrush(activity.Outgoing ? Color.FromRgb(244, 206, 106) : Color.FromRgb(120, 221, 208));
        ActivityRail.ShowActivity(activity);
        _ = UpdateActivityThumbnailAsync();
    }
    private async Task UpdateActivityThumbnailAsync()
    {
        var activity = ActivityRail.Activity;
        if (_closed || activity is null) return;
        try
        {
            var thumbnail = await _viewModel.LoadActivityThumbnailAsync(activity.Items[0], _lifetime.Token);
            if (!_closed && ReferenceEquals(ActivityRail.Activity, activity) && thumbnail is not null) ActivityRail.SetThumbnail(thumbnail);
        }
        catch (OperationCanceledException) when (_closed) { }
    }
    public void StopActivity()
    {
        _closed = true;
        _lifetime.Cancel();
        _viewModel.StopThumbnailLoading();
        ActivityRail.Stop();
        QueueProgress.IsIndeterminate = false;
        SystemParameters.StaticPropertyChanged -= MotionPreferenceChanged;
    }
    private void MotionPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
            QueueProgress.IsIndeterminate = _isSendingFiles && SystemParameters.ClientAreaAnimation;
    }
    public void ShowVault() { VaultTab.IsChecked = true; SearchInput.Focus(); }
    private void View_Changed(object sender, RoutedEventArgs e)
    {
        if (TransferArea is null || _viewModel is null) return;
        var vault = VaultTab.IsChecked == true;
        TransferArea.Visibility = vault ? Visibility.Collapsed : Visibility.Visible;
        ActivityStrip.Visibility = vault ? Visibility.Collapsed : Visibility.Visible;
        VaultFilters.Visibility = vault ? Visibility.Visible : Visibility.Collapsed;
        ListTitle.Text = vault ? "Your vault" : "Recent activity";
        PreviewMedia.MaxHeight = vault ? 190 : 110;
        if (!vault)
        {
            SearchInput.Text = "";
            DirectionInput.SelectedIndex = 0;
            KindInput.SelectedIndex = 0;
            _viewModel.OldestFirst = false;
            SortButton.ToolTip = "Newest first; click for oldest first";
        }
        RefreshHistory();
    }
    private void Filters_Changed(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.SearchQuery = SearchInput.Text;
        _viewModel.DirectionFilter = (DirectionInput.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "All";
        _viewModel.KindFilter = (KindInput.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "All types";
        RefreshHistory();
    }
    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.OldestFirst = !_viewModel.OldestFirst;
        SortButton.ToolTip = _viewModel.OldestFirst ? "Oldest first; click for newest first" : "Newest first; click for oldest first";
        RefreshHistory();
    }
    private async void SendText_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SendTextInput.Text)) { TransferStatus.Text = "Enter a note first."; return; }
        if (_isSendingFiles) return;
        var text = SendTextInput.Text;
        await QueueTextAsync(text, "Note queued for phone.");
        if (TransferStatus.Text == "Note queued for phone." && SendTextInput.Text == text) SendTextInput.Clear();
    }
    private async void SendClipboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsText() || string.IsNullOrWhiteSpace(Clipboard.GetText()))
            { TransferStatus.Text = "No text on the clipboard."; return; }
            await QueueTextAsync(Clipboard.GetText(), "Clipboard text queued for phone.");
        }
        catch (Exception error) when (IsExpectedError(error)) { TransferStatus.Text = error.Message; }
    }
    private Task QueueTextAsync(string text, string message)
    {
        if (_isSendingFiles || _closed) return Task.CompletedTask;
        _activeQueue = QueueTextCoreAsync(text, message);
        return _activeQueue;
    }
    private async Task QueueTextCoreAsync(string text, string message)
    {
        _isSendingFiles = true;
        ResendButton.IsEnabled = false;
        TransferStatus.Text = "Queuing note...";
        try
        {
            var index = App.ServerApp.Services.GetRequiredService<HistoryIndex>();
            var item = await Task.Run(() => OutboxQueue.QueueText(index, text));
            Queued(message, new[] { item });
        }
        catch (Exception error) when (IsExpectedError(error)) { TransferStatus.Text = error.Message; }
        finally { _isSendingFiles = false; UpdatePreview(); }
    }
    private async void ExpandedPanel_Drop(object sender, DragEventArgs e)
    {
        ResetDropZone();
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        if (_isSendingFiles) { e.Effects = DragDropEffects.None; return; }
        var paths = ((string[])e.Data.GetData(DataFormats.FileDrop)!).ToArray();
        e.Effects = DragDropEffects.Copy;
        await QueueFilesAsync(paths);
    }
    private async void DropZone_Click(object sender, MouseButtonEventArgs e)
    {
        if (_isSendingFiles) return;
        var picker = new OpenFileDialog { Title = "Send files to phone", Multiselect = true };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) await QueueFilesAsync(picker.FileNames);
    }
    internal Task QueueFilesAsync(IEnumerable<string> paths, string? displayName = null)
    {
        if (_isSendingFiles || _closed) return Task.CompletedTask;
        _activeQueue = QueueFilesCoreAsync(paths, displayName);
        return _activeQueue;
    }
    private async Task QueueFilesCoreAsync(IEnumerable<string> paths, string? displayName)
    {
        if (_isSendingFiles) return;
        var index = App.ServerApp.Services.GetRequiredService<HistoryIndex>();
        var store = App.ServerApp.Services.GetRequiredService<ItemStore>();
        _isSendingFiles = true;
        ResendButton.IsEnabled = false;
        QueueProgress.Visibility = Visibility.Visible;
        QueueProgress.IsIndeterminate = SystemParameters.ClientAreaAnimation;
        var queued = new List<HistoryItem>();
        var failures = new List<string>();
        try
        {
            foreach (var path in paths)
            {
                TransferStatus.Text = $"Queuing {Path.GetFileName(path)}...";
                try
                {
                    var item = await Task.Run(() =>
                    {
                        if (Directory.Exists(path)) throw new IOException("Folders cannot be sent.");
                        return OutboxQueue.QueueFile(index, store, path, displayName);
                    });
                    queued.Add(item);
                }
                catch (Exception error) when (IsExpectedError(error)) { failures.Add($"{Path.GetFileName(path)}: {error.Message}"); }
            }
            var result = failures.Count == 0 ? $"{queued.Count} file(s) queued for phone." : $"{queued.Count} file(s) queued. {string.Join(" ", failures)}";
            if (queued.Count > 0) Queued(result, queued); else TransferStatus.Text = result;
        }
        finally { _isSendingFiles = false; QueueProgress.IsIndeterminate = false; QueueProgress.Visibility = Visibility.Collapsed; UpdatePreview(); }
    }
    private void Queued(string message, IReadOnlyList<HistoryItem> items)
    {
        TransferStatus.Text = message;
        RefreshHistory();
        TransferQueued?.Invoke(this, new TransferActivity(TransferActivityKind.Queued, items));
    }
    private void Panel_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || _isSendingFiles) { e.Effects = DragDropEffects.None; return; }
        DropZone.Background = new SolidColorBrush(Color.FromRgb(43, 67, 67));
        DropZone.BorderBrush = (Brush)FindResource("BeamBrush");
        DropTitle.Text = "Release to send";
        e.Effects = DragDropEffects.Copy;
    }
    private void Panel_DragLeave(object sender, DragEventArgs e) => ResetDropZone();
    private void ResetDropZone()
    {
        DropZone.Background = new SolidColorBrush(Color.FromRgb(29, 40, 42));
        DropZone.BorderBrush = new SolidColorBrush(Color.FromRgb(72, 120, 116));
        DropTitle.Text = "Send files";
    }
    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();
    private void UpdatePreview()
    {
        var entry = SelectedEntry;
        PreviewName.Text = entry?.Title ?? "Select an item";
        PreviewMeta.Text = entry is null ? "" : $"{entry.DirectionLabel} / {entry.DisplaySize}\n{entry.DisplayTime}";
        PreviewState.Text = entry?.State ?? "";
        PreviewImage.Source = entry?.Thumbnail;
        PreviewIcon.Text = entry?.Icon ?? "\uE8A5";
        PreviewIcon.Visibility = entry?.Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
        if (entry is not null) _ = LoadSelectedThumbnailAsync(entry);
        PreviewText.Text = entry?.TextContent ?? "";
        PreviewText.Visibility = entry?.IsText == true ? Visibility.Visible : Visibility.Collapsed;
        PreviewMedia.Visibility = entry?.IsText == true ? Visibility.Collapsed : Visibility.Visible;
        OpenItemLabel.Text = entry?.IsText == true ? "Copy note" : "Open file";
        OpenItemButton.IsEnabled = entry is not null && !entry.Missing;
        RevealButton.IsEnabled = entry is { IsText: false, Missing: false };
        ResendButton.IsEnabled = entry is not null && !entry.Missing && !_isSendingFiles;
    }
    private async Task LoadSelectedThumbnailAsync(HistoryEntryViewModel entry)
    {
        if (_closed) return;
        try
        {
            await entry.EnsureThumbnailAsync(_lifetime.Token);
            if (_closed || !ReferenceEquals(SelectedEntry, entry)) return;
            PreviewImage.Source = entry.Thumbnail;
            PreviewIcon.Visibility = entry.Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (_closed) { }
    }
    private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();
    private void OpenItem_Click(object sender, RoutedEventArgs e) => OpenSelected();
    private void OpenSelected() => RunAction(() =>
    {
        var entry = SelectedEntry;
        if (entry is null) return;
        if (entry.IsText) { Clipboard.SetText(entry.TextContent ?? ""); TransferStatus.Text = "Note copied."; }
        else if (entry.Missing) TransferStatus.Text = "This file is no longer in Beam storage.";
        else Process.Start(new ProcessStartInfo(entry.FilePath!) { UseShellExecute = true });
    });
    private void Reveal_Click(object sender, RoutedEventArgs e) => RunAction(() =>
    {
        if (SelectedEntry is { IsText: false, Missing: false } entry)
            Process.Start("explorer.exe", $"/select,\"{entry.FilePath}\"");
    });
    private async void Resend_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry is not { } entry) return;
        if (entry.IsText) await QueueTextAsync(entry.TextContent ?? "", "Note queued again.");
        else if (!entry.Missing) await QueueFilesAsync(new[] { entry.FilePath! }, entry.Name);
    }
    private void Inbox_Click(object sender, RoutedEventArgs e) => RunAction(() => Process.Start(new ProcessStartInfo(App.Options.DataDirectory) { UseShellExecute = true }));
    private void Pair_Click(object sender, RoutedEventArgs e) => App.ShowPairing();
    private static bool IsExpectedError(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception or COMException;
    private void RunAction(Action action)
    {
        try { action(); }
        catch (Exception error) when (IsExpectedError(error)) { TransferStatus.Text = error.Message; }
    }
}
