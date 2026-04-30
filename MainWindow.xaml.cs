using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;
using GRBL_Cam.ViewModels;

namespace GRBL_Cam;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private Point _lastOrbitPoint;
    private bool _isOrbiting;
    private double _cameraYawDegrees = -38;
    private double _cameraPitchDegrees = 28;
    private double _cameraDistance = 260;
    private Point3D _cameraTarget = new(0, 0, 0);
    private readonly DispatcherTimer _previewRefreshTimer;
    private bool _ignorePreviewInputChanges = true;
    private ToolLibraryWindow? _toolLibraryWindow;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        _previewRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _previewRefreshTimer.Tick += PreviewRefreshTimer_Tick;
        DataContext = _viewModel;
        Closing += MainWindow_Closing;
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        FitCameraToPreview();

        var startupInputTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        startupInputTimer.Tick += (_, _) =>
        {
            startupInputTimer.Stop();
            _ignorePreviewInputChanges = false;
        };
        startupInputTimer.Start();
    }

    private void ImportStepButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "CAD / Mesh Preview Files (*.step;*.stp;*.stl;*.obj)|*.step;*.stp;*.stl;*.obj|STEP Files (*.step;*.stp)|*.step;*.stp|Mesh Files (*.stl;*.obj)|*.stl;*.obj|All Files (*.*)|*.*",
            Title = "Select CAD model"
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.SetPartSourcePath(dialog.FileName);
            FitCameraToPreview();
        }
    }

    private void ImportStockButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Solid Files (*.step;*.stp;*.stl;*.obj)|*.step;*.stp;*.stl;*.obj|All Files (*.*)|*.*",
            Title = "Select stock solid"
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.SetStockSolidPath(dialog.FileName);
            FitCameraToPreview();
        }
    }

    private void RefreshPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _viewModel.RefreshPreview();
        FitCameraToPreview();
    }

    private void ResetViewButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        FitCameraToPreview();
    }

    private void ExportGCodeButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "G-code (*.nc;*.gcode;*.tap)|*.nc;*.gcode;*.tap|All Files (*.*)|*.*",
            FileName = $"{_viewModel.CurrentJob.Name}.nc",
            Title = "Export GRBL G-code"
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.ExportGCodeTo(dialog.FileName);
        }
    }

    private void OpenToolLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_toolLibraryWindow is not null)
        {
            _toolLibraryWindow.Activate();
            return;
        }

        _toolLibraryWindow = new ToolLibraryWindow(_viewModel)
        {
            Owner = this
        };
        _toolLibraryWindow.Closed += (_, _) => _toolLibraryWindow = null;
        _toolLibraryWindow.Show();
    }

    private void PreviewViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        if (_isOrbiting)
        {
            return;
        }

        var hits = HitTestPreview(e.GetPosition(PreviewViewport));
        foreach (var hit in hits.OfType<RayMeshGeometry3DHitTestResult>())
        {
            if (_viewModel.TrySelectSetupOriginFromPreview(hit.ModelHit, hit.PointHit))
            {
                e.Handled = true;
                return;
            }
        }

        foreach (var hit in hits)
        {
            if (hit.ModelHit is not null && _viewModel.TrySelectOperationFromPreview(hit.ModelHit))
            {
                e.Handled = true;
                return;
            }
        }

        foreach (var hit in hits.OfType<RayMeshGeometry3DHitTestResult>())
        {
            if (_viewModel.TrySelectFeatureFromPreview(hit.ModelHit, hit.PointHit))
            {
                e.Handled = true;
                return;
            }
        }
    }

    private IReadOnlyList<RayHitTestResult> HitTestPreview(Point point)
    {
        var hits = new List<RayHitTestResult>();
        VisualTreeHelper.HitTest(
            PreviewViewport,
            null,
            result =>
            {
                if (result is RayHitTestResult rayHit)
                {
                    hits.Add(rayHit);
                }

                return HitTestResultBehavior.Continue;
            },
            new PointHitTestParameters(point));

        return hits
            .OrderBy(hit => hit.DistanceToRayOrigin)
            .ToList();
    }

    private void PreviewViewport_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        _lastOrbitPoint = e.GetPosition(this);
        _isOrbiting = true;
        PreviewViewport.CaptureMouse();
        Mouse.OverrideCursor = Cursors.Hand;
    }

    private void PreviewViewport_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        _ = e;
        EndOrbit();
    }

    private void PreviewViewport_MouseMove(object sender, MouseEventArgs e)
    {
        _ = sender;
        if (!_isOrbiting)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        var delta = currentPoint - _lastOrbitPoint;
        _lastOrbitPoint = currentPoint;

        _cameraYawDegrees -= delta.X * 0.5;
        _cameraPitchDegrees = Math.Clamp(_cameraPitchDegrees + (delta.Y * 0.35), -89, 89);
        UpdateCamera();
    }

    private void PreviewViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _ = sender;
        var zoomFactor = e.Delta > 0 ? 0.88 : 1.12;
        _cameraDistance = Math.Clamp(_cameraDistance * zoomFactor, 30, 5000);
        UpdateCamera();
    }

    private void FitCameraToPreview()
    {
        var bounds = _viewModel.PreviewBounds;
        if (bounds.IsEmpty || bounds.SizeX <= 0 || bounds.SizeY <= 0 || bounds.SizeZ <= 0)
        {
            _cameraTarget = new Point3D(0, 0, 0);
            _cameraDistance = 260;
        }
        else
        {
            _cameraTarget = new Point3D(
                bounds.X + (bounds.SizeX / 2d),
                bounds.Y + (bounds.SizeY / 2d),
                bounds.Z + (bounds.SizeZ / 2d));

            var largest = Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
            var radius = Math.Max(largest / 2d, 12);
            var fovRadians = PreviewCamera.FieldOfView * Math.PI / 180d;
            _cameraDistance = Math.Clamp((radius / Math.Tan(fovRadians / 2d)) * 1.8, 50, 5000);
        }

        _cameraYawDegrees = -38;
        _cameraPitchDegrees = 28;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        var yaw = _cameraYawDegrees * Math.PI / 180d;
        var pitch = _cameraPitchDegrees * Math.PI / 180d;
        var offset = new Vector3D(
            _cameraDistance * Math.Cos(pitch) * Math.Cos(yaw),
            _cameraDistance * Math.Cos(pitch) * Math.Sin(yaw),
            _cameraDistance * Math.Sin(pitch));

        var position = _cameraTarget + offset;
        PreviewCamera.Position = position;
        PreviewCamera.LookDirection = _cameraTarget - position;
        PreviewCamera.UpDirection = new Vector3D(0, 0, 1);
        PreviewOverlayCamera.Position = position;
        PreviewOverlayCamera.LookDirection = _cameraTarget - position;
        PreviewOverlayCamera.UpDirection = new Vector3D(0, 0, 1);
    }

    private void EndOrbit()
    {
        _isOrbiting = false;
        PreviewViewport.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
    }

    private void PreviewEditingControl_Changed(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        QueuePreviewRefresh();
    }

    private void PreviewEditingControl_LostFocus(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Dispatcher.BeginInvoke(QueuePreviewRefresh, DispatcherPriority.Background);
    }

    private void PreviewRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _ = sender;
        _previewRefreshTimer.Stop();
        var previousBounds = _viewModel.PreviewBounds;
        _viewModel.NotifyPreviewInputsChanged();
        EnsureCameraCanSeePreview(previousBounds, _viewModel.PreviewBounds);
    }

    private void QueuePreviewRefresh()
    {
        if (!IsLoaded || _ignorePreviewInputChanges)
        {
            return;
        }

        _previewRefreshTimer.Stop();
        _previewRefreshTimer.Start();
    }

    private void EnsureCameraCanSeePreview(Rect3D previousBounds, Rect3D currentBounds)
    {
        if (currentBounds.IsEmpty || currentBounds.SizeX <= 0 || currentBounds.SizeY <= 0 || currentBounds.SizeZ <= 0)
        {
            return;
        }

        if (previousBounds.IsEmpty)
        {
            return;
        }

        var previousRadius = Math.Max(previousBounds.SizeX, Math.Max(previousBounds.SizeY, previousBounds.SizeZ)) / 2d;
        var currentRadius = Math.Max(currentBounds.SizeX, Math.Max(currentBounds.SizeY, currentBounds.SizeZ)) / 2d;
        var previousCenter = new Point3D(
            previousBounds.X + (previousBounds.SizeX / 2d),
            previousBounds.Y + (previousBounds.SizeY / 2d),
            previousBounds.Z + (previousBounds.SizeZ / 2d));
        var currentCenter = new Point3D(
            currentBounds.X + (currentBounds.SizeX / 2d),
            currentBounds.Y + (currentBounds.SizeY / 2d),
            currentBounds.Z + (currentBounds.SizeZ / 2d));

        var centerShift = (currentCenter - previousCenter).Length;
        var geometryExpanded = currentRadius > previousRadius * 1.05;
        var geometryMoved = centerShift > Math.Max(previousRadius * 0.35, 4d);

        if (!geometryExpanded && !geometryMoved)
        {
            return;
        }

        var fovRadians = PreviewCamera.FieldOfView * Math.PI / 180d;
        _cameraTarget = currentCenter;
        if (geometryExpanded)
        {
            _cameraDistance = Math.Max(_cameraDistance, (currentRadius / Math.Tan(fovRadians / 2d)) * 1.8);
        }

        UpdateCamera();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _ = sender;
        _ = e;
        _previewRefreshTimer.Stop();
        EndOrbit();
        _toolLibraryWindow?.Close();
        _viewModel.SaveState();
    }
}
