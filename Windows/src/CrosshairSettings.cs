namespace LanDrop.Windows;

public enum CrosshairShape { Circle, Cross, Diamond }

public sealed class CrosshairSettings
{
    public bool Enabled { get; set; }
    public CrosshairShape Shape { get; set; } = CrosshairShape.Cross;
    public int Size { get; set; } = 3;
    public int Thickness { get; set; } = 3;
    public EdgeOverlayAspectRatio AspectRatio { get; set; }
    public EdgeOverlaySplit Split { get; set; }
    public int PositionX { get; set; }
    public int PositionY { get; set; }
    public string MonitorDeviceName { get; set; } = "";
    public string ColorHex { get; set; } = "#FF0000";
    public string CustomColorHex { get; set; } = "#FF0000";
    public int Opacity { get; set; } = 80;
}
