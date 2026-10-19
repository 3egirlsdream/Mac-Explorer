using Avalonia.Controls;
using SvgPath = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;

namespace MacExplorer.Controls;

/// <summary>24-unit outline icons from the transfer panel design.</summary>
internal static class FileTaskIcons
{
    internal const string Copy = "M14 20H3.8A1.8 1.8 0 0 1 2 18.2V5A2 2 0 0 1 4 3H8L10.5 6H20A2 2 0 0 1 22 8V13 M15.5 13H22A1 1 0 0 1 23 14V21.5A1 1 0 0 1 22 22.5H15.5A1 1 0 0 1 14.5 21.5V14A1 1 0 0 1 15.5 13Z";
    internal const string Download = "M14 2H5A2 2 0 0 0 3 4V20A2 2 0 0 0 5 22H19A2 2 0 0 0 21 20V8L14 2V8H21 M12 10V19 M8 15L12 19L16 15";
    internal const string Archive = "M14 2H5A2 2 0 0 0 3 4V20A2 2 0 0 0 5 22H19A2 2 0 0 0 21 20V8L14 2V8H21 M9 2V4H11V6H9V8H11V10H9V12H11V14H9 M9 15H11V18H9Z";
    internal const string Move = "M14 2H5A2 2 0 0 0 3 4V20A2 2 0 0 0 5 22H19A2 2 0 0 0 21 20V18 M14 2V8H21L14 2 M13 13H24 M20 9L24 13L20 17";
    internal const string Upload = "M7.5 20H5.5A4.5 4.5 0 0 1 4.5 11.1A5.3 5.3 0 0 1 14.9 8.5A5.8 5.8 0 0 1 18.5 20H16.5 M12 23V11 M7.5 15.5L12 11L16.5 15.5";
    internal const string Pause = "M7 4V20 M17 4V20";
    internal const string Play = "M6.5 3.5L21 12L6.5 20.5Z";
    internal const string Retry = "M19.8 8A8 8 0 1 0 20 15 M20 3V8H15";
    internal const string Close = "M5 5L19 19 M19 5L5 19";

    internal static Viewbox Create(string data, double size)
    {
        var path = new SvgPath { Data = Geometry.Parse(data), Width = 26, Height = 26,
            Margin = new Avalonia.Thickness(1), Stretch = Stretch.None, Fill = null,
            StrokeThickness = size < 26 ? 2 : 1.5, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round };
        return new Viewbox { Width = size, Height = size, Child = path };
    }
}
