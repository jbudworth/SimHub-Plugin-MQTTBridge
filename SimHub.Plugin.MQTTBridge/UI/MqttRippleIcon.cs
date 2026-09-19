using System;
using System.Windows.Media;

namespace SimHub.Plugin.MQTTBridge.UI
{
    /// <summary>
    /// Renders the official MQTT "ripple" mark as a WPF DrawingImage, for use as the
    /// plugin's sidebar icon.
    ///
    /// Source: https://github.com/mqtt/mqttorg-graphics (svg/mqtt-icon-transparent.svg).
    /// Per that repo's README, these graphics are free to use to indicate support of the
    /// MQTT protocol in a product (not to imply endorsement by the MQTT community or any
    /// standards body). The four path definitions below are copied verbatim from that SVG;
    /// only the fill color is applied here in code instead of inline in the markup.
    /// </summary>
    internal static class MqttRippleIcon
    {
        private static readonly string[] PathData =
        {
            "M7.1,180.6v117.1c0,8.4,6.8,15.3,15.3,15.3H142C141,239.8,80.9,180.7,7.1,180.6z",
            "M7.1,84.1v49.8c99,0.9,179.4,80.7,180.4,179.1h51.7C238.2,186.6,134.5,84.2,7.1,84.1z",
            "M312.9,297.6V193.5C278.1,107.2,207.3,38.9,119,7.1H22.4c-8.4,0-15.3,6.8-15.3,15.3v15" +
            "c152.6,0.9,276.6,124,277.6,275.6h13C306.1,312.9,312.9,306.1,312.9,297.6z",
            "M272.6,49.8c14.5,14.4,28.6,31.7,40.4,47.8V22.4c0-8.4-6.8-15.3-15.3-15.3h-77.3" +
            "C238.4,19.7,256.6,33.9,272.6,49.8z"
        };

        // #660066, the fill color used in the official mark.
        private static readonly Color FillColor = Color.FromRgb(0x66, 0x00, 0x66);

        private static readonly Lazy<ImageSource> Instance = new Lazy<ImageSource>(Build);

        public static ImageSource Get() => Instance.Value;

        private static ImageSource Build()
        {
            var brush = new SolidColorBrush(FillColor);
            brush.Freeze();

            var group = new DrawingGroup();
            foreach (var d in PathData)
            {
                var geometry = Geometry.Parse(d);
                group.Children.Add(new GeometryDrawing(brush, null, geometry));
            }

            var image = new DrawingImage(group);
            image.Freeze();
            return image;
        }
    }
}
