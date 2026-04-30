using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows;
using System.IO;
using GRBL_Cam.Models;

namespace GRBL_Cam.Services;

public sealed class JobPreviewSceneData
{
    public Model3DGroup Scene { get; init; } = new();

    public Model3DGroup OverlayScene { get; init; } = new();

    public Rect3D Bounds { get; init; }

    public IReadOnlyDictionary<Model3D, ToolpathOperationDefinition> SelectableModels { get; init; }
        = new Dictionary<Model3D, ToolpathOperationDefinition>();

    public IReadOnlySet<Model3D> FeatureSelectableModels { get; init; }
        = new HashSet<Model3D>();

    public IReadOnlyDictionary<Model3D, PreviewEdgeGeometry> EdgeSelectableModels { get; init; }
        = new Dictionary<Model3D, PreviewEdgeGeometry>();

    public IReadOnlyDictionary<Model3D, SetupOriginSource> OriginSelectableModels { get; init; }
        = new Dictionary<Model3D, SetupOriginSource>();

    public IReadOnlyList<PreviewEdgeGeometry> EdgeGeometries { get; init; } = Array.Empty<PreviewEdgeGeometry>();

    public Rect3D PartBounds { get; init; }

    public Rect3D StockBounds { get; init; }

    public double StockTopZ { get; init; }

    public string Summary { get; init; } = "3D preview ready.";
}

public sealed class ToolpathPlaybackSnapshot
{
    public bool IsActive { get; init; }

    public double SegmentPosition { get; init; }
}

public sealed class JobPreviewSceneBuilder
{
    private readonly PreviewGeometryLoader _geometryLoader = new();

    public JobPreviewSceneData Build(
        CamJob job,
        MachineProfile? machine,
        ToolpathOperationDefinition? selectedOperation,
        IReadOnlyList<OperationToolpath>? toolpaths = null,
        ToolpathPlaybackSnapshot? playback = null)
    {
        toolpaths ??= Array.Empty<OperationToolpath>();
        var setup = job.Setup;
        var previewSetup = BuildIndexedPreviewSetup(setup, selectedOperation, machine);
        var activeToolpaths = toolpaths
            .Where(toolpath => ReferenceEquals(toolpath.Setup, setup) || setup.Operations.Contains(toolpath.Operation))
            .ToList();
        var partGeometry = _geometryLoader.TryLoadPartGeometry(previewSetup);
        var stockGeometry = previewSetup.Stock.ShowInPreview ? _geometryLoader.TryLoadStockGeometry(previewSetup) : null;
        var stockPlacement = GetStockPlacement(previewSetup, partGeometry);
        var playbackSegments = BuildPlaybackSegments(previewSetup, stockPlacement, activeToolpaths);
        var isPlaybackActive = playback?.IsActive == true && playbackSegments.Count > 0;
        var partBounds = partGeometry?.HasRenderableGeometry == true ? partGeometry.Bounds : GetFallbackPartBounds(previewSetup);
        var stockBounds = GetPreviewStockBounds(previewSetup, partGeometry, stockGeometry);
        var bounds = GetSceneBounds(previewSetup, partGeometry, stockGeometry, activeToolpaths);
        var scene = new Model3DGroup();
        var overlayScene = new Model3DGroup();
        var selectableModels = new Dictionary<Model3D, ToolpathOperationDefinition>();
        var featureSelectableModels = new HashSet<Model3D>();
        var edgeSelectableModels = new Dictionary<Model3D, PreviewEdgeGeometry>();
        var originSelectableModels = new Dictionary<Model3D, SetupOriginSource>();

        AddLights(scene);
        AddLights(overlayScene);
        if (partGeometry?.HasRenderableGeometry == true)
        {
            AddToolpathPreview(scene, previewSetup, stockPlacement, activeToolpaths, selectedOperation, bounds, opacityScale: 0.24, thicknessScale: 0.72);
        }

        AddPart(scene, previewSetup, partGeometry, featureSelectableModels, edgeSelectableModels, originSelectableModels);
        if (isPlaybackActive)
        {
            AddPlaybackStock(scene, previewSetup, stockBounds, playbackSegments, playback!);
        }
        else
        {
            AddStock(scene, previewSetup, partGeometry, stockGeometry, originSelectableModels);
        }

        if (partGeometry?.HasRenderableGeometry != true)
        {
            AddFeatureVolumes(scene, selectableModels, previewSetup, setup.Operations, selectedOperation);
        }

        AddSetupOriginMarker(overlayScene, previewSetup, bounds);
        AddSelectedFeatureReference(overlayScene, previewSetup, stockPlacement, selectedOperation, bounds);
        var visibleToolpathSegments = AddToolpathPreview(scene, previewSetup, stockPlacement, activeToolpaths, selectedOperation, bounds);
        if (isPlaybackActive)
        {
            AddPlaybackToolMarker(scene, playbackSegments, playback!, bounds);
        }

        return new JobPreviewSceneData
        {
            Scene = scene,
            OverlayScene = overlayScene,
            Bounds = bounds,
            SelectableModels = selectableModels,
            FeatureSelectableModels = featureSelectableModels,
            EdgeSelectableModels = edgeSelectableModels,
            OriginSelectableModels = originSelectableModels,
            EdgeGeometries = partGeometry?.EdgeGeometries ?? Array.Empty<PreviewEdgeGeometry>(),
            PartBounds = partBounds,
            StockBounds = stockBounds,
            StockTopZ = stockPlacement.TopZ,
            Summary = BuildSummary(job, previewSetup, selectedOperation, partGeometry, stockGeometry, activeToolpaths, visibleToolpathSegments)
        };
    }

    private static JobSetup BuildIndexedPreviewSetup(JobSetup setup, ToolpathOperationDefinition? selectedOperation, MachineProfile? machine)
    {
        var operationIndexA = selectedOperation?.RotaryIndexA ?? 0;
        var operationIndexB = selectedOperation?.RotaryIndexB ?? 0;
        var partRotation = ResolvePreviewRotaryAngles(machine, setup.Part.RotationA + operationIndexA, setup.Part.RotationB + operationIndexB);
        var stockRotation = ResolvePreviewRotaryAngles(machine, setup.Stock.RotationA + operationIndexA, setup.Stock.RotationB + operationIndexB);

        return new JobSetup
        {
            Name = setup.Name,
            TransferRestFromPreviousSetup = setup.TransferRestFromPreviousSetup,
            Part = new PartDefinition
            {
                Name = setup.Part.Name,
                SourceType = setup.Part.SourceType,
                SourcePath = setup.Part.SourcePath,
                LengthX = setup.Part.LengthX,
                WidthY = setup.Part.WidthY,
                HeightZ = setup.Part.HeightZ,
                Diameter = setup.Part.Diameter,
                RotationA = partRotation.X,
                RotationB = partRotation.Y,
                RotationC = 0
            },
            Stock = new StockDefinition
            {
                Shape = setup.Stock.Shape,
                ShowInPreview = setup.Stock.ShowInPreview,
                ImportedSolidPath = setup.Stock.ImportedSolidPath,
                LengthX = setup.Stock.LengthX,
                WidthY = setup.Stock.WidthY,
                HeightZ = setup.Stock.HeightZ,
                Diameter = setup.Stock.Diameter,
                OffsetX = setup.Stock.OffsetX,
                OffsetY = setup.Stock.OffsetY,
                OffsetZ = setup.Stock.OffsetZ,
                RotationA = stockRotation.X,
                RotationB = stockRotation.Y,
                RotationC = 0,
                RadialAllowance = setup.Stock.RadialAllowance,
                AxialAllowance = setup.Stock.AxialAllowance
            },
            Operations = setup.Operations,
            WorkOrigin = setup.WorkOrigin,
            OriginSource = setup.OriginSource,
            OriginAnchor = setup.OriginAnchor,
            FlipAxisX = setup.FlipAxisX,
            FlipAxisY = setup.FlipAxisY,
            FlipAxisZ = setup.FlipAxisZ,
            WorkOffset = setup.WorkOffset,
            AlignmentOffsetX = setup.AlignmentOffsetX,
            AlignmentOffsetY = setup.AlignmentOffsetY,
            AlignmentOffsetZ = setup.AlignmentOffsetZ,
            SafeZ = setup.SafeZ,
            ClearanceZ = setup.ClearanceZ,
            WorkOffsetCode = setup.WorkOffsetCode,
            Notes = setup.Notes
        };
    }

    private static (double X, double Y) ResolvePreviewRotaryAngles(MachineProfile? machine, double axisA, double axisB)
    {
        var x = 0d;
        var y = 0d;
        AddMappedAngle(GetRotaryAxisDirection(machine, "A", RotaryAxisDirection.X), axisA, ref x, ref y);
        AddMappedAngle(GetRotaryAxisDirection(machine, "B", RotaryAxisDirection.Y), axisB, ref x, ref y);
        return (x, y);
    }

