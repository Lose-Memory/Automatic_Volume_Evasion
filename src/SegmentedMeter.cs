using System.Windows;
using System.Windows.Media;

namespace AutoVolume;

/// <summary>分格电平条：一段一段亮起，和调音台的表头一致。Marker 是阈值刻度，NaN 表示不画。</summary>
public sealed class SegmentedMeter : FrameworkElement
{
    private const double SegmentWidth = 4;
    private const double SegmentGap = 2;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SegmentedMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(SegmentedMeter),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyBrushProperty = DependencyProperty.Register(
        nameof(EmptyBrush), typeof(Brush), typeof(SegmentedMeter),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkerProperty = DependencyProperty.Register(
        nameof(Marker), typeof(double), typeof(SegmentedMeter),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkerBrushProperty = DependencyProperty.Register(
        nameof(MarkerBrush), typeof(Brush), typeof(SegmentedMeter),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush EmptyBrush
    {
        get => (Brush)GetValue(EmptyBrushProperty);
        set => SetValue(EmptyBrushProperty, value);
    }

    /// <summary>0–1 的刻度位置，NaN 表示不显示。</summary>
    public double Marker
    {
        get => (double)GetValue(MarkerProperty);
        set => SetValue(MarkerProperty, value);
    }

    public Brush MarkerBrush
    {
        get => (Brush)GetValue(MarkerBrushProperty);
        set => SetValue(MarkerBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var count = (int)Math.Floor((width + SegmentGap) / (SegmentWidth + SegmentGap));
        if (count <= 0) return;

        var lit = (int)Math.Round(count * Math.Clamp(Value, 0, 1));
        var fill = FillBrush ?? Brushes.Transparent;
        var empty = EmptyBrush ?? Brushes.Transparent;

        for (var i = 0; i < count; i++)
            dc.DrawRectangle(i < lit ? fill : empty, null,
                new Rect(i * (SegmentWidth + SegmentGap), 0, SegmentWidth, height));

        if (double.IsNaN(Marker)) return;

        var x = Math.Clamp(Marker, 0, 1) * width - 1;
        dc.DrawRectangle(MarkerBrush ?? Brushes.Transparent, null, new Rect(x, 0, 2, height));
    }
}