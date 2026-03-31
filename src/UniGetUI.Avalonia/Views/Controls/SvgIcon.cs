using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace UniGetUI.Avalonia.Views.Controls;

/// <summary>
/// Lightweight SVG icon renderer that uses Avalonia's native geometry engine
/// instead of SkiaSharp's SVG module (which is broken on some macOS configurations).
/// Supports single-path and multi-path SVGs with a uniform viewBox.
/// </summary>
public class SvgIcon : Control
{
    public static readonly StyledProperty<string?> PathProperty =
        AvaloniaProperty.Register<SvgIcon, string?>(nameof(Path));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<SvgIcon, IBrush?>(nameof(Foreground),
            defaultValue: null, inherits: true);

    public string? Path
    {
        get => GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private readonly List<Geometry> _geometries = new();
    private double _viewBoxWidth = 24, _viewBoxHeight = 24;

    static SvgIcon()
    {
        AffectsRender<SvgIcon>(ForegroundProperty);
        AffectsMeasure<SvgIcon>(PathProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PathProperty)
            LoadSvg(change.NewValue as string);
    }

    private void LoadSvg(string? uri)
    {
        _geometries.Clear();
        _viewBoxWidth = 24;
        _viewBoxHeight = 24;

        if (string.IsNullOrEmpty(uri))
        {
            InvalidateVisual();
            return;
        }

        try
        {
            using Stream stream = AssetLoader.Open(new Uri(uri));
            XDocument doc = XDocument.Load(stream);
            XElement? svg = doc.Root;
            if (svg is null) return;

            string? vb = svg.Attribute("viewBox")?.Value;
            if (vb != null)
            {
                var parts = vb.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 &&
                    double.TryParse(parts[2], System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double w) &&
                    double.TryParse(parts[3], System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double h))
                {
                    _viewBoxWidth = w;
                    _viewBoxHeight = h;
                }
            }
            else if (
                double.TryParse(svg.Attribute("width")?.Value, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double svgW) &&
                double.TryParse(svg.Attribute("height")?.Value, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double svgH))
            {
                _viewBoxWidth = svgW;
                _viewBoxHeight = svgH;
            }

            XNamespace ns = "http://www.w3.org/2000/svg";
            foreach (XElement el in doc.Descendants(ns + "path"))
            {
                string? d = el.Attribute("d")?.Value;
                if (!string.IsNullOrEmpty(d))
                {
                    try { _geometries.Add(Geometry.Parse(d)); }
                    catch { /* skip malformed path data */ }
                }
            }
            foreach (XElement el in doc.Descendants(ns + "ellipse"))
            {
                if (double.TryParse(el.Attribute("cx")?.Value, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double cx) &&
                    double.TryParse(el.Attribute("cy")?.Value, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double cy) &&
                    double.TryParse(el.Attribute("rx")?.Value, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double rx) &&
                    double.TryParse(el.Attribute("ry")?.Value, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double ry))
                {
                    _geometries.Add(new EllipseGeometry(new Rect(cx - rx, cy - ry, rx * 2, ry * 2)));
                }
            }
        }
        catch
        {
            // Silently ignore missing or unreadable assets
        }

        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? _viewBoxWidth : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? _viewBoxHeight : availableSize.Height;
        return new Size(w, h);
    }

    public override void Render(DrawingContext context)
    {
        if (_geometries.Count == 0) return;

        IBrush brush = Foreground ?? Brushes.Black;

        double scaleX = Bounds.Width / _viewBoxWidth;
        double scaleY = Bounds.Height / _viewBoxHeight;
        double scale = Math.Min(scaleX, scaleY);

        double offsetX = (Bounds.Width - _viewBoxWidth * scale) / 2;
        double offsetY = (Bounds.Height - _viewBoxHeight * scale) / 2;

        using var _ = context.PushTransform(
            Matrix.CreateTranslation(offsetX, offsetY) * Matrix.CreateScale(scale, scale));

        foreach (Geometry geo in _geometries)
            context.DrawGeometry(brush, null, geo);
    }
}