    private static RotaryAxisDirection GetRotaryAxisDirection(MachineProfile? machine, string axisName, RotaryAxisDirection fallback)
    {
        var axis = machine?.Axes.FirstOrDefault(candidate =>
            candidate.Type == AxisType.Rotary
            && string.Equals(NormalizeRotaryAxisName(candidate.Name), axisName, StringComparison.OrdinalIgnoreCase));
        return axis?.RotatesAround ?? fallback;
    }

    private static string NormalizeRotaryAxisName(string? axisName)
    {
        return string.IsNullOrWhiteSpace(axisName)
            ? string.Empty
            : axisName.Trim().Substring(0, 1).ToUpperInvariant();
    }

    private static void AddMappedAngle(RotaryAxisDirection direction, double angle, ref double x, ref double y)
    {
        if (Math.Abs(angle) < 0.000001)
        {
            return;
        }

        if (direction == RotaryAxisDirection.Y)
        {
            y += angle;
        }
        else
        {
            x += angle;
        }
    }

    private static void AddLights(Model3DGroup scene)
    {
        scene.Children.Add(new AmbientLight(Color.FromRgb(92, 98, 108)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(230, 234, 240), new Vector3D(-0.6, 0.3, -1)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(180, 190, 205), new Vector3D(0.4, -0.8, -0.4)));
    }

    private static void AddStock(
        Model3DGroup scene,
        JobSetup setup,
        LoadedPreviewGeometry? partGeometry,
        LoadedPreviewGeometry? stockGeometry,
        IDictionary<Model3D, SetupOriginSource> originSelectableModels)
    {
        if (!setup.Stock.ShowInPreview)
        {
            return;
        }

        if (stockGeometry?.HasRenderableGeometry == true)
        {
            scene.Children.Add(stockGeometry.Model);
            RegisterOriginModelTree(stockGeometry.Model, originSelectableModels, SetupOriginSource.Stock);
            return;
        }

        var dimensions = GetPreviewStockDimensions(setup, partGeometry);
        var placement = GetStockPlacement(setup, partGeometry);
        var stockTransform = BuildStockSetupTransform(setup, placement, dimensions);
        var stockColor = Color.FromRgb(207, 178, 132);
        switch (setup.Stock.Shape)
        {
            case StockShape.Cylinder:
                foreach (var outline in CreateCylinderOutlineModels(
                    placement.CenterX,
                    placement.CenterY,
                    placement.TopZ - dimensions.Height,
                    placement.TopZ,
                    dimensions.Diameter / 2d,
                    stockColor,
                    0.62))
                {
                    outline.Transform = stockTransform;
                    scene.Children.Add(outline);
                    originSelectableModels[outline] = SetupOriginSource.Stock;
                }
                break;
            default:
                foreach (var outline in CreateBoxOutlineModels(
                    placement.CenterX - (dimensions.Length / 2d),
                    placement.CenterY - (dimensions.Width / 2d),
                    placement.TopZ - dimensions.Height,
                    dimensions.Length,
                    dimensions.Width,
                    dimensions.Height,
                    stockColor,
                    0.58))
                {
                    outline.Transform = stockTransform;
                    scene.Children.Add(outline);
                    originSelectableModels[outline] = SetupOriginSource.Stock;
                }
                break;
        }
    }

    private static void AddPart(
        Model3DGroup scene,
        JobSetup setup,
        LoadedPreviewGeometry? partGeometry,
        ISet<Model3D> featureSelectableModels,
        IDictionary<Model3D, PreviewEdgeGeometry> edgeSelectableModels,
        IDictionary<Model3D, SetupOriginSource> originSelectableModels)
    {
        if (partGeometry?.HasRenderableGeometry == true)
        {
            scene.Children.Add(partGeometry.Model);
            RegisterModelTree(partGeometry.Model, featureSelectableModels);
            RegisterOriginModelTree(partGeometry.Model, originSelectableModels, SetupOriginSource.Model);
            foreach (var edgeModel in partGeometry.EdgeModels)
            {
                edgeSelectableModels[edgeModel.Key] = edgeModel.Value;
            }

            return;
        }

        var partColor = Color.FromRgb(99, 157, 201);
        var partTopZ = 0d;
        var partBottomZ = -Math.Max(setup.Part.HeightZ, 2);

        if (setup.Part.SourceType == PartSourceType.PrimitiveCylinder)
        {
            var model = CreateCylinderModel(
                0,
                0,
                partBottomZ,
                partTopZ,
                Math.Max(setup.Part.Diameter, 8) / 2d,
                partColor,
                0.22);
            model.Transform = BuildPartSetupTransform(setup);
            scene.Children.Add(model);
            featureSelectableModels.Add(model);
            originSelectableModels[model] = SetupOriginSource.Model;
            return;
        }

        var length = Math.Max(setup.Part.LengthX, 2);
        var width = Math.Max(setup.Part.WidthY, 2);
        var boxModel = CreateBoxModel(
            -length / 2d,
            -width / 2d,
            partBottomZ,
            length,
            width,
            Math.Max(partTopZ - partBottomZ, 2),
            partColor,
            0.20);
        boxModel.Transform = BuildPartSetupTransform(setup);
        scene.Children.Add(boxModel);
        featureSelectableModels.Add(boxModel);
        originSelectableModels[boxModel] = SetupOriginSource.Model;
    }

    private static void RegisterModelTree(Model3D model, ISet<Model3D> models)
    {
        models.Add(model);
        if (model is not Model3DGroup group)
        {
            return;
        }

        foreach (var child in group.Children)
        {
            RegisterModelTree(child, models);
        }
    }

    private static void RegisterOriginModelTree(
        Model3D model,
        IDictionary<Model3D, SetupOriginSource> models,
        SetupOriginSource source)
    {
        models[model] = source;
        if (model is not Model3DGroup group)
        {
            return;
        }

        foreach (var child in group.Children)
        {
            RegisterOriginModelTree(child, models, source);
        }
    }

    private static void AddSetupOriginMarker(Model3DGroup scene, JobSetup setup, Rect3D bounds)
    {
        var origin = new Point3D(setup.WorkOrigin.X, setup.WorkOrigin.Y, setup.WorkOrigin.Z);
        var largestExtent = bounds.IsEmpty
            ? 100d
            : Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
        var axisLength = Math.Clamp(largestExtent * 0.14, 8, 42);
        var thickness = Math.Clamp(largestExtent * 0.004, 0.12, 0.75);
        var pointSize = thickness * 3.2;

        scene.Children.Add(CreateBoxModel(
            origin.X - (pointSize / 2d),
            origin.Y - (pointSize / 2d),
            origin.Z - (pointSize / 2d),
            pointSize,
            pointSize,
            pointSize,
            Color.FromRgb(255, 245, 214),
            0.98));

        AddAxisMarker(
            scene,
            origin,
            new Vector3D(setup.FlipAxisX ? -1 : 1, 0, 0),
            axisLength,
            thickness,
            Color.FromRgb(238, 77, 83));
        AddAxisMarker(
            scene,
            origin,
            new Vector3D(0, setup.FlipAxisY ? -1 : 1, 0),
            axisLength,
            thickness,
            Color.FromRgb(73, 204, 111));
        AddAxisMarker(
            scene,
            origin,
            new Vector3D(0, 0, setup.FlipAxisZ ? -1 : 1),
            axisLength,
            thickness,
            Color.FromRgb(91, 168, 255));
    }

    private static void AddAxisMarker(
        Model3DGroup scene,
        Point3D origin,
        Vector3D direction,
        double axisLength,
        double thickness,
        Color color)
    {
        direction.Normalize();
        var end = origin + (direction * axisLength);
        scene.Children.Add(CreateSegmentModel(origin, end, thickness, color, 0.98));

        var headStart = origin + (direction * (axisLength * 0.82));
        scene.Children.Add(CreateSegmentModel(headStart, end, thickness * 2.3, color, 0.9));
    }

    private static List<PlaybackSegment> BuildPlaybackSegments(
        JobSetup setup,
        StockPlacement placement,
        IReadOnlyList<OperationToolpath> toolpaths)
    {
        var segments = new List<PlaybackSegment>();
        foreach (var toolpath in toolpaths)
        {
            for (var index = 1; index < toolpath.Moves.Count; index++)
            {
                var start = ToPreviewPoint(setup, placement, toolpath.Moves[index - 1]);
                var end = ToPreviewPoint(setup, placement, toolpath.Moves[index]);
                if ((end - start).LengthSquared < 0.000001)
                {
                    continue;
                }

                segments.Add(new PlaybackSegment(
                    start,
                    end,
                    toolpath.Tool,
                    toolpath.Moves[index].IsCutting && toolpath.Moves[index].Mode == MotionMode.Linear));
            }
        }

        return segments;
    }

