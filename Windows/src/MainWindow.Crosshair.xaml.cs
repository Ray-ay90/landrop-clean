using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Globalization;

namespace LanDrop.Windows;

public partial class MainWindow
{
    private CrosshairSettings _crosshairSettings = new();
    private CrosshairWindow? _crosshairWindow;
    private bool _updatingCrosshairControls = true;
    private bool _selectingCrosshairPreset;

    private void OpenCrosshairSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentNote();
        HideViews();
        CrosshairView.Visibility = Visibility.Visible;
        RefreshCrosshairControls();
        SetActiveNavigation(CrosshairNavButton);
    }

    private void RefreshCrosshairControls()
    {
        _updatingCrosshairControls = true;
        CrosshairEnabledBox.IsChecked = _crosshairSettings.Enabled;
        CrosshairShapeBox.SelectedIndex = Math.Clamp((int)_crosshairSettings.Shape, 0, 2);
        CrosshairSizeBox.SelectedIndex = Math.Clamp(_crosshairSettings.Size, 0, 6);
        CrosshairThicknessBox.SelectedIndex = Math.Clamp(_crosshairSettings.Thickness, 0, 6);
        CrosshairAspectRatioBox.SelectedIndex = Math.Clamp((int)_crosshairSettings.AspectRatio, 0, 3);
        CrosshairSplitBox.SelectedIndex = Math.Clamp((int)_crosshairSettings.Split, 0, 2);
        CrosshairMonitorBox.Items.Clear();
        CrosshairMonitorBox.Items.Add(new ComboBoxItem { Content = "所有屏幕", Tag = "" });
        foreach (var display in CrosshairWindow.GetDisplays())
            CrosshairMonitorBox.Items.Add(new ComboBoxItem { Content = display.Name, Tag = display.DeviceName });
        var selected = CrosshairMonitorBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => (string)item.Tag == _crosshairSettings.MonitorDeviceName);
        if (selected is null)
        {
            selected = new ComboBoxItem { Content = "已选屏幕未连接，暂用所有屏幕", Tag = _crosshairSettings.MonitorDeviceName };
            CrosshairMonitorBox.Items.Add(selected);
        }
        CrosshairMonitorBox.SelectedItem = selected;
        CrosshairPositionXBox.Text = _crosshairSettings.PositionX.ToString(CultureInfo.InvariantCulture);
        CrosshairPositionYBox.Text = _crosshairSettings.PositionY.ToString(CultureInfo.InvariantCulture);
        CrosshairColorBox.Text = _crosshairSettings.ColorHex;
        CrosshairOpacitySlider.Value = Math.Clamp(_crosshairSettings.Opacity, 0, 100);
        CrosshairColorHint.Text = "输入 #RRGGBB 格式的六位颜色值。";
        CrosshairPositionHint.Text = "以中心为起点，正 X 向右，正 Y 向下。";
        _updatingCrosshairControls = false;
        UpdateCrosshairPreview();
    }

    private void CrosshairControls_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingCrosshairControls) return;
        _crosshairSettings.Enabled = CrosshairEnabledBox.IsChecked == true;
        _crosshairSettings.Shape = (CrosshairShape)Math.Max(0, CrosshairShapeBox.SelectedIndex);
        _crosshairSettings.Size = Math.Max(0, CrosshairSizeBox.SelectedIndex);
        _crosshairSettings.Thickness = Math.Max(0, CrosshairThicknessBox.SelectedIndex);
        _crosshairSettings.AspectRatio = (EdgeOverlayAspectRatio)Math.Max(0, CrosshairAspectRatioBox.SelectedIndex);
        _crosshairSettings.Split = (EdgeOverlaySplit)Math.Max(0, CrosshairSplitBox.SelectedIndex);
        _crosshairSettings.MonitorDeviceName = (CrosshairMonitorBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _crosshairSettings.Opacity = (int)CrosshairOpacitySlider.Value;
        UpdateCrosshairPreview();
        ApplyCrosshairSettings();
        try
        {
            SaveSettings();
            CrosshairStatusText.Text = "更改已自动保存。准心不抢焦点，不影响鼠标点击。";
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to save crosshair settings.", ex);
            CrosshairStatusText.Text = "设置暂未保存，请检查 data 目录是否可写。";
        }
    }

    private void CrosshairColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingCrosshairControls) return;
        if (!TryGetEdgeOverlayColor(CrosshairColorBox.Text, out var color))
        {
            CrosshairColorHint.Text = "请输入有效的六位十六进制颜色，例如 #FF0000。";
            return;
        }
        _crosshairSettings.ColorHex = color;
        if (!_selectingCrosshairPreset) _crosshairSettings.CustomColorHex = color;
        CrosshairColorHint.Text = "输入 #RRGGBB 格式的六位颜色值。";
        CrosshairControls_Changed(sender, e);
    }

    private void CrosshairColorPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string color }) return;
        _selectingCrosshairPreset = true;
        try { CrosshairColorBox.Text = color; }
        finally { _selectingCrosshairPreset = false; }
    }

    private void CrosshairCustomColorButton_Click(object sender, RoutedEventArgs e)
    {
        CrosshairColorBox.Text = _crosshairSettings.CustomColorHex;
    }

    private void CrosshairResetPositionButton_Click(object sender, RoutedEventArgs e)
    {
        _updatingCrosshairControls = true;
        CrosshairPositionXBox.Text = "0";
        CrosshairPositionYBox.Text = "0";
        _crosshairSettings.PositionX = 0;
        _crosshairSettings.PositionY = 0;
        CrosshairPositionHint.Text = "以中心为起点，正 X 向右，正 Y 向下。";
        _updatingCrosshairControls = false;
        CrosshairControls_Changed(sender, e);
    }

    private void CrosshairPositionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingCrosshairControls) return;
        if (!int.TryParse(CrosshairPositionXBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
            !int.TryParse(CrosshairPositionYBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
        {
            CrosshairPositionHint.Text = "请输入整数偏移；无效输入不会改变当前准心位置。";
            return;
        }
        _crosshairSettings.PositionX = x;
        _crosshairSettings.PositionY = y;
        CrosshairPositionHint.Text = "以中心为起点，正 X 向右，正 Y 向下。";
        CrosshairControls_Changed(sender, e);
    }

    private void UpdateCrosshairPreview()
    {
        CrosshairSummaryText.Text = _crosshairSettings.Enabled ? "已开启" : "已关闭";
        if (TryGetEdgeOverlayColor(_crosshairSettings.ColorHex, out var color))
            CrosshairColorPreview.Background = new SolidColorBrush(ParseColor(color));
        CrosshairPreviewCanvas.Children.Clear();
        var preview = new CrosshairSettings
        {
            Shape = _crosshairSettings.Shape, Size = _crosshairSettings.Size,
            Thickness = _crosshairSettings.Thickness, ColorHex = _crosshairSettings.ColorHex,
            Opacity = _crosshairSettings.Opacity
        };
        foreach (var shape in CrosshairGeometry.CreateShapes(preview,
            new Rect(0, 0, CrosshairPreviewCanvas.Width, CrosshairPreviewCanvas.Height)))
            CrosshairPreviewCanvas.Children.Add(shape);
    }

    private void ApplyCrosshairSettings()
    {
        if (!_crosshairSettings.Enabled)
        {
            _crosshairWindow?.Close();
            _crosshairWindow = null;
            return;
        }
        _crosshairWindow ??= new CrosshairWindow();
        _crosshairWindow.ApplySettings(_crosshairSettings);
    }

    private bool CloseCrosshairDropdown()
    {
        foreach (var box in new[] { CrosshairShapeBox, CrosshairSizeBox, CrosshairThicknessBox,
            CrosshairMonitorBox, CrosshairAspectRatioBox, CrosshairSplitBox })
        {
            if (!box.IsDropDownOpen) continue;
            box.IsDropDownOpen = false;
            return true;
        }
        return false;
    }
}
