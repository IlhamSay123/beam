using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Beam.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Beam.App;

public partial class PillWindow : Window
{
    private bool _isExpanded;
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly HashSet<string> _seenInboundIds = new();

    public PillWindow()
    {
        InitializeComponent();
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = SystemParameters.WorkArea.Bottom - Height - 24;
        StartPolling();
    }

    private void StartPolling()
    {
        var index = App.ServerApp.Services.GetRequiredService<HistoryIndex>();
        foreach (var item in index.List().Where(i => i.Direction == "inbound"))
        {
            _seenInboundIds.Add(item.Id); // don't notify for items that existed before this run
        }

        _pollTimer.Tick += (_, _) =>
        {
            var newInbound = index.List()
                .Where(i => i.Direction == "inbound" && !_seenInboundIds.Contains(i.Id))
                .ToList();

            foreach (var item in newInbound)
            {
                _seenInboundIds.Add(item.Id);
                NotifyInboundReceived(item);
            }
        };
        _pollTimer.Start();
    }

    public void NotifyInboundReceived(HistoryItem item)
    {
        var storyboard = (Storyboard)FindResource("GlowPulseStoryboard");
        storyboard.Begin(this);

        new ToastContentBuilder()
            .AddText("Beam")
            .AddText(item.IsText ? "New text received" : $"Received {item.Name}")
            .Show();

        if (ContentHost.Children.Count > 0 && ContentHost.Children[0] is ExpandedPanel panel)
        {
            panel.RefreshHistory();
        }
    }

    public void ToggleExpanded()
    {
        var storyboard = (Storyboard)FindResource(_isExpanded ? "CollapseStoryboard" : "ExpandStoryboard");
        _isExpanded = !_isExpanded;

        if (_isExpanded)
        {
            ContentHost.Children.Clear();
            ContentHost.Children.Add(new ExpandedPanel());
        }

        storyboard.Begin(this);

        if (!_isExpanded)
        {
            ContentHost.Children.Clear();
        }
    }

    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }
}