    private static void AddPlaybackStock(
        Model3DGroup scene,
        JobSetup setup,
        Rect3D stockBounds,
        IReadOnlyList<PlaybackSegment> playbackSegments,
        ToolpathPlaybackSnapshot playback)
    {
        if (stockBounds.IsEmpty || stockBounds.SizeX <= 0 || stockBounds.SizeY <= 0 || stockBounds.SizeZ <= 0)
        {
            return;
        }

        var stockModel = CreatePlaybackStockModel(setup, stockBounds, playbackSegments, playback);
        scene.Children.Add(stockModel);
    }

    private static GeometryModel3D CreatePlaybackStockModel(
        JobSetup setup,
        Rect3D stockBounds,
        IReadOnlyList<PlaybackSegment> playbackSegments,
        ToolpathPlaybackSnapshot playback)
    {
        const int maxGrid = 62;
        const int minGrid = 22;
        var longest = Math.Max(stockBounds.SizeX, stockBounds.SizeY);
        var xCount = Math.Clamp((int)Math.Round((stockBounds.SizeX / longest) * maxGrid), minGrid, maxGrid);
        var yCount = Math.Clamp((int)Math.Round((stockBounds.SizeY / longest) * maxGrid), minGrid, maxGrid);
        var topZ = stockBounds.Z + stockBounds.SizeZ;
        var bottomZ = stockBounds.Z;
        var heights = new double[xCount, yCount];
        var inside = new bool[xCount, yCount];
        var stepX = stockBounds.SizeX / Math.Max(xCount - 1, 1);
        var stepY = stockBounds.SizeY / Math.Max(yCount - 1, 1);
        var cellSize = Math.Max(Math.Min(stepX, stepY), 0.05);

        for (var x = 0; x < xCount; x++)
        {
            for (var y = 0; y < yCount; y++)
            {
                var worldX = stockBounds.X + (x * stepX);
                var worldY = stockBounds.Y + (y * stepY);
                heights[x, y] = topZ;
                inside[x, y] = IsInsidePlaybackStock(setup, stockBounds, worldX, worldY);
            }
        }

        ApplyPlaybackCuts(heights, inside, stockBounds, cellSize, playbackSegments, playback);
        return CreatePlaybackStockMesh(setup, stockBounds, heights, inside, Color.FromRgb(45, 142, 76));
    }

    private static void ApplyPlaybackCuts(
        double[,] heights,
        bool[,] inside,
        Rect3D stockBounds,
        double cellSize,
        IReadOnlyList<PlaybackSegment> playbackSegments,
        ToolpathPlaybackSnapshot playback)
    {
        var clampedPosition = Math.Clamp(playback.SegmentPosition, 0, playbackSegments.Count);
        var fullSegmentCount = Math.Min((int)Math.Floor(clampedPosition), playbackSegments.Count);
        var partialFraction = clampedPosition - fullSegmentCount;

        for (var index = 0; index < fullSegmentCount; index++)
        {
            RasterizePlaybackCut(heights, inside, stockBounds, cellSize, playbackSegments[index], 1d);
        }

        if (fullSegmentCount < playbackSegments.Count && partialFraction > 0.0001)
        {
            RasterizePlaybackCut(heights, inside, stockBounds, cellSize, playbackSegments[fullSegmentCount], partialFraction);
        }
    }

    private static void RasterizePlaybackCut(
        double[,] heights,
        bool[,] inside,
        Rect3D stockBounds,
        double cellSize,
        PlaybackSegment segment,
        double fraction)
    {
        if (!segment.IsCutting)
        {
            return;
        }

        var xCount = heights.GetLength(0);
        var yCount = heights.GetLength(1);
        var stepX = stockBounds.SizeX / Math.Max(xCount - 1, 1);
        var stepY = stockBounds.SizeY / Math.Max(yCount - 1, 1);
        var toolRadius = Math.Max(segment.Tool.CuttingDiameter, 0.5) / 2d;
        var segmentVector = segment.End - segment.Start;
        var segmentLength = segmentVector.Length * Math.Clamp(fraction, 0, 1);
        var samples = Math.Max(1, (int)Math.Ceiling(segmentLength / Math.Max(cellSize * 0.55, 0.4)));

        for (var sample = 0; sample <= samples; sample++)
        {
            var t = Math.Clamp((sample / (double)samples) * fraction, 0, 1);
            var x = Lerp(segment.Start.X, segment.End.X, t);
            var y = Lerp(segment.Start.Y, segment.End.Y, t);
            var z = Lerp(segment.Start.Z, segment.End.Z, t);
            var minGridX = Math.Max(0, (int)Math.Floor(((x - toolRadius) - stockBounds.X) / stepX));
            var maxGridX = Math.Min(xCount - 1, (int)Math.Ceiling(((x + toolRadius) - stockBounds.X) / stepX));
            var minGridY = Math.Max(0, (int)Math.Floor(((y - toolRadius) - stockBounds.Y) / stepY));
            var maxGridY = Math.Min(yCount - 1, (int)Math.Ceiling(((y + toolRadius) - stockBounds.Y) / stepY));

            for (var gridX = minGridX; gridX <= maxGridX; gridX++)
            {
                for (var gridY = minGridY; gridY <= maxGridY; gridY++)
                {
                    if (!inside[gridX, gridY])
                    {
                        continue;
                    }

                    var worldX = stockBounds.X + (gridX * stepX);
                    var worldY = stockBounds.Y + (gridY * stepY);
                    var dx = worldX - x;
                    var dy = worldY - y;
                    if ((dx * dx) + (dy * dy) <= toolRadius * toolRadius)
                    {
                        heights[gridX, gridY] = Math.Min(heights[gridX, gridY], Math.Max(stockBounds.Z, z));
                    }
                }
            }
        }
    }

    private static GeometryModel3D CreatePlaybackStockMesh(
        JobSetup setup,
        Rect3D stockBounds,
        double[,] heights,
        bool[,] inside,
        Color color)
    {
        var mesh = new MeshGeometry3D();
        var xCount = heights.GetLength(0);
        var yCount = heights.GetLength(1);
        var indices = new int[xCount, yCount];
        var stepX = stockBounds.SizeX / Math.Max(xCount - 1, 1);
        var stepY = stockBounds.SizeY / Math.Max(yCount - 1, 1);

        if (setup.Stock.Shape == StockShape.Cylinder)
        {
            return CreatePlaybackCylinderStockMesh(stockBounds, heights, color);
        }

        for (var x = 0; x < xCount; x++)
        {
            for (var y = 0; y < yCount; y++)
            {
                indices[x, y] = -1;
                if (!inside[x, y])
                {
                    continue;
                }

                indices[x, y] = mesh.Positions.Count;
                mesh.Positions.Add(new Point3D(stockBounds.X + (x * stepX), stockBounds.Y + (y * stepY), heights[x, y]));
                mesh.Normals.Add(new Vector3D(0, 0, 1));
            }
        }

        for (var x = 0; x < xCount - 1; x++)
        {
            for (var y = 0; y < yCount - 1; y++)
            {
                var a = indices[x, y];
                var b = indices[x + 1, y];
                var c = indices[x + 1, y + 1];
                var d = indices[x, y + 1];
                if (a < 0 || b < 0 || c < 0 || d < 0)
                {
                    continue;
                }

                mesh.TriangleIndices.Add(a);
                mesh.TriangleIndices.Add(b);
                mesh.TriangleIndices.Add(c);
                mesh.TriangleIndices.Add(a);
                mesh.TriangleIndices.Add(c);
                mesh.TriangleIndices.Add(d);
            }
        }

        AddPlaybackBoxSides(mesh, stockBounds, heights);
        AddPlaybackBoxBottom(mesh, stockBounds);

        return CreateGeometryModel(mesh, color, 1.0);
    }

