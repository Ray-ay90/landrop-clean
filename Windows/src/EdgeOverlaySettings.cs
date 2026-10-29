namespace LanDrop.Windows;

public enum EdgeOverlayShape { Rectangle, Semicircle, Triangle }
public enum EdgeOverlayAspectRatio { FullScreen, UltraWide21x9, Standard4x3, Standard5x4 }
public enum EdgeOverlaySplit { None, SideBySide, TopBottom }
public enum EdgeOverlayDisplayMode { Window, Stretch }

public sealed class EdgeOverlaySettings
{
    public bool Enabled { get; set; }
    public EdgeOverlayShape Shape { get; set; }
    public int Size { get; set; } = 3;
    public int Length { get; set; }
    public EdgeOverlayAspectRatio AspectRatio { get; set; }
    public EdgeOverlaySplit Split { get; set; }
    public EdgeOverlayDisplayMode DisplayMode { get; set; } = EdgeOverlayDisplayMode.Stretch;
    public string ColorHex { get; set; } = "#00FF00";
    public int Opacity { get; set; } = 60;
    public bool UsePerEdgeOpacity { get; set; }
    public bool TopVisible { get; set; } = true;
    public bool BottomVisible { get; set; } = true;
    public bool LeftVisible { get; set; } = true;
    public bool RightVisible { get; set; } = true;
    public int TopOpacity { get; set; } = 60;
    public int BottomOpacity { get; set; } = 60;
    public int LeftOpacity { get; set; } = 60;
    public int RightOpacity { get; set; } = 60;
}
