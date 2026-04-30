using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using GRBL_Cam.Models;
using GRBL_Cam.ViewModels;

namespace GRBL_Cam;

public partial class ToolLibraryWindow : Window
{
    private readonly DispatcherTimer _previewRefreshTimer;
    private MainViewModel? _subscribedViewModel;

    public ToolLibraryWindow()
    {
        InitializeComponent();
        StyleColumn.ItemsSource = Enum.GetValues(typeof(ToolStyle));
        _previewRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _previewRefreshTimer.Tick += PreviewRefreshTimer_Tick;
        DataContextChanged += ToolLibraryWindow_DataContextChanged;
        Loaded += (_, _) => RefreshToolPreview();
    }

    public ToolLibraryWindow(MainViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void ToolLibraryWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _ = sender;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        _subscribedViewModel = e.NewValue as MainViewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        QueueToolPreviewRefresh();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName == nameof(MainViewModel.SelectedTool))
        {
            QueueToolPreviewRefresh();
        }
    }

    private void ToolGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        QueueToolPreviewRefresh();
    }

    private void ToolEditingControl_Changed(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        QueueToolPreviewRefresh();
    }

    private void PreviewRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _previewRefreshTimer.Stop();
        RefreshToolPreview();
    }

    private void QueueToolPreviewRefresh()
    {
        if (!IsLoaded)
        {
            return;
        }

        _previewRefreshTimer.Stop();
        _previewRefreshTimer.Start();
    }

    private void RefreshToolPreview()
    {
        var tool = ViewModel?.SelectedTool;
        if (tool is null)
        {
            ToolPreviewVisual.Content = CreateEmptyPreviewScene();
            ToolPreviewSummary.Text = "Select a tool to preview its cutting shape.";
            FitToolCamera(20, 60, 20);
            return;
        }

        ToolPreviewVisual.Content = CreateToolPreviewScene(tool);
        var diameter = Math.Max(tool.CuttingDiameter, 0);
        var cutLength = Math.Max(tool.CuttingLength, tool.FluteLength);
        ToolPreviewSummary.Text =
            $"T{tool.Number} {tool.Name}\n" +
            $"{tool.Style} | Dia {diameter:0.###} | {tool.FluteCount} flute(s)\n" +
            $"Flute {tool.FluteLength:0.###}, Cut {cutLength:0.###}, Stickout {tool.StickOut:0.###}\n" +
            $"Corner R {tool.CornerRadius:0.###}, Tip Dia {tool.TipDiameter:0.###}, Tip Angle {tool.TipAngleDegrees:0.###}";
        FitToolCamera(Math.Max(diameter, tool.ShankDiameter), Math.Max(tool.StickOut, cutLength + 20), Math.Max(cutLength, tool.FluteLength));
    }

    private static Model3DGroup CreateEmptyPreviewScene()
    {
        var scene = new Model3DGroup();
        AddLights(scene);
        return scene;
    }

    private static Model3DGroup CreateToolPreviewScene(ToolDefinition tool)
    {
        var scene = new Model3DGroup();
        AddLights(scene);

        var cuttingDiameter = Math.Max(tool.CuttingDiameter, 0.8);
        var toolRadius = cuttingDiameter / 2d;
        var cuttingLength = Math.Max(Math.Max(tool.CuttingLength, tool.FluteLength), Math.Max(cuttingDiameter, 3));
        var fluteLength = Math.Clamp(tool.FluteLength > 0 ? tool.FluteLength : cuttingLength, 0, cuttingLength);
        var shankRadius = Math.Max(tool.ShankDiameter > 0 ? tool.ShankDiameter : cuttingDiameter, cuttingDiameter) / 2d;
        var shankEndZ = Math.Max(tool.StickOut, cuttingLength + Math.Max(cuttingDiameter, 10));
        var cuttingColor = Color.FromRgb(255, 213, 69);
        var shankColor = Color.FromRgb(210, 164, 57);

        AddCuttingShape(scene, tool, cuttingLength, cuttingColor);
        AddFluteGuides(scene, tool, fluteLength, toolRadius);

        scene.Children.Add(CreateCylinderModel(
            0,
            0,
            cuttingLength,
            shankEndZ,
            shankRadius,
            shankColor,
            0.95,
            44));

        AddReferenceAxes(scene, Math.Max(cuttingDiameter * 2.2, 12), shankEndZ);
        return scene;
    }

    private static void AddCuttingShape(Model3DGroup scene, ToolDefinition tool, double cuttingLength, Color color)
    {
        var radius = Math.Max(tool.CuttingDiameter, 0.8) / 2d;
        var tipRadius = Math.Clamp(tool.TipDiameter, 0, Math.Max(tool.CuttingDiameter, 0.8)) / 2d;

        switch (tool.Style)
        {
            case ToolStyle.Ball:
                scene.Children.Add(CreateHemisphereModel(0, 0, radius, radius, lowerHalf: true, color, 0.98));
                if (cuttingLength > radius)
                {
                    scene.Children.Add(CreateCylinderModel(0, 0, radius, cuttingLength, radius, color, 0.98, 44));
                }

                break;
            case ToolStyle.Bull:
                var cornerRadius = Math.Clamp(tool.CornerRadius > 0 ? tool.CornerRadius : radius * 0.18, 0, radius);
                scene.Children.Add(CreateCylinderModel(0, 0, cornerRadius, cuttingLength, radius, color, 0.98, 44));
                scene.Children.Add(CreateCylinderModel(0, 0, 0, cornerRadius, Math.Max(radius - cornerRadius, radius * 0.35), color, 0.98, 44));
                scene.Children.Add(CreateCylinderModel(0, 0, cornerRadius * 0.85, cornerRadius * 1.05, radius, Color.FromRgb(255, 238, 132), 0.98, 44));
                break;
            case ToolStyle.Drill:
            case ToolStyle.SpotDrill:
            case ToolStyle.CenterDrill:
            case ToolStyle.VPoint:
            case ToolStyle.VBit:
            case ToolStyle.Chamfer:
            case ToolStyle.Engraver:
                var tipLength = Math.Clamp(GetTipLength(tool), radius * 0.2, cuttingLength);
                scene.Children.Add(CreateFrustumModel(0, 0, 0, tipLength, tipRadius, radius, color, 0.98, 44));
                if (cuttingLength > tipLength + 0.001)
                {
                    scene.Children.Add(CreateCylinderModel(0, 0, tipLength, cuttingLength, radius, color, 0.98, 44));
                }

                break;
            case ToolStyle.Taper:
            case ToolStyle.Dovetail:
                var smallRadius = tipRadius > 0 ? tipRadius : Math.Max(radius * 0.35, 0.2);
                scene.Children.Add(CreateFrustumModel(0, 0, 0, cuttingLength, smallRadius, radius, color, 0.98, 44));
                break;
            case ToolStyle.Lollipop:
                var neckRadius = Math.Max(tool.NeckDiameter > 0 ? tool.NeckDiameter / 2d : radius * 0.35, 0.2);
                var headCenterZ = radius;
                var neckStartZ = radius * 2d;
                var requestedNeckLength = tool.NeckLength > 0 ? tool.NeckLength : cuttingLength - neckStartZ;
                var neckEndZ = Math.Min(cuttingLength, neckStartZ + Math.Max(requestedNeckLength, 0));
                scene.Children.Add(CreateSphereModel(0, 0, headCenterZ, radius, color, 0.98));
                if (neckEndZ > neckStartZ + 0.001)
                {
                    scene.Children.Add(CreateCylinderModel(0, 0, neckStartZ, neckEndZ, neckRadius, color, 0.98, 36));
                }

                break;
            default:
                scene.Children.Add(CreateCylinderModel(0, 0, 0, cuttingLength, radius, color, 0.98, 44));
                break;
        }
    }

    private static void AddFluteGuides(Model3DGroup scene, ToolDefinition tool, double fluteLength, double radius)
    {
        var fluteCount = Math.Clamp(tool.FluteCount, 1, 12);
        if (fluteLength <= 0.001)
        {
            return;
        }

        var turns = Math.Clamp(fluteLength / Math.Max(radius * 6d, 1d), 0.55, 2.4);
        var samples = 26;
        var guideRadius = radius * 1.018;
        var thickness = Math.Clamp(radius * 0.06, 0.035, 0.18);
        for (var flute = 0; flute < fluteCount; flute++)
        {
            var startAngle = (Math.PI * 2d * flute) / fluteCount;
            var previous = GetHelixPoint(startAngle, turns, guideRadius, fluteLength, 0);
            for (var index = 1; index <= samples; index++)
            {
                var t = index / (double)samples;
                var next = GetHelixPoint(startAngle, turns, guideRadius, fluteLength, t);
                scene.Children.Add(CreateSegmentModel(previous, next, thickness, Color.FromRgb(124, 92, 24), 0.95));
                previous = next;
            }
        }
    }

    private static Point3D GetHelixPoint(double startAngle, double turns, double radius, double length, double t)
    {
        var angle = startAngle + (turns * Math.PI * 2d * t);
        return new Point3D(Math.Cos(angle) * radius, Math.Sin(angle) * radius, length * t);
    }

    private static double GetTipLength(ToolDefinition tool)
    {
        var includedAngle = tool.TipAngleDegrees > 0 ? tool.TipAngleDegrees : tool.TaperAngleDegrees;
        if (includedAngle <= 0 || includedAngle >= 179)
        {
            return Math.Max(tool.CuttingDiameter, 0.8) / 2d;
        }

        var majorRadius = Math.Max(tool.CuttingDiameter, 0.8) / 2d;
        var tipRadius = Math.Clamp(tool.TipDiameter, 0, Math.Max(tool.CuttingDiameter, 0.8)) / 2d;
        return Math.Max(0.2, (majorRadius - tipRadius) / Math.Tan((includedAngle / 2d) * Math.PI / 180d));
    }

    private void FitToolCamera(double diameter, double height, double workingLength)
    {
        var safeDiameter = Math.Max(diameter, 8);
        var safeHeight = Math.Max(height, 30);
        var targetZ = Math.Clamp(Math.Max(workingLength, safeHeight * 0.28), safeHeight * 0.24, safeHeight * 0.46);
        var target = new Point3D(0, 0, targetZ);
        var verticalSpan = safeHeight + Math.Max(safeDiameter * 2d, 10);
        var distance = Math.Max(safeDiameter * 6.2, verticalSpan * 2.2);
        var position = target + new Vector3D(distance * 0.62, -distance * 0.88, distance * 0.36);
        ToolPreviewCamera.Position = position;
        ToolPreviewCamera.LookDirection = target - position;
        ToolPreviewCamera.UpDirection = new Vector3D(0, 0, 1);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        _previewRefreshTimer.Stop();
        base.OnClosed(e);
    }

    private static void AddLights(Model3DGroup scene)
    {
        scene.Children.Add(new AmbientLight(Color.FromRgb(84, 88, 96)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(238, 238, 232), new Vector3D(-0.45, 0.35, -1)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(150, 170, 190), new Vector3D(0.65, -0.8, -0.25)));
    }

    private static void AddReferenceAxes(Model3DGroup scene, double spread, double height)
    {
        var thickness = Math.Clamp(spread * 0.012, 0.04, 0.18);
        scene.Children.Add(CreateSegmentModel(new Point3D(-spread, 0, 0), new Point3D(spread, 0, 0), thickness, Color.FromRgb(226, 91, 77), 0.9));
        scene.Children.Add(CreateSegmentModel(new Point3D(0, -spread, 0), new Point3D(0, spread, 0), thickness, Color.FromRgb(73, 204, 111), 0.9));
        scene.Children.Add(CreateSegmentModel(new Point3D(0, 0, 0), new Point3D(0, 0, height), thickness, Color.FromRgb(91, 168, 255), 0.9));
    }

    private static GeometryModel3D CreateCylinderModel(
        double centerX,
        double centerY,
        double bottomZ,
        double topZ,
        double radius,
        Color color,
        double opacity,
        int segments)
    {
        var mesh = new MeshGeometry3D();
        var topCenter = new Point3D(centerX, centerY, topZ);
        var bottomCenter = new Point3D(centerX, centerY, bottomZ);

        for (var segment = 0; segment < segments; segment++)
        {
            var angle0 = (Math.PI * 2 * segment) / segments;
            var angle1 = (Math.PI * 2 * (segment + 1)) / segments;
            var normal0 = new Vector3D(Math.Cos(angle0), Math.Sin(angle0), 0);
            var normal1 = new Vector3D(Math.Cos(angle1), Math.Sin(angle1), 0);
            var p0 = new Point3D(centerX + (Math.Cos(angle0) * radius), centerY + (Math.Sin(angle0) * radius), bottomZ);
            var p1 = new Point3D(centerX + (Math.Cos(angle1) * radius), centerY + (Math.Sin(angle1) * radius), bottomZ);
            var p2 = new Point3D(centerX + (Math.Cos(angle1) * radius), centerY + (Math.Sin(angle1) * radius), topZ);
            var p3 = new Point3D(centerX + (Math.Cos(angle0) * radius), centerY + (Math.Sin(angle0) * radius), topZ);

            AddQuad(mesh, p0, p1, p2, p3, normal0, normal1);
            AddTriangle(mesh, topCenter, p3, p2, new Vector3D(0, 0, 1));
            AddTriangle(mesh, bottomCenter, p1, p0, new Vector3D(0, 0, -1));
        }

        return CreateGeometryModel(mesh, color, opacity);
    }

    private static GeometryModel3D CreateFrustumModel(
        double centerX,
        double centerY,
        double bottomZ,
        double topZ,
        double bottomRadius,
        double topRadius,
        Color color,
        double opacity,
        int segments)
    {
        var mesh = new MeshGeometry3D();
        var safeBottomRadius = Math.Max(bottomRadius, 0);
        var safeTopRadius = Math.Max(topRadius, 0);
        var topCenter = new Point3D(centerX, centerY, topZ);
        var bottomCenter = new Point3D(centerX, centerY, bottomZ);

        for (var segment = 0; segment < segments; segment++)
        {
            var angle0 = (Math.PI * 2 * segment) / segments;
            var angle1 = (Math.PI * 2 * (segment + 1)) / segments;
            var normal0 = new Vector3D(Math.Cos(angle0), Math.Sin(angle0), 0);
            var normal1 = new Vector3D(Math.Cos(angle1), Math.Sin(angle1), 0);
            var p0 = new Point3D(centerX + (Math.Cos(angle0) * safeBottomRadius), centerY + (Math.Sin(angle0) * safeBottomRadius), bottomZ);
            var p1 = new Point3D(centerX + (Math.Cos(angle1) * safeBottomRadius), centerY + (Math.Sin(angle1) * safeBottomRadius), bottomZ);
            var p2 = new Point3D(centerX + (Math.Cos(angle1) * safeTopRadius), centerY + (Math.Sin(angle1) * safeTopRadius), topZ);
            var p3 = new Point3D(centerX + (Math.Cos(angle0) * safeTopRadius), centerY + (Math.Sin(angle0) * safeTopRadius), topZ);

            AddQuad(mesh, p0, p1, p2, p3, normal0, normal1);
            if (safeTopRadius > 0.0001)
            {
                AddTriangle(mesh, topCenter, p3, p2, new Vector3D(0, 0, 1));
            }

            if (safeBottomRadius > 0.0001)
            {
                AddTriangle(mesh, bottomCenter, p1, p0, new Vector3D(0, 0, -1));
            }
        }

        return CreateGeometryModel(mesh, color, opacity);
    }

    private static GeometryModel3D CreateSphereModel(double centerX, double centerY, double centerZ, double radius, Color color, double opacity)
    {
        return CreateSphericalSectionModel(centerX, centerY, centerZ, radius, 0, Math.PI, color, opacity);
    }

    private static GeometryModel3D CreateHemisphereModel(double centerX, double centerY, double centerZ, double radius, bool lowerHalf, Color color, double opacity)
    {
        return lowerHalf
            ? CreateSphericalSectionModel(centerX, centerY, centerZ, radius, Math.PI / 2d, Math.PI, color, opacity)
            : CreateSphericalSectionModel(centerX, centerY, centerZ, radius, 0, Math.PI / 2d, color, opacity);
    }

    private static GeometryModel3D CreateSphericalSectionModel(double centerX, double centerY, double centerZ, double radius, double thetaStart, double thetaEnd, Color color, double opacity)
    {
        const int segments = 36;
        const int stacks = 14;
        var mesh = new MeshGeometry3D();
        var indices = new int[stacks + 1, segments];

        for (var stack = 0; stack <= stacks; stack++)
        {
            var theta = Lerp(thetaStart, thetaEnd, stack / (double)stacks);
            var ringRadius = Math.Sin(theta) * radius;
            var z = centerZ + (Math.Cos(theta) * radius);
            for (var segment = 0; segment < segments; segment++)
            {
                var angle = (Math.PI * 2d * segment) / segments;
                var normal = new Vector3D(Math.Sin(theta) * Math.Cos(angle), Math.Sin(theta) * Math.Sin(angle), Math.Cos(theta));
                if (normal.LengthSquared > 0.000001)
                {
                    normal.Normalize();
                }

                indices[stack, segment] = mesh.Positions.Count;
                mesh.Positions.Add(new Point3D(centerX + (Math.Cos(angle) * ringRadius), centerY + (Math.Sin(angle) * ringRadius), z));
                mesh.Normals.Add(normal);
            }
        }

        for (var stack = 0; stack < stacks; stack++)
        {
            for (var segment = 0; segment < segments; segment++)
            {
                var next = (segment + 1) % segments;
                mesh.TriangleIndices.Add(indices[stack, segment]);
                mesh.TriangleIndices.Add(indices[stack + 1, segment]);
                mesh.TriangleIndices.Add(indices[stack + 1, next]);
                mesh.TriangleIndices.Add(indices[stack, segment]);
                mesh.TriangleIndices.Add(indices[stack + 1, next]);
                mesh.TriangleIndices.Add(indices[stack, next]);
            }
        }

        return CreateGeometryModel(mesh, color, opacity);
    }

    private static GeometryModel3D CreateSegmentModel(Point3D start, Point3D end, double thickness, Color color, double opacity)
    {
        var direction = end - start;
        if (direction.LengthSquared < 0.0000001)
        {
            return new GeometryModel3D();
        }

        direction.Normalize();
        var reference = Math.Abs(Vector3D.DotProduct(direction, new Vector3D(0, 0, 1))) > 0.92
            ? new Vector3D(1, 0, 0)
            : new Vector3D(0, 0, 1);
        var side1 = Vector3D.CrossProduct(direction, reference);
        side1.Normalize();
        side1 *= thickness / 2d;
        var side2 = Vector3D.CrossProduct(direction, side1);
        side2.Normalize();
        side2 *= thickness / 2d;

        var p0 = start - side1 - side2;
        var p1 = start + side1 - side2;
        var p2 = start + side1 + side2;
        var p3 = start - side1 + side2;
        var p4 = end - side1 - side2;
        var p5 = end + side1 - side2;
        var p6 = end + side1 + side2;
        var p7 = end - side1 + side2;
        var mesh = new MeshGeometry3D();
        AddQuad(mesh, p0, p1, p5, p4, CalculateNormal(p0, p1, p5));
        AddQuad(mesh, p1, p2, p6, p5, CalculateNormal(p1, p2, p6));
        AddQuad(mesh, p2, p3, p7, p6, CalculateNormal(p2, p3, p7));
        AddQuad(mesh, p3, p0, p4, p7, CalculateNormal(p3, p0, p4));
        AddQuad(mesh, p4, p5, p6, p7, direction);
        AddQuad(mesh, p3, p2, p1, p0, -direction);
        return CreateGeometryModel(mesh, color, opacity);
    }

    private static GeometryModel3D CreateGeometryModel(MeshGeometry3D mesh, Color color, double opacity)
    {
        var alpha = (byte)(Math.Clamp(opacity, 0.05, 1.0) * 255);
        var diffuseBrush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        diffuseBrush.Freeze();
        var highlightBrush = new SolidColorBrush(Color.FromArgb(Math.Min((byte)255, (byte)(alpha + 20)), 255, 255, 255));
        highlightBrush.Freeze();
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(diffuseBrush));
        material.Children.Add(new SpecularMaterial(highlightBrush, 46));
        return new GeometryModel3D
        {
            Geometry = mesh,
            Material = material,
            BackMaterial = material
        };
    }

    private static void AddQuad(MeshGeometry3D mesh, Point3D p0, Point3D p1, Point3D p2, Point3D p3, Vector3D normal)
    {
        AddQuad(mesh, p0, p1, p2, p3, normal, normal);
    }

    private static void AddQuad(MeshGeometry3D mesh, Point3D p0, Point3D p1, Point3D p2, Point3D p3, Vector3D normal0, Vector3D normal1)
    {
        var start = mesh.Positions.Count;
        mesh.Positions.Add(p0);
        mesh.Positions.Add(p1);
        mesh.Positions.Add(p2);
        mesh.Positions.Add(p3);
        mesh.Normals.Add(normal0);
        mesh.Normals.Add(normal1);
        mesh.Normals.Add(normal1);
        mesh.Normals.Add(normal0);
        mesh.TriangleIndices.Add(start);
        mesh.TriangleIndices.Add(start + 1);
        mesh.TriangleIndices.Add(start + 2);
        mesh.TriangleIndices.Add(start);
        mesh.TriangleIndices.Add(start + 2);
        mesh.TriangleIndices.Add(start + 3);
    }

    private static void AddTriangle(MeshGeometry3D mesh, Point3D p0, Point3D p1, Point3D p2, Vector3D normal)
    {
        var start = mesh.Positions.Count;
        mesh.Positions.Add(p0);
        mesh.Positions.Add(p1);
        mesh.Positions.Add(p2);
        mesh.Normals.Add(normal);
        mesh.Normals.Add(normal);
        mesh.Normals.Add(normal);
        mesh.TriangleIndices.Add(start);
        mesh.TriangleIndices.Add(start + 1);
        mesh.TriangleIndices.Add(start + 2);
    }

    private static Vector3D CalculateNormal(Point3D a, Point3D b, Point3D c)
    {
        var normal = Vector3D.CrossProduct(b - a, c - a);
        if (normal.LengthSquared < 0.0000001)
        {
            return new Vector3D(0, 0, 1);
        }

        normal.Normalize();
        return normal;
    }

    private static double Lerp(double start, double end, double amount)
    {
        return start + ((end - start) * amount);
    }
}