    private static GeometryModel3D CreatePlaybackCylinderStockMesh(Rect3D stockBounds, double[,] heights, Color color)
    {
        const int segments = 96;
        const int radialSteps = 34;
        var mesh = new MeshGeometry3D();
        var centerX = stockBounds.X + (stockBounds.SizeX / 2d);
        var centerY = stockBounds.Y + (stockBounds.SizeY / 2d);
        var radius = Math.Min(stockBounds.SizeX, stockBounds.SizeY) / 2d;
        var bottomZ = stockBounds.Z;
        var topIndices = new int[radialSteps + 1, segments];
        var bottomIndices = new int[segments];

        var centerTopHeight = GetPlaybackHeightAt(heights, stockBounds, centerX, centerY);
        var centerTopIndex = mesh.Positions.Count;
        mesh.Positions.Add(new Point3D(centerX, centerY, centerTopHeight));
        mesh.Normals.Add(new Vector3D(0, 0, 1));
        for (var segment = 0; segment < segments; segment++)
        {
            topIndices[0, segment] = centerTopIndex;
        }

        for (var radial = 1; radial <= radialSteps; radial++)
        {
            var sampleRadius = radius * radial / radialSteps;
            var heightSampleRadius = Math.Min(sampleRadius, radius * 0.992);
            for (var segment = 0; segment < segments; segment++)
            {
                var angle = (Math.PI * 2d * segment) / segments;
                var x = centerX + (Math.Cos(angle) * sampleRadius);
                var y = centerY + (Math.Sin(angle) * sampleRadius);
                var height = GetPlaybackHeightAt(
                    heights,
                    stockBounds,
                    centerX + (Math.Cos(angle) * heightSampleRadius),
                    centerY + (Math.Sin(angle) * heightSampleRadius));
                topIndices[radial, segment] = mesh.Positions.Count;
                mesh.Positions.Add(new Point3D(x, y, height));
                mesh.Normals.Add(new Vector3D(0, 0, 1));
            }
        }

        for (var segment = 0; segment < segments; segment++)
        {
            var next = (segment + 1) % segments;
            mesh.TriangleIndices.Add(centerTopIndex);
            mesh.TriangleIndices.Add(topIndices[1, segment]);
            mesh.TriangleIndices.Add(topIndices[1, next]);
        }

        for (var radial = 1; radial < radialSteps; radial++)
        {
            for (var segment = 0; segment < segments; segment++)
            {
                var next = (segment + 1) % segments;
                mesh.TriangleIndices.Add(topIndices[radial, segment]);
                mesh.TriangleIndices.Add(topIndices[radial + 1, segment]);
                mesh.TriangleIndices.Add(topIndices[radial + 1, next]);
                mesh.TriangleIndices.Add(topIndices[radial, segment]);
                mesh.TriangleIndices.Add(topIndices[radial + 1, next]);
                mesh.TriangleIndices.Add(topIndices[radial, next]);
            }
        }

        var centerBottomIndex = mesh.Positions.Count;
        mesh.Positions.Add(new Point3D(centerX, centerY, bottomZ));
        mesh.Normals.Add(new Vector3D(0, 0, -1));

        for (var index = 0; index < segments; index++)
        {
            var angle = (Math.PI * 2d * index) / segments;
            var x = centerX + (Math.Cos(angle) * radius);
            var y = centerY + (Math.Sin(angle) * radius);
            bottomIndices[index] = mesh.Positions.Count;
            mesh.Positions.Add(new Point3D(x, y, bottomZ));
            mesh.Normals.Add(new Vector3D(0, 0, -1));
        }

        for (var index = 0; index < segments; index++)
        {
            var next = (index + 1) % segments;
            mesh.TriangleIndices.Add(centerBottomIndex);
            mesh.TriangleIndices.Add(bottomIndices[next]);
            mesh.TriangleIndices.Add(bottomIndices[index]);
        }

        for (var index = 0; index < segments; index++)
        {
            var next = (index + 1) % segments;
            var angle = (Math.PI * 2d * (index + 0.5)) / segments;
            var normal = new Vector3D(Math.Cos(angle), Math.Sin(angle), 0);

            AddQuad(
                mesh,
                mesh.Positions[bottomIndices[next]],
                mesh.Positions[bottomIndices[index]],
                mesh.Positions[topIndices[radialSteps, index]],
                mesh.Positions[topIndices[radialSteps, next]],
                normal);
        }

        return CreateGeometryModel(mesh, color, 1.0);
    }

    private static void AddPlaybackBoxSides(MeshGeometry3D mesh, Rect3D stockBounds, double[,] heights)
    {
        var xCount = heights.GetLength(0);
        var yCount = heights.GetLength(1);
        var stepX = stockBounds.SizeX / Math.Max(xCount - 1, 1);
        var stepY = stockBounds.SizeY / Math.Max(yCount - 1, 1);
        var bottomZ = stockBounds.Z;

        for (var x = 0; x < xCount - 1; x++)
        {
            var x0 = stockBounds.X + (x * stepX);
            var x1 = stockBounds.X + ((x + 1) * stepX);
            AddQuad(
                mesh,
                new Point3D(x0, stockBounds.Y, bottomZ),
                new Point3D(x1, stockBounds.Y, bottomZ),
                new Point3D(x1, stockBounds.Y, heights[x + 1, 0]),
                new Point3D(x0, stockBounds.Y, heights[x, 0]),
                new Vector3D(0, -1, 0));
            AddQuad(
                mesh,
                new Point3D(x1, stockBounds.Y + stockBounds.SizeY, bottomZ),
                new Point3D(x0, stockBounds.Y + stockBounds.SizeY, bottomZ),
                new Point3D(x0, stockBounds.Y + stockBounds.SizeY, heights[x, yCount - 1]),
                new Point3D(x1, stockBounds.Y + stockBounds.SizeY, heights[x + 1, yCount - 1]),
                new Vector3D(0, 1, 0));
        }

        for (var y = 0; y < yCount - 1; y++)
        {
            var y0 = stockBounds.Y + (y * stepY);
            var y1 = stockBounds.Y + ((y + 1) * stepY);
            AddQuad(
                mesh,
                new Point3D(stockBounds.X, y1, bottomZ),
                new Point3D(stockBounds.X, y0, bottomZ),
                new Point3D(stockBounds.X, y0, heights[0, y]),
                new Point3D(stockBounds.X, y1, heights[0, y + 1]),
                new Vector3D(-1, 0, 0));
            AddQuad(
                mesh,
                new Point3D(stockBounds.X + stockBounds.SizeX, y0, bottomZ),
                new Point3D(stockBounds.X + stockBounds.SizeX, y1, bottomZ),
                new Point3D(stockBounds.X + stockBounds.SizeX, y1, heights[xCount - 1, y + 1]),
                new Point3D(stockBounds.X + stockBounds.SizeX, y0, heights[xCount - 1, y]),
                new Vector3D(1, 0, 0));
        }
    }

    private static void AddPlaybackBoxBottom(MeshGeometry3D mesh, Rect3D stockBounds)
    {
        var minX = stockBounds.X;
        var maxX = stockBounds.X + stockBounds.SizeX;
        var minY = stockBounds.Y;
        var maxY = stockBounds.Y + stockBounds.SizeY;
        var bottomZ = stockBounds.Z;

        AddQuad(
            mesh,
            new Point3D(minX, maxY, bottomZ),
            new Point3D(maxX, maxY, bottomZ),
            new Point3D(maxX, minY, bottomZ),
            new Point3D(minX, minY, bottomZ),
            new Vector3D(0, 0, -1));
    }

    private static bool IsInsidePlaybackStock(JobSetup setup, Rect3D stockBounds, double x, double y)
    {
        if (setup.Stock.Shape != StockShape.Cylinder)
        {
            return true;
        }

        var centerX = stockBounds.X + (stockBounds.SizeX / 2d);
        var centerY = stockBounds.Y + (stockBounds.SizeY / 2d);
        var radius = Math.Min(stockBounds.SizeX, stockBounds.SizeY) / 2d;
        var dx = x - centerX;
        var dy = y - centerY;
        return (dx * dx) + (dy * dy) <= radius * radius;
    }

    private static double GetPlaybackHeightAt(double[,] heights, Rect3D stockBounds, double x, double y)
    {
        var xCount = heights.GetLength(0);
        var yCount = heights.GetLength(1);
        var stepX = stockBounds.SizeX / Math.Max(xCount - 1, 1);
        var stepY = stockBounds.SizeY / Math.Max(yCount - 1, 1);
        var gridX = Math.Clamp((int)Math.Round((x - stockBounds.X) / stepX), 0, xCount - 1);
        var gridY = Math.Clamp((int)Math.Round((y - stockBounds.Y) / stepY), 0, yCount - 1);
        return heights[gridX, gridY];
    }

