using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace NdiMerger.App.Controls;

/// <summary>
/// Horizontal audio meter: green / yellow / red zones, level fill, optional clip hold line.
/// </summary>
public sealed class AudioLevelMeter : Canvas
{
    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(
            nameof(Level),
            typeof(double),
            typeof(AudioLevelMeter),
            new PropertyMetadata(0.0, OnVisualChanged));

    public static readonly DependencyProperty ClipHoldProperty =
        DependencyProperty.Register(
            nameof(ClipHold),
            typeof(double),
            typeof(AudioLevelMeter),
            new PropertyMetadata(double.NaN, OnVisualChanged));

    public static readonly DependencyProperty ShowClipHoldProperty =
        DependencyProperty.Register(
            nameof(ShowClipHold),
            typeof(bool),
            typeof(AudioLevelMeter),
            new PropertyMetadata(false, OnVisualChanged));

    private readonly Rectangle _zoneGreen = new() { Fill = new SolidColorBrush(Color.FromRgb(0x1A, 0x3D, 0x1A)) };
    private readonly Rectangle _zoneYellow = new() { Fill = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x1A)) };
    private readonly Rectangle _zoneRed = new() { Fill = new SolidColorBrush(Color.FromRgb(0x3D, 0x1A, 0x1A)) };
    private readonly Rectangle _fill = new() { Fill = Brushes.LimeGreen };
    private readonly Line _clipLine = new()
    {
        Stroke = Brushes.Red,
        StrokeThickness = 2,
        Visibility = Visibility.Collapsed
    };

    // Zone splits (linear amplitude): green &lt; 0.70, yellow &lt; 0.89, else red.
    private const double YellowStart = 0.70;
    private const double RedStart = 0.89;

    public AudioLevelMeter()
    {
        Height = 12;
        ClipToBounds = true;
        Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
        Children.Add(_zoneGreen);
        Children.Add(_zoneYellow);
        Children.Add(_zoneRed);
        Children.Add(_fill);
        Children.Add(_clipLine);
        SizeChanged += (_, _) => Redraw();
        Loaded += (_, _) => Redraw();
    }

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    /// <summary>0–1 position of the clip hold marker; NaN when inactive.</summary>
    public double ClipHold
    {
        get => (double)GetValue(ClipHoldProperty);
        set => SetValue(ClipHoldProperty, value);
    }

    public bool ShowClipHold
    {
        get => (bool)GetValue(ShowClipHoldProperty);
        set => SetValue(ShowClipHoldProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AudioLevelMeter meter)
            meter.Redraw();
    }

    private void Redraw()
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        SetLeft(_zoneGreen, 0);
        SetTop(_zoneGreen, 0);
        _zoneGreen.Width = w * YellowStart;
        _zoneGreen.Height = h;

        SetLeft(_zoneYellow, w * YellowStart);
        SetTop(_zoneYellow, 0);
        _zoneYellow.Width = w * (RedStart - YellowStart);
        _zoneYellow.Height = h;

        SetLeft(_zoneRed, w * RedStart);
        SetTop(_zoneRed, 0);
        _zoneRed.Width = w * (1.0 - RedStart);
        _zoneRed.Height = h;

        double level = Math.Clamp(Level, 0, 1);
        _fill.Fill = level >= RedStart
            ? Brushes.OrangeRed
            : level >= YellowStart
                ? Brushes.Gold
                : Brushes.LimeGreen;

        SetLeft(_fill, 0);
        SetTop(_fill, 0);
        _fill.Width = Math.Max(0, w * level);
        _fill.Height = h;

        if (ShowClipHold && !double.IsNaN(ClipHold))
        {
            double x = Math.Clamp(ClipHold, 0, 1) * w;
            _clipLine.X1 = x;
            _clipLine.X2 = x;
            _clipLine.Y1 = 0;
            _clipLine.Y2 = h;
            _clipLine.Visibility = Visibility.Visible;
        }
        else
        {
            _clipLine.Visibility = Visibility.Collapsed;
        }
    }
}
