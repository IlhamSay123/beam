using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Beam.App;

public partial class PillWindow : Window
{
    private bool _isExpanded;

    public PillWindow()
    {
        InitializeComponent();
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = SystemParameters.WorkArea.Bottom - Height - 24;
    }

    public void ToggleExpanded()
    {
        var storyboard = (Storyboard)FindResource(_isExpanded ? "CollapseStoryboard" : "ExpandStoryboard");
        _isExpanded = !_isExpanded;
        storyboard.Begin(this);
        // ContentHost stays empty until Task 10 introduces ExpandedPanel and
        // wires it in here — this task only proves the resize/expand shell.
    }

    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }
}