    private static void AddPlaybackToolMarker(
        Model3DGroup scene,
        IReadOnlyList<PlaybackSegment> playbackSegments,
        ToolpathPlaybackSnapshot playback,
        Rect3D bounds)
    {
        if (playbackSegments.Count == 0)
        {
            return;
        }

        var segmentPosition = Math.Clamp(playback.SegmentPosition, 0, playbackSegments.Count);
        var segmentIndex = Math.Min((int)Math.Floor(segmentPosition), playbackSegments.Count - 1);
        var fraction = segmentIndex >= playbackSegments.Count - 1 && segmentPosition >= playbackSegments.Count
            ? 1d
            : segmentPosition - Math.Floor(segmentPosition);
        var segment = playbackSegments[segmentIndex];
        var toolPoint = new Point3D(
            Lerp(segment.Start.X, segment.End.X, fraction),
            Lerp(segment.Start.Y, segment.End.Y, fraction),
            Lerp(segment.Start.Z, segment.End.Z, fraction));
        var largestExtent = bounds.IsEmpty ? 100d : Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
        var cuttingLength = Math.Max(
            Math.Max(segment.Tool.CuttingLength, segment.Tool.FluteLength),
            Math.Clamp(largestExtent * 0.08, 8, 40));
        var shankRadius = Math.Max(segment.Tool.ShankDiameter > 0 ? segment.Tool.ShankDiameter : segment.Tool.CuttingDiameter, segment.Tool.CuttingDiameter) / 2d;
        var shankLength = Math.Max(segment.Tool.StickOut - cuttingLength, Math.Clamp(largestExtent * 0.08, 8, 48));

        AddToolCuttingGeometry(scene, segment.Tool, toolPoint, cuttingLength);
        scene.Children.Add(CreateCylinderModel(
            toolPoint.X,
            toolPoint.Y,
            toolPoint.Z + cuttingLength,
            toolPoint.Z + cuttingLength + shankLength,
            shankRadius,
            Color.FromRgb(229, 178, 44),
            0.88,
            32));
    }

    private static void AddToolCuttingGeometry(Model3DGroup scene, ToolDefinition tool, Point3D toolPoint, double cuttingLength)
    {
        var toolRadius = Math.Max(tool.CuttingDiameter, 0.8) / 2d;
        var tipRadius = Math.Clamp(tool.TipDiameter, 0, Math.Max(tool.CuttingDiameter, 0.8)) / 2d;
        var toolColor = Color.FromRgb(255, 221, 72);

        switch (tool.Style)
        {
            case ToolStyle.Ball:
                scene.Children.Add(CreateHemisphereModel(toolPoint.X, toolPoint.Y, toolPoint.Z + toolRadius, toolRadius, lowerHalf: true, toolColor, 0.98));
                if (cuttingLength > toolRadius)
                {
                    scene.Children.Add(CreateCylinderModel(toolPoint.X, toolPoint.Y, toolPoint.Z + toolRadius, toolPoint.Z + cuttingLength, toolRadius, toolColor, 0.98, 32));
                }

                break;
            case ToolStyle.Drill:
            case ToolStyle.SpotDrill:
            case ToolStyle.CenterDrill:
            case ToolStyle.VPoint:
            case ToolStyle.VBit:
            case ToolStyle.Chamfer:
            case ToolStyle.Engraver:
                var tipLength = Math.Clamp(GetToolTipLength(tool), toolRadius * 0.2, cuttingLength);
                scene.Children.Add(CreateFrustumModel(toolPoint.X, toolPoint.Y, toolPoint.Z, toolPoint.Z + tipLength, tipRadius, toolRadius, toolColor, 0.98, 36));
                if (cuttingLength > tipLength + 0.001)
                {
                    scene.Children.Add(CreateCylinderModel(toolPoint.X, toolPoint.Y, toolPoint.Z + tipLength, toolPoint.Z + cuttingLength, toolRadius, toolColor, 0.98, 36));
                }

                break;
            case ToolStyle.Taper:
            case ToolStyle.Dovetail:
                var smallRadius = tipRadius > 0 ? tipRadius : Math.Max(toolRadius * 0.35, 0.2);
                scene.Children.Add(CreateFrustumModel(toolPoint.X, toolPoint.Y, toolPoint.Z, toolPoint.Z + cuttingLength, smallRadius, toolRadius, toolColor, 0.98, 36));
                break;
            case ToolStyle.Lollipop:
                var neckRadius = Math.Max(tool.NeckDiameter > 0 ? tool.NeckDiameter / 2d : toolRadius * 0.35, 0.2);
                var headCenterZ = toolPoint.Z + toolRadius;
                var neckStartZ = headCenterZ + toolRadius;
                var requestedNeckLength = tool.NeckLength > 0 ? tool.NeckLength : cuttingLength - (toolRadius * 2d);
                var neckEndZ = Math.Min(toolPoint.Z + cuttingLength, neckStartZ + Math.Max(requestedNeckLength, 0));
                scene.Children.Add(CreateSphereModel(toolPoint.X, toolPoint.Y, headCenterZ, toolRadius, toolColor, 0.98));
                if (neckEndZ > neckStartZ + 0.001)
                {
                    scene.Children.Add(CreateCylinderModel(toolPoint.X, toolPoint.Y, neckStartZ, neckEndZ, neckRadius, toolColor, 0.98, 28));
                }

                break;
            default:
                scene.Children.Add(CreateCylinderModel(toolPoint.X, toolPoint.Y, toolPoint.Z, toolPoint.Z + cuttingLength, toolRadius, toolColor, 0.98, 32));
                break;
        }
    }

    private static double GetToolTipLength(ToolDefinition tool)
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

    private static void AddSelectedFeatureReference(
        Model3DGroup scene,
        JobSetup setup,
        StockPlacement stockPlacement,
        ToolpathOperationDefinition? selectedOperation,
        Rect3D bounds)
    {
        if (selectedOperation is null)
        {
            return;
        }

        var largestExtent = bounds.IsEmpty
            ? 100d
            : Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
        var thickness = Math.Clamp(largestExtent * 0.004, 0.12, 0.75);

        if (selectedOperation.Feature.Shape == FeatureShape.EdgePath && selectedOperation.Feature.PathPoints.Count >= 2)
        {
            AddFeaturePath(scene, setup, stockPlacement, selectedOperation.Feature.PathPoints, thickness, Color.FromRgb(255, 138, 64), 0.96);
            return;
        }

        if (selectedOperation.Type is OperationType.BulkRemoval or OperationType.Raster or OperationType.AdaptiveClearing or OperationType.ZLevelFinishing or OperationType.Parallel3DFinishing or OperationType.ScallopFinishing or OperationType.PencilCleanup && selectedOperation.Feature.KeepoutLoops.Count > 0)
        {
            foreach (var keepoutLoop in selectedOperation.Feature.KeepoutLoops)
            {
                AddFeaturePath(scene, setup, stockPlacement, keepoutLoop.Points, thickness, Color.FromRgb(255, 138, 64), 0.92);
            }
        }
    }

    private static void AddFeaturePath(
        Model3DGroup scene,
        JobSetup setup,
        StockPlacement stockPlacement,
        IReadOnlyList<FeaturePathPoint> pathPoints,
        double thickness,
        Color color,
        double opacity)
    {
        if (pathPoints.Count < 2)
        {
            return;
        }

        var points = pathPoints
            .Select(point => new Point3D(
                setup.WorkOffset.X + setup.AlignmentOffsetX + point.X,
                setup.WorkOffset.Y + setup.AlignmentOffsetY + point.Y,
                stockPlacement.TopZ + point.Z))
            .ToList();

        for (var index = 1; index < points.Count; index++)
        {
            scene.Children.Add(CreateSegmentModel(
                points[index - 1],
                points[index],
                thickness,
                color,
                opacity));
        }
    }

    private static int AddToolpathPreview(
        Model3DGroup scene,
        JobSetup setup,
        StockPlacement stockPlacement,
        IReadOnlyList<OperationToolpath> toolpaths,
        ToolpathOperationDefinition? selectedOperation,
        Rect3D bounds,
        double opacityScale = 1d,
        double thicknessScale = 1d)
    {
        if (toolpaths.Count == 0 || opacityScale <= 0.001 || thicknessScale <= 0.001)
        {
            return 0;
        }

        var largestExtent = bounds.IsEmpty
            ? 100d
            : Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
        var baseThickness = Math.Clamp(largestExtent * 0.0022, 0.08, 0.45);
        var totalSegments = toolpaths.Sum(toolpath => Math.Max(0, toolpath.Moves.Count - 1));
        var stride = Math.Max(1, totalSegments / 3200);
        var segmentIndex = 0;
        var renderedSegments = 0;

        foreach (var toolpath in toolpaths)
        {
            var isSelected = ReferenceEquals(toolpath.Operation, selectedOperation);
            for (var index = 1; index < toolpath.Moves.Count; index++)
            {
                var previous = toolpath.Moves[index - 1];
                var current = toolpath.Moves[index];
                if (!isSelected && segmentIndex++ % stride != 0)
                {
                    continue;
                }

                var start = ToPreviewPoint(setup, stockPlacement, previous);
                var end = ToPreviewPoint(setup, stockPlacement, current);
                if ((end - start).LengthSquared < 0.000001)
                {
                    continue;
                }

                var color = GetToolpathSegmentColor(current, isSelected);
                var opacity = current.IsCutting
                    ? isSelected ? 0.98 : 0.72
                    : isSelected ? 0.62 : 0.34;
                opacity = Math.Clamp(opacity * opacityScale, 0.03, 1.0);
                var thickness = GetToolpathPreviewThickness(toolpath.Tool, current, baseThickness, isSelected) * thicknessScale;

                scene.Children.Add(CreateSegmentModel(start, end, thickness, color, opacity));
                renderedSegments++;
            }
        }

        return renderedSegments;
    }

