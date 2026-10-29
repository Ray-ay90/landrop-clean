using System.Windows;
using System.Windows.Automation;

namespace LanDrop.Windows;

public partial class MainWindow
{
    private void ToggleSidebarButton_Click(object sender, RoutedEventArgs e)
    {
        var expanded = SidebarPanel.Visibility == Visibility.Visible;
        SidebarPanel.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = new GridLength(expanded ? 0 : 232);
        SidebarToggleButton.ToolTip = expanded ? "展开侧栏" : "收起侧栏";
        AutomationProperties.SetName(SidebarToggleButton, expanded ? "展开侧栏" : "收起侧栏");
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
}
