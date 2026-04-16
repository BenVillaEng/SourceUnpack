using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SourceUnpack.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    // ── 3D Orbit Camera State ──
    private bool _isDragging;
    private System.Windows.Point _lastMousePos;
    private double _camYaw = 0;
    private double _camPitch = 20;
    private double _camDist = 80;
    private Point3D _camTarget = new(0, 0, 0);

    // ── Easter Egg: Konami Code ──
    private readonly Key[] _konamiSequence = {
        Key.Up, Key.Up, Key.Down, Key.Down,
        Key.Left, Key.Right, Key.Left, Key.Right,
        Key.B, Key.A
    };
    private int _konamiIndex = 0;

    // ── Easter Egg: Title Triple-Click ──
    private int _titleClickCount = 0;
    private DateTime _lastTitleClick = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    // ── Drag & Drop ──

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = System.Windows.DragDropEffects.None;
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
            if (files.Length > 0)
            {
                string ext = System.IO.Path.GetExtension(files[0]).ToLowerInvariant();
                if (ext is ".bsp" or ".vpk" or ".gma" or ".mdl" or ".vtf")
                    e.Effects = System.Windows.DragDropEffects.Copy;
            }
        }
        e.Handled = true;
    }

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
        if (files.Length > 0 && DataContext is ViewModels.MainViewModel vm)
        {
            vm.HandleFileDrop(files[0]);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsWin = new SettingsWindow();
        settingsWin.Owner = this;
        settingsWin.DataContext = this.DataContext;
        settingsWin.ShowDialog();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            // ── Easter Egg: Triple-click title ──
            var now = DateTime.Now;
            if ((now - _lastTitleClick).TotalMilliseconds < 400)
            {
                _titleClickCount++;
                if (_titleClickCount >= 3)
                {
                    _titleClickCount = 0;
                    ShowXenTooltip();
                    return; // don't drag on triple-click
                }
            }
            else
            {
                _titleClickCount = 1;
            }
            _lastTitleClick = now;

            DragMove();
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm)
        {
            vm.StopSteamCmdCommand.Execute(null);
        }
    }

    private void TreeView_SelectedItemChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is ViewModels.MainViewModel vm && e.NewValue is ViewModels.AssetTreeNode node)
        {
            vm.SelectedNode = node;
        }
    }

    // ── 3D Viewport Mouse Handlers ──

    private void ModelViewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _lastMousePos = e.GetPosition(ModelViewport);
        ModelViewport.CaptureMouse();
    }

    private void ModelViewport_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDragging) return;

        var pos = e.GetPosition(ModelViewport);
        double dx = pos.X - _lastMousePos.X;
        double dy = pos.Y - _lastMousePos.Y;
        _lastMousePos = pos;

        _camYaw += dx * 0.5;
        _camPitch += dy * 0.5;
        _camPitch = Math.Clamp(_camPitch, -89, 89);

        UpdateCamera();
    }

    private void ModelViewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        ModelViewport.ReleaseMouseCapture();
    }

    private void ModelViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _camDist -= e.Delta * 0.1;
        _camDist = Math.Clamp(_camDist, 1, 10000);
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        double yawRad = _camYaw * Math.PI / 180;
        double pitchRad = _camPitch * Math.PI / 180;

        double x = _camDist * Math.Cos(pitchRad) * Math.Sin(yawRad);
        double y = _camDist * Math.Sin(pitchRad);
        double z = _camDist * Math.Cos(pitchRad) * Math.Cos(yawRad);

        var camPos = new Point3D(_camTarget.X + x, _camTarget.Y + y, _camTarget.Z + z);
        var lookDir = new Vector3D(_camTarget.X - camPos.X, _camTarget.Y - camPos.Y, _camTarget.Z - camPos.Z);

        ModelCamera.Position = camPos;
        ModelCamera.LookDirection = lookDir;
    }

    /// <summary>
    /// Called by ViewModel to set auto-center camera for a new model.
    /// </summary>
    public void SetCameraTarget(Point3D center, double radius)
    {
        _camTarget = center;
        _camDist = radius * 2.5;
        _camYaw = 30;
        _camPitch = 20;
        UpdateCamera();
    }

    // ══════════════════════════════════════
    // ██  EASTER EGGS
    // ══════════════════════════════════════

    /// <summary>
    /// Konami Code: ↑↑↓↓←→←→BA — shows a G-Man quote overlay.
    /// </summary>
    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == _konamiSequence[_konamiIndex])
        {
            _konamiIndex++;
            if (_konamiIndex >= _konamiSequence.Length)
            {
                _konamiIndex = 0;
                ShowGmanEasterEgg();
            }
        }
        else
        {
            _konamiIndex = e.Key == _konamiSequence[0] ? 1 : 0;
        }
    }

    private void ShowGmanEasterEgg()
    {
        // Create an overlay with the G-Man quote
        var overlay = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(230, 15, 15, 15)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0
        };

        var stack = new StackPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Lambda symbol (λ)
        stack.Children.Add(new TextBlock
        {
            Text = "λ",
            FontSize = 120,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 150, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 20)
        });

        // G-Man quote
        stack.Children.Add(new TextBlock
        {
            Text = "\"The right man in the wrong place\ncan make all the difference in the world.\"",
            FontSize = 16,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 200, 200)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(0, 0, 0, 10)
        });

        stack.Children.Add(new TextBlock
        {
            Text = "— G-Man",
            FontSize = 12,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(120, 120, 120)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 30)
        });

        stack.Children.Add(new TextBlock
        {
            Text = "☢ RESONANCE CASCADE DETECTED ☢",
            FontSize = 10,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 80, 80)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            FontWeight = FontWeights.Bold
        });

        overlay.Child = stack;

        // Add to the root grid
        var rootGrid = (Grid)((Border)Content).Child;
        Grid.SetRowSpan(overlay, 10);
        rootGrid.Children.Add(overlay);

        // Animate fade in
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500));
        overlay.BeginAnimation(OpacityProperty, fadeIn);

        // Auto-dismiss after 3 seconds
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (s, _) =>
        {
            timer.Stop();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(800));
            fadeOut.Completed += (_, _) => rootGrid.Children.Remove(overlay);
            overlay.BeginAnimation(OpacityProperty, fadeOut);
        };
        timer.Start();
    }

    /// <summary>
    /// Triple-click title bar — shows a brief Xen crystal tooltip.
    /// </summary>
    private void ShowXenTooltip()
    {
        var popup = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 30, 30, 30)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 150, 0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 32, 0, 0),
            Opacity = 0
        };

        popup.Child = new TextBlock
        {
            Text = "⚡ Runs on Xen crystal energy ⚡",
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 200, 100)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        };

        var rootGrid = (Grid)((Border)Content).Child;
        Grid.SetRowSpan(popup, 10);
        rootGrid.Children.Add(popup);

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));
        popup.BeginAnimation(OpacityProperty, fadeIn);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (s, _) =>
        {
            timer.Stop();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500));
            fadeOut.Completed += (_, _) => rootGrid.Children.Remove(popup);
            popup.BeginAnimation(OpacityProperty, fadeOut);
        };
        timer.Start();
    }
}