    private static double GetToolpathPreviewThickness(
        ToolDefinition tool,
        ToolpathMove move,
        double baseThickness,
        bool isSelected)
    {
        _ = tool;
        if (!move.IsCutting)
        {
            return baseThickness * 0.58;
        }

        return isSelected ? baseThickness * 1.85 : baseThickness * 1.2;
    }

    private static Point3D ToPreviewPoint(JobSetup setup, StockPlacement stockPlacement, ToolpathMove move)
    {
        return new Point3D(
            move.X,
            move.Y,
            move.Z + (stockPlacement.TopZ - setup.WorkOffset.Z));
    }

    private static Color GetToolpathSegmentColor(ToolpathMove move, bool isSelected)
    {
        if (move.IsCutting)
        {
            return isSelected
                ? Color.FromRgb(255, 221, 83)
                : Color.FromRgb(71, 226, 170);
        }

        return isSelected
            ? Color.FromRgb(158, 218, 255)
            : Color.FromRgb(130, 151, 172);
    }

    private static void AddFeatureVolumes(
        Model3DGroup scene,
        IDictionary<Model3D, ToolpathOperationDefinition> selectableModels,
        JobSetup setup,
        IEnumerable<ToolpathOperationDefinition> operations,
        ToolpathOperationDefinition? selectedOperation)
    {
        if (selectedOperation is null || !selectedOperation.Enabled)
        {
            return;
        }

        foreach (var model in CreateOperationModels(setup, selectedOperation, Color.FromRgb(242, 129, 58), 0.92))
        {
            scene.Children.Add(model);
            selectableModels[model] = selectedOperation;
        }
    }

    private static IEnumerable<GeometryModel3D> CreateOperationModels(
        JobSetup setup,
        ToolpathOperationDefinition operation,
        Color color,
        double opacity)
    {
        var featureTopZ = setup.WorkOffset.Z + operation.Feature.StartZ;
        var featureBottomZ = featureTopZ - Math.Max(operation.Feature.Depth, 0.35);
        var centerX = setup.WorkOffset.X + setup.AlignmentOffsetX + operation.Feature.CenterX;
        var centerY = setup.WorkOffset.Y + setup.AlignmentOffsetY + operation.Feature.CenterY;

        if (operation.Type == OperationType.Facing)
        {
            var stockWidth = setup.Stock.Shape == StockShape.Cylinder ? setup.Stock.Diameter : setup.Stock.WidthY;
            var stockLength = setup.Stock.Shape == StockShape.Cylinder ? setup.Stock.Diameter : setup.Stock.LengthX;

            foreach (var model in CreateBoxOutlineModels(
                centerX - (stockLength / 2d),
                centerY - (stockWidth / 2d),
                featureTopZ - Math.Max(operation.Feature.Depth, 0.35),
                stockLength,
                stockWidth,
                Math.Max(operation.Feature.Depth, 0.35),
                color,
                opacity))
            {
                yield return model;
            }
            yield break;
        }

        if (operation.Type == OperationType.Drill && operation.Feature.Shape == FeatureShape.HolePattern)
        {
            foreach (var point in GetHolePatternPoints(setup, operation.Feature))
            {
                foreach (var model in CreateCylinderOutlineModels(
                    point.X,
                    point.Y,
                    featureBottomZ,
                    featureTopZ,
                    Math.Max(operation.Feature.Diameter > 0 ? operation.Feature.Diameter / 2d : 1.5, 1.2),
                    color,
                    opacity))
                {
                    yield return model;
                }
            }

            yield break;
        }

        if (operation.Feature.Shape == FeatureShape.Circle)
        {
            foreach (var model in CreateCylinderOutlineModels(
                centerX,
                centerY,
                featureBottomZ,
                featureTopZ,
                Math.Max(operation.Feature.Diameter, 2) / 2d,
                color,
                opacity))
            {
                yield return model;
            }
            yield break;
        }

        foreach (var model in CreateBoxOutlineModels(
            centerX - (Math.Max(operation.Feature.Length, 2) / 2d),
            centerY - (Math.Max(operation.Feature.Width, 2) / 2d),
            featureBottomZ,
            Math.Max(operation.Feature.Length, 2),
            Math.Max(operation.Feature.Width, 2),
            Math.Max(featureTopZ - featureBottomZ, 0.35),
            color,
            opacity))
        {
            yield return model;
        }
    }

    private static IReadOnlyList<Point> GetHolePatternPoints(JobSetup setup, FeatureDefinition feature)
    {
        var centerX = setup.WorkOffset.X + setup.AlignmentOffsetX + feature.CenterX;
        var centerY = setup.WorkOffset.Y + setup.AlignmentOffsetY + feature.CenterY;
        var points = new List<Point>();
        var totalPitchX = (Math.Max(feature.Columns, 1) - 1) * feature.PitchX;
        var totalPitchY = (Math.Max(feature.Rows, 1) - 1) * feature.PitchY;
        var originX = centerX - (totalPitchX / 2d);
        var originY = centerY - (totalPitchY / 2d);

        for (var row = 0; row < Math.Max(feature.Rows, 1); row++)
        {
            for (var column = 0; column < Math.Max(feature.Columns, 1); column++)
            {
                points.Add(new Point(originX + (column * feature.PitchX), originY + (row * feature.PitchY)));
            }
        }

        return points;
    }

    private static Rect3D GetSceneBounds(
        JobSetup setup,
        LoadedPreviewGeometry? partGeometry,
        LoadedPreviewGeometry? stockGeometry,
        IReadOnlyList<OperationToolpath> toolpaths)
    {
        var placement = GetStockPlacement(setup, partGeometry);
        var stockDimensions = GetPreviewStockDimensions(setup, partGeometry);
        var stockLength = setup.Stock.Shape == StockShape.Cylinder ? stockDimensions.Diameter : stockDimensions.Length;
        var stockWidth = setup.Stock.Shape == StockShape.Cylinder ? stockDimensions.Diameter : stockDimensions.Width;
        var topZ = placement.TopZ;
        var deepestFeatureZ = setup.Operations.Count == 0
            ? topZ - stockDimensions.Height
            : setup.Operations.Min(operation => (setup.WorkOffset.Z + operation.Feature.StartZ) - Math.Max(operation.Feature.Depth, 0.35));
        var bottomZ = Math.Min(topZ - Math.Max(stockDimensions.Height, 4), deepestFeatureZ);

        var bounds = Rect3D.Empty;
        if (setup.Stock.ShowInPreview)
        {
            bounds = new Rect3D(
                placement.CenterX - (stockLength / 2d),
                placement.CenterY - (stockWidth / 2d),
                bottomZ,
                Math.Max(stockLength, 10),
                Math.Max(stockWidth, 10),
                Math.Max(topZ - bottomZ, 10));
        }

        if (partGeometry?.HasRenderableGeometry == true)
        {
            bounds = bounds.IsEmpty ? partGeometry.Bounds : Union(bounds, partGeometry.Bounds);
        }

        if (stockGeometry?.HasRenderableGeometry == true)
        {
            bounds = bounds.IsEmpty ? stockGeometry.Bounds : Union(bounds, stockGeometry.Bounds);
        }

        var originPoint = new Point3D(setup.WorkOrigin.X, setup.WorkOrigin.Y, setup.WorkOrigin.Z);
        var originBounds = new Rect3D(originPoint.X, originPoint.Y, originPoint.Z, 0.1, 0.1, 0.1);
        bounds = bounds.IsEmpty ? originBounds : Union(bounds, originBounds);

        foreach (var toolpath in toolpaths)
        {
            foreach (var move in toolpath.Moves)
            {
                var previewPoint = ToPreviewPoint(setup, placement, move);
                var moveBounds = new Rect3D(previewPoint.X, previewPoint.Y, previewPoint.Z, 0.1, 0.1, 0.1);
                bounds = bounds.IsEmpty ? moveBounds : Union(bounds, moveBounds);
            }
        }

        if (bounds.IsEmpty)
        {
            bounds = GetFallbackPartBounds(setup);
        }

        return bounds;
    }

    private static Rect3D GetPreviewStockBounds(
        JobSetup setup,
        LoadedPreviewGeometry? partGeometry,
        LoadedPreviewGeometry? stockGeometry)
    {
        if (stockGeometry?.HasRenderableGeometry == true)
        {
            return stockGeometry.Bounds;
        }

        var placement = GetStockPlacement(setup, partGeometry);
        var dimensions = GetPreviewStockDimensions(setup, partGeometry);
        var stockLength = setup.Stock.Shape == StockShape.Cylinder ? dimensions.Diameter : dimensions.Length;
        var stockWidth = setup.Stock.Shape == StockShape.Cylinder ? dimensions.Diameter : dimensions.Width;
        var bounds = new Rect3D(
            placement.CenterX - (stockLength / 2d),
            placement.CenterY - (stockWidth / 2d),
            placement.TopZ - dimensions.Height,
            Math.Max(stockLength, 0.1),
            Math.Max(stockWidth, 0.1),
            Math.Max(dimensions.Height, 0.1));
        var stockTransform = BuildStockSetupTransform(setup, placement, dimensions);
        return stockTransform.Value.IsIdentity ? bounds : stockTransform.TransformBounds(bounds);
    }

