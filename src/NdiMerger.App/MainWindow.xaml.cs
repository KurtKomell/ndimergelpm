using System.IO;
using System.Windows;
using System.Windows.Controls;
using NdiMerger.Sources;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NdiMerger.App.ViewModels;
using NdiMerger.Core.Models;

namespace NdiMerger.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _overlayTimer;
    private MainViewModel Vm => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += (_, _) =>
        {
            _overlayTimer.Stop();
            // Commit any in-progress TextBox edits (e.g. Rotation) before shutdown.
            FocusManager.SetFocusedElement(this, this);
            Vm.RequestShutdown();
        };
        Closed += (_, _) =>
        {
            Vm.SaveSession(this);
            Vm.Dispose();
            if (Application.Current is not null)
                Application.Current.Shutdown();
        };

        _overlayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _overlayTimer.Tick += (_, _) => DrawOverlays();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var assets = FindAssetsDir();
        Vm.Initialize(assets);
        RestoreWindowBounds();
        FitPreviewToBorder();
        DrawOverlays();
        _overlayTimer.Start();
    }

    private void RestoreWindowBounds()
    {
        var session = Vm.LoadedSession;
        if (session is null) return;

        if (session.WindowWidth >= 400)
            Width = session.WindowWidth;
        if (session.WindowHeight >= 300)
            Height = session.WindowHeight;

        if (!double.IsNaN(session.WindowLeft) && !double.IsNaN(session.WindowTop))
        {
            Left = session.WindowLeft;
            Top = session.WindowTop;
            var work = SystemParameters.WorkArea;
            if (Left < work.Left - Width + 100 || Left > work.Right - 100 ||
                Top < work.Top - Height + 100 || Top > work.Bottom - 100)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        if (session.WindowState is 1 or 2)
            WindowState = (WindowState)session.WindowState;
    }

    private static string FindAssetsDir()
    {
        var candidates = new[]
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "assets"),
            System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "assets")),
            System.IO.Path.GetFullPath(System.IO.Path.Combine(Directory.GetCurrentDirectory(), "assets")),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(System.IO.Path.Combine(c, "pixelmap.json")))
                return c;
        }
        return System.IO.Path.Combine(AppContext.BaseDirectory, "assets");
    }

    private void NdiAdapterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NdiAdapterCombo.SelectedItem is NdiAdapterChoice choice)
            Vm.CommitNdiAdapterFromUi(choice);
    }

    private void PreviewBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        FitPreviewToBorder();
        DrawOverlays();
    }

    private void FitPreviewToBorder()
    {
        if (Vm.CanvasWidth <= 0 || Vm.CanvasHeight <= 0)
            return;

        double availW = PreviewBorder.ActualWidth;
        double availH = PreviewBorder.ActualHeight;
        if (availW <= 1 || availH <= 1)
            return;

        double scale = Math.Min(availW / Vm.CanvasWidth, availH / Vm.CanvasHeight);
        double viewW = Vm.CanvasWidth * scale;
        double viewH = Vm.CanvasHeight * scale;

        PreviewHost.Width = viewW;
        PreviewHost.Height = viewH;
        PreviewImageControl.Width = viewW;
        PreviewImageControl.Height = viewH;
        OverlayCanvas.Width = viewW;
        OverlayCanvas.Height = viewH;
    }

    private void DrawOverlays()
    {
        OverlayCanvas.Children.Clear();
        if (Vm.Room3DEnabled)
            return;
        if (!TryGetCanvasSize(out var cw, out var ch) || cw <= 0 || ch <= 0)
            return;
        if (OverlayCanvas.Width <= 0 || OverlayCanvas.Height <= 0)
            return;

        double sx = OverlayCanvas.Width / cw;
        double sy = OverlayCanvas.Height / ch;

        var zoneColors = new[]
        {
            Color.FromArgb(140, 0, 200, 255),
            Color.FromArgb(140, 255, 180, 0),
            Color.FromArgb(140, 80, 255, 120),
            Color.FromArgb(140, 255, 80, 160),
        };
        int i = 0;
        foreach (var z in Vm.Zones)
        {
            var stroke = new SolidColorBrush(zoneColors[i++ % zoneColors.Length]);
            var rect = new Rectangle
            {
                Width = z.Width * sx,
                Height = z.Height * sy,
                Stroke = stroke,
                StrokeThickness = 1.5,
                Fill = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255))
            };
            Canvas.SetLeft(rect, z.X * sx);
            Canvas.SetTop(rect, z.Y * sy);
            OverlayCanvas.Children.Add(rect);

            var label = new TextBlock
            {
                Text = $"{z.Name}\n{z.ContentWidth:0}×{z.ContentHeight:0}",
                Foreground = Brushes.White,
                FontSize = 10,
                Opacity = 0.9,
                TextAlignment = TextAlignment.Center
            };
            Canvas.SetLeft(label, z.X * sx + 4);
            Canvas.SetTop(label, z.Y * sy + 4);
            OverlayCanvas.Children.Add(label);
        }

        // LiDAR sensor marker for calibration
        if (Vm.LidarSensorX > 0 || Vm.LidarSensorY > 0)
        {
            var sensor = new Ellipse
            {
                Width = 12,
                Height = 12,
                Stroke = Brushes.Red,
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(120, 255, 60, 60))
            };
            Canvas.SetLeft(sensor, Vm.LidarSensorX * sx - 6);
            Canvas.SetTop(sensor, Vm.LidarSensorY * sy - 6);
            OverlayCanvas.Children.Add(sensor);
        }

        if (Vm.PlacingLidarSensor)
        {
            var hint = new TextBlock
            {
                Text = "Klick = Sensorposition setzen",
                Foreground = Brushes.Yellow,
                Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                FontSize = 12,
                Padding = new Thickness(6, 3, 6, 3)
            };
            Canvas.SetLeft(hint, 8);
            Canvas.SetTop(hint, 28);
            OverlayCanvas.Children.Add(hint);
        }

        // Layer content bounds on the map (after rotation)
        if (Vm.ShowLayerOverlays)
        {
            foreach (var layer in Vm.Layers)
            {
                if (!layer.IsEffectivelyVisible) continue;
                var ribbonRects = WallCarousel.GetRibbonOverlayRects(layer, Vm.Zones);
                var rects = ribbonRects.Count > 0
                    ? ribbonRects
                    : new[] { layer.GetMapBounds() };
                bool selected = ReferenceEquals(layer, Vm.SelectedLayer);
                bool labeled = false;
                foreach (var (bx, by, bw, bh) in rects)
                {
                    if (bw <= 0 || bh <= 0) continue;

                    var layerRect = new Rectangle
                    {
                        Width = bw * sx,
                        Height = bh * sy,
                        Stroke = selected ? Brushes.Yellow : Brushes.Lime,
                        StrokeThickness = selected ? 2.5 : 1.5,
                        StrokeDashArray = selected ? null : new DoubleCollection { 4, 2 },
                        Fill = new SolidColorBrush(Color.FromArgb(selected ? (byte)55 : (byte)35, 50, 255, 80))
                    };
                    Canvas.SetLeft(layerRect, bx * sx);
                    Canvas.SetTop(layerRect, by * sy);
                    OverlayCanvas.Children.Add(layerRect);

                    if (labeled)
                        continue;
                    labeled = true;
                    var layerLabel = new TextBlock
                    {
                        Text = layer.HasCrop
                            ? $"{layer.Name} ({layer.GetSourcePixelSize().Width}×{layer.GetSourcePixelSize().Height} crop)"
                            : $"{layer.Name} ({layer.NativeWidth}×{layer.NativeHeight})",
                        Foreground = Brushes.White,
                        Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
                        FontSize = 11,
                        Padding = new Thickness(3, 1, 3, 1)
                    };
                    Canvas.SetLeft(layerLabel, bx * sx + 4);
                    Canvas.SetTop(layerLabel, by * sy + 4);
                    OverlayCanvas.Children.Add(layerLabel);
                }
            }
        }
    }

    private void LayerTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is LayerTreeNode node)
            Vm.SelectedTreeNode = node;
    }

    private bool TryGetCanvasSize(out int w, out int h)
    {
        w = Vm.CanvasWidth;
        h = Vm.CanvasHeight;
        return w > 0 && h > 0;
    }

    private Point ToCanvasPixels(MouseEventArgs e)
    {
        var p = e.GetPosition(PreviewHost);
        if (!TryGetCanvasSize(out var cw, out var ch) || PreviewHost.ActualWidth <= 0)
            return p;

        double sx = cw / PreviewHost.ActualWidth;
        double sy = ch / PreviewHost.ActualHeight;
        return new Point(p.X * sx, p.Y * sy);
    }

    private void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        PreviewHost.CaptureMouse();
        PreviewHost.Focus();
        if (Vm.Room3DEnabled)
        {
            var p = e.GetPosition(PreviewHost);
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            Vm.OnRoom3DMouseDown(p, PreviewHost.ActualWidth, PreviewHost.ActualHeight,
                leftButton: true, rightButton: false, shift: shift);
        }
        else
        {
            Vm.OnPreviewMouseDown(ToCanvasPixels(e), startDrag: true);
        }
        DrawOverlays();
    }

    private void Preview_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!Vm.Room3DEnabled)
            return;
        PreviewHost.CaptureMouse();
        PreviewHost.Focus();
        var p = e.GetPosition(PreviewHost);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        Vm.OnRoom3DMouseDown(p, PreviewHost.ActualWidth, PreviewHost.ActualHeight,
            leftButton: false, rightButton: true, shift: shift);
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (Vm.Room3DEnabled)
        {
            if (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed)
            {
                var p = e.GetPosition(PreviewHost);
                Vm.OnRoom3DMouseMove(p, PreviewHost.ActualWidth, PreviewHost.ActualHeight);
            }
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            Vm.OnPreviewMouseMove(ToCanvasPixels(e));
            DrawOverlays();
        }
    }

    private void Preview_MouseLeftButtonUp(object sender, MouseEventArgs e)
    {
        PreviewHost.ReleaseMouseCapture();
        if (Vm.Room3DEnabled)
            Vm.OnRoom3DMouseUp();
        else
            Vm.OnPreviewMouseUp();
        DrawOverlays();
    }

    private void Preview_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        PreviewHost.ReleaseMouseCapture();
        if (Vm.Room3DEnabled)
            Vm.OnRoom3DMouseUp();
    }

    private void Preview_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm.Room3DEnabled)
        {
            PreviewHost.Focus();
            Vm.OnRoom3DMouseWheel(e.Delta);
            e.Handled = true;
        }
    }

    private void Preview_KeyDown(object sender, KeyEventArgs e)
    {
        if (!Vm.Room3DEnabled)
            return;
        Vm.OnRoom3DKeyDown(e.Key);
        if (e.Key == Key.R)
            e.Handled = true;
    }
}