    private static string BuildSummary(
        CamJob job,
        JobSetup setup,
        ToolpathOperationDefinition? selectedOperation,
        LoadedPreviewGeometry? partGeometry,
        LoadedPreviewGeometry? stockGeometry,
        IReadOnlyList<OperationToolpath> toolpaths,
        int visibleToolpathSegments)
    {
        var modelLabel = string.IsNullOrWhiteSpace(setup.Part.SourcePath)
            ? setup.Part.SourceType.ToString()
            : Path.GetFileName(setup.Part.SourcePath);
        var selectedText = selectedOperation is null
            ? "No operation selected."
            : partGeometry?.HasRenderableGeometry == true
                ? $"Selected: {selectedOperation.Name} ({selectedOperation.Type})."
                : $"Selected: {selectedOperation.Name} ({selectedOperation.Type}) in orange.";
        var partText = partGeometry?.HasRenderableGeometry == true ? partGeometry.Description : "part envelope";
        var stockText = !setup.Stock.ShowInPreview
            ? "stock hidden"
            : stockGeometry?.HasRenderableGeometry == true ? stockGeometry.Description : "stock envelope";
        var stockOffsetText = setup.Stock.ShowInPreview
            ? $" Stock offset: X{setup.Stock.OffsetX:0.###}, Y{setup.Stock.OffsetY:0.###}, Z{setup.Stock.OffsetZ:0.###}. Stock visual rotation: X{setup.Stock.RotationA:0.###}, Y{setup.Stock.RotationB:0.###}."
            : string.Empty;
        var setupText = job.Setups.Count > 1 ? $" Active setup: {setup.Name}." : string.Empty;
        var operationEnvelopeText = selectedOperation is null
            ? "operation overlays hidden"
            : partGeometry?.HasRenderableGeometry == true ? "operation overlays hidden" : "the selected operation envelope";
        var toolpathMoveCount = toolpaths.Sum(toolpath => toolpath.Moves.Count);
        var zeroMoveDetail = toolpaths.Count > 0 && toolpathMoveCount == 0
            ? " " + string.Join(" ", toolpaths
                .Select(toolpath => toolpath.Summary)
                .Where(summary => !string.IsNullOrWhiteSpace(summary))
                .Distinct()
                .Take(2))
            : string.Empty;
        var toolpathText = toolpathMoveCount == 0
            ? $"No toolpath preview yet.{zeroMoveDetail}"
            : $"{visibleToolpathSegments} 3D toolpath segments shown from {toolpathMoveCount} planned moves.";

        return $"{selectedText} Previewing {partText}, {stockText}, and {operationEnvelopeText}. {toolpathText} Profile, 2D contour, chamfer, and pencil operations can select visible model edges; roughing and 3D finishing operations can select model faces as floors/lower limits.{setupText} Model source: {modelLabel}.{stockOffsetText}";
    }

    private static Rect3D Union(Rect3D first, Rect3D second)
    {
        var result = first;
        result.Union(second);
        return result;
    }

    private static StockPreviewDimensions GetPreviewStockDimensions(JobSetup setup, LoadedPreviewGeometry? partGeometry)
    {
        var radialAllowance = Math.Max(setup.Stock.RadialAllowance, 0);
        var axialAllowance = Math.Max(setup.Stock.AxialAllowance, 0);
        var partDimensions = GetPartReferenceDimensions(setup, partGeometry);

        var fallbackLength = partDimensions.Length + (radialAllowance * 2d);
        var fallbackWidth = partDimensions.Width + (radialAllowance * 2d);
        var fallbackHeight = partDimensions.Height + axialAllowance;
        var fallbackDiameter = Math.Max(partDimensions.Diameter, Math.Max(partDimensions.Length, partDimensions.Width)) + (radialAllowance * 2d);

        var length = setup.Stock.LengthX > 0 ? setup.Stock.LengthX : fallbackLength;
        var width = setup.Stock.WidthY > 0 ? setup.Stock.WidthY : fallbackWidth;
        var height = setup.Stock.HeightZ > 0 ? setup.Stock.HeightZ : fallbackHeight;
        var diameter = setup.Stock.Diameter > 0 ? setup.Stock.Diameter : fallbackDiameter;

        return new StockPreviewDimensions(
            Math.Max(length, 2d),
            Math.Max(width, 2d),
            Math.Max(height, 2d),
            Math.Max(diameter, 2d));
    }

    private static StockPlacement GetStockPlacement(JobSetup setup, LoadedPreviewGeometry? partGeometry)
    {
        if (partGeometry?.HasRenderableGeometry == true)
        {
            return new StockPlacement(
                partGeometry.Bounds.X + (partGeometry.Bounds.SizeX / 2d) + setup.Stock.OffsetX,
                partGeometry.Bounds.Y + (partGeometry.Bounds.SizeY / 2d) + setup.Stock.OffsetY,
                partGeometry.Bounds.Z + partGeometry.Bounds.SizeZ + setup.Stock.OffsetZ);
        }

        var fallbackBounds = GetFallbackPartBounds(setup);
        return new StockPlacement(
            fallbackBounds.X + (fallbackBounds.SizeX / 2d) + setup.Stock.OffsetX,
            fallbackBounds.Y + (fallbackBounds.SizeY / 2d) + setup.Stock.OffsetY,
            fallbackBounds.Z + fallbackBounds.SizeZ + setup.Stock.OffsetZ);
    }

    private static PartReferenceDimensions GetPartReferenceDimensions(JobSetup setup, LoadedPreviewGeometry? partGeometry)
    {
        if (partGeometry?.HasRenderableGeometry == true)
        {
            return new PartReferenceDimensions(
                Math.Max(partGeometry.Bounds.SizeX, 2d),
                Math.Max(partGeometry.Bounds.SizeY, 2d),
                Math.Max(partGeometry.Bounds.SizeZ, 2d),
                Math.Max(partGeometry.Bounds.SizeX, partGeometry.Bounds.SizeY));
        }

        var bounds = GetFallbackPartBounds(setup);
        return new PartReferenceDimensions(
            Math.Max(bounds.SizeX, 2d),
            Math.Max(bounds.SizeY, 2d),
            Math.Max(bounds.SizeZ, 2d),
            Math.Max(Math.Max(bounds.SizeX, bounds.SizeY), Math.Max(setup.Part.Diameter, 2d)));
    }

    private static Rect3D GetFallbackPartBounds(JobSetup setup)
    {
        Rect3D localBounds;
        if (setup.Part.SourceType == PartSourceType.PrimitiveCylinder)
        {
            var diameter = Math.Max(setup.Part.Diameter, 2d);
            localBounds = new Rect3D(
                -diameter / 2d,
                -diameter / 2d,
                -Math.Max(setup.Part.HeightZ, 2d),
                diameter,
                diameter,
                Math.Max(setup.Part.HeightZ, 2d));
        }
        else
        {
            var length = Math.Max(setup.Part.LengthX, 2d);
            var width = Math.Max(setup.Part.WidthY, 2d);
            var height = Math.Max(setup.Part.HeightZ, 2d);
            localBounds = new Rect3D(-length / 2d, -width / 2d, -height, length, width, height);
        }

        var transform = BuildPartSetupTransform(setup);
        return transform.Value.IsIdentity ? localBounds : transform.TransformBounds(localBounds);
    }

    private static Transform3D BuildPartSetupTransform(JobSetup setup)
    {
        var transformGroup = new Transform3DGroup();
        AddRotation(transformGroup, new Vector3D(1, 0, 0), setup.Part.RotationA);
        AddRotation(transformGroup, new Vector3D(0, 1, 0), setup.Part.RotationB);

        var translation = new Vector3D(
            setup.WorkOffset.X + setup.AlignmentOffsetX,
            setup.WorkOffset.Y + setup.AlignmentOffsetY,
            setup.WorkOffset.Z + setup.AlignmentOffsetZ);
        if (translation.LengthSquared >= 0.0000001)
        {
            transformGroup.Children.Add(new TranslateTransform3D(translation));
        }

        return transformGroup.Children.Count switch
        {
            0 => Transform3D.Identity,
            1 => transformGroup.Children[0],
            _ => transformGroup
        };
    }

    private static Transform3D BuildStockSetupTransform(JobSetup setup, StockPlacement placement, StockPreviewDimensions dimensions)
    {
        var transformGroup = new Transform3DGroup();
        var center = new Point3D(
            placement.CenterX,
            placement.CenterY,
            placement.TopZ - (dimensions.Height / 2d));
        AddRotation(transformGroup, new Vector3D(1, 0, 0), setup.Stock.RotationA, center);
        AddRotation(transformGroup, new Vector3D(0, 1, 0), setup.Stock.RotationB, center);

        return transformGroup.Children.Count switch
        {
            0 => Transform3D.Identity,
            1 => transformGroup.Children[0],
            _ => transformGroup
        };
    }

    private static void AddRotation(Transform3DGroup transformGroup, Vector3D axis, double angleDegrees)
    {
        if (Math.Abs(angleDegrees) < 0.000001)
        {
            return;
        }

        transformGroup.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(axis, angleDegrees)));
    }

    private static void AddRotation(Transform3DGroup transformGroup, Vector3D axis, double angleDegrees, Point3D center)
    {
        if (Math.Abs(angleDegrees) < 0.000001)
        {
            return;
        }

        transformGroup.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(axis, angleDegrees), center));
    }

    private readonly record struct StockPreviewDimensions(double Length, double Width, double Height, double Diameter);

    private readonly record struct PartReferenceDimensions(double Length, double Width, double Height, double Diameter);

    private readonly record struct StockPlacement(double CenterX, double CenterY, double TopZ);

    private readonly record struct PlaybackSegment(Point3D Start, Point3D End, ToolDefinition Tool, bool IsCutting);

    private static GeometryModel3D CreateBoxModel(
        double minX,
        double minY,
        double minZ,
        double sizeX,
        double sizeY,
        double sizeZ,
        Color color,
        double opacity)
    {
        var maxX = minX + sizeX;
        var maxY = minY + sizeY;
        var maxZ = minZ + sizeZ;
        var mesh = new MeshGeometry3D();

        AddQuad(mesh,
            new Point3D(minX, minY, maxZ),
            new Point3D(maxX, minY, maxZ),
            new Point3D(maxX, maxY, maxZ),
            new Point3D(minX, maxY, maxZ),
            new Vector3D(0, 0, 1));
        AddQuad(mesh,
            new Point3D(minX, maxY, minZ),
            new Point3D(maxX, maxY, minZ),
            new Point3D(maxX, minY, minZ),
            new Point3D(minX, minY, minZ),
            new Vector3D(0, 0, -1));
        AddQuad(mesh,
            new Point3D(minX, minY, minZ),
            new Point3D(maxX, minY, minZ),
            new Point3D(maxX, minY, maxZ),
            new Point3D(minX, minY, maxZ),
            new Vector3D(0, -1, 0));
        AddQuad(mesh,
            new Point3D(maxX, maxY, minZ),
            new Point3D(minX, maxY, minZ),
            new Point3D(minX, maxY, maxZ),
            new Point3D(maxX, maxY, maxZ),
            new Vector3D(0, 1, 0));
        AddQuad(mesh,
            new Point3D(minX, maxY, minZ),
            new Point3D(minX, minY, minZ),
            new Point3D(minX, minY, maxZ),
            new Point3D(minX, maxY, maxZ),
            new Vector3D(-1, 0, 0));
        AddQuad(mesh,
            new Point3D(maxX, minY, minZ),
            new Point3D(maxX, maxY, minZ),
            new Point3D(maxX, maxY, maxZ),
            new Point3D(maxX, minY, maxZ),
            new Vector3D(1, 0, 0));

        return CreateGeometryModel(mesh, color, opacity);
    }

    private static GeometryModel3D CreateCylinderModel(
        double centerX,
        double centerY,
        double bottomZ,
        double topZ,
        double radius,
        Color color,
        double opacity,
        int segments = 36)
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
        int segments = 36)
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

    private static GeometryModel3D CreateSphereModel(
        double centerX,
        double centerY,
        double centerZ,
        double radius,
        Color color,
        double opacity)
    {
        return CreateSphericalSectionModel(centerX, centerY, centerZ, radius, 0, Math.PI, color, opacity);
    }

    private static GeometryModel3D CreateHemisphereModel(
        double centerX,
        double centerY,
        double centerZ,
        double radius,
        bool lowerHalf,
        Color color,
        double opacity)
    {
        return lowerHalf
            ? CreateSphericalSectionModel(centerX, centerY, centerZ, radius, Math.PI / 2d, Math.PI, color, opacity)
            : CreateSphericalSectionModel(centerX, centerY, centerZ, radius, 0, Math.PI / 2d, color, opacity);
    }

    private static GeometryModel3D CreateSphericalSectionModel(
        double centerX,
        double centerY,
        double centerZ,
        double radius,
        double thetaStart,
        double thetaEnd,
        Color color,
        double opacity)
    {
        const int segments = 32;
        const int stacks = 12;
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

    private static IEnumerable<GeometryModel3D> CreateBoxOutlineModels(
        double minX,
        double minY,
        double minZ,
        double sizeX,
        double sizeY,
        double sizeZ,
        Color color,
        double opacity)
    {
        var maxX = minX + sizeX;
        var maxY = minY + sizeY;
        var maxZ = minZ + sizeZ;
        var corners = new[]
        {
            new Point3D(minX, minY, minZ),
            new Point3D(maxX, minY, minZ),
            new Point3D(maxX, maxY, minZ),
            new Point3D(minX, maxY, minZ),
            new Point3D(minX, minY, maxZ),
            new Point3D(maxX, minY, maxZ),
            new Point3D(maxX, maxY, maxZ),
            new Point3D(minX, maxY, maxZ)
        };

        var thickness = Math.Clamp(Math.Max(sizeX, Math.Max(sizeY, sizeZ)) * 0.0035, 0.06, 0.35);
        var edgePairs = new (int A, int B)[]
        {
            (0, 1), (1, 2), (2, 3), (3, 0),
            (4, 5), (5, 6), (6, 7), (7, 4),
            (0, 4), (1, 5), (2, 6), (3, 7)
        };

        foreach (var edgePair in edgePairs)
        {
            yield return CreateSegmentModel(corners[edgePair.A], corners[edgePair.B], thickness, color, opacity);
        }
    }

    private static IEnumerable<GeometryModel3D> CreateCylinderOutlineModels(
        double centerX,
        double centerY,
        double bottomZ,
        double topZ,
        double radius,
        Color color,
        double opacity)
    {
        var segments = 28;
        var thickness = Math.Clamp(radius * 0.008, 0.06, 0.3);
        var topPoints = new List<Point3D>(segments);
        var bottomPoints = new List<Point3D>(segments);
        for (var index = 0; index < segments; index++)
        {
            var angle = (Math.PI * 2d * index) / segments;
            var x = centerX + (Math.Cos(angle) * radius);
            var y = centerY + (Math.Sin(angle) * radius);
            topPoints.Add(new Point3D(x, y, topZ));
            bottomPoints.Add(new Point3D(x, y, bottomZ));
        }

        for (var index = 0; index < segments; index++)
        {
            var next = (index + 1) % segments;
            yield return CreateSegmentModel(topPoints[index], topPoints[next], thickness, color, opacity);
            yield return CreateSegmentModel(bottomPoints[index], bottomPoints[next], thickness, color, opacity * 0.75);
        }

        foreach (var index in new[] { 0, segments / 4, segments / 2, (segments * 3) / 4 })
        {
            yield return CreateSegmentModel(bottomPoints[index], topPoints[index], thickness, color, opacity * 0.8);
        }
    }

    private static GeometryModel3D CreateGeometryModel(MeshGeometry3D mesh, Color color, double opacity)
    {
        var material = CreateMaterial(color, opacity);
        var model = new GeometryModel3D
        {
            Geometry = mesh,
            Material = material,
            BackMaterial = material
        };

        return model;
    }

    private static Material CreateMaterial(Color color, double opacity)
    {
        var alpha = (byte)(Math.Clamp(opacity, 0.05, 1.0) * 255);
        var diffuseBrush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        diffuseBrush.Freeze();
        var highlightBrush = new SolidColorBrush(Color.FromArgb(Math.Min((byte)255, (byte)(alpha + 15)), 255, 255, 255));
        highlightBrush.Freeze();

        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(diffuseBrush));
        material.Children.Add(new SpecularMaterial(highlightBrush, 42));
        return material;
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

    private static void AddQuad(
        MeshGeometry3D mesh,
        Point3D p0,
        Point3D p1,
        Point3D p2,
        Point3D p3,
        Vector3D normal)
    {
        AddQuad(mesh, p0, p1, p2, p3, normal, normal);
    }

    private static void AddQuad(
        MeshGeometry3D mesh,
        Point3D p0,
        Point3D p1,
        Point3D p2,
        Point3D p3,
        Vector3D normal0,
        Vector3D normal1)
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
}
