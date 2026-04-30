using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Media3D;
using GRBL_Cam.Models;

namespace GRBL_Cam.Services;

public sealed class CamStateStore
{
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public CamStateStore(string? stateFilePath = null)
    {
        StateFilePath = stateFilePath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GRBL Cam",
                "cam-state.json");
    }

    public string StateFilePath { get; }

    public CamApplicationState LoadOrCreate()
    {
        if (!File.Exists(StateFilePath))
        {
            var seeded = SeedDataFactory.Create();
            Save(seeded);
            return seeded;
        }

        var json = File.ReadAllText(StateFilePath);
        var state = JsonSerializer.Deserialize<CamApplicationState>(json, _serializerOptions) ?? SeedDataFactory.Create();
        Normalize(state);
        return state;
    }

    public void Save(CamApplicationState state)
    {
        Normalize(state);
        var directory = Path.GetDirectoryName(StateFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(state, _serializerOptions);
        File.WriteAllText(StateFilePath, json);
    }

    private static void Normalize(CamApplicationState state)
    {
        state.MachineProfiles ??= new();
        state.ToolLibrary ??= new();
        state.CurrentJob ??= new();
        state.CurrentJob.Setups ??= new();
        state.CurrentJob.Setup ??= new();
        state.CurrentJob.Operations ??= new();

        foreach (var machine in state.MachineProfiles)
        {
            machine.Axes ??= new();
            machine.HomePoint ??= new();
        }

        foreach (var tool in state.ToolLibrary)
        {
            NormalizeTool(tool);
        }

        NormalizeSetup(state.CurrentJob.Setup, "Setup 1", isFirstSetup: true);
        var hadSavedSetups = state.CurrentJob.Setups.Count > 0;

        if (!hadSavedSetups)
        {
            if (state.CurrentJob.Operations.Count > 0 && state.CurrentJob.Setup.Operations.Count == 0)
            {
                foreach (var operation in state.CurrentJob.Operations)
                {
                    state.CurrentJob.Setup.Operations.Add(operation);
                }
            }

            state.CurrentJob.Setups.Add(state.CurrentJob.Setup);
        }

        for (var index = 0; index < state.CurrentJob.Setups.Count; index++)
        {
            NormalizeSetup(state.CurrentJob.Setups[index], $"Setup {index + 1}", index == 0);
        }

        var activeSetup = state.CurrentJob.Setups[0];
        if (!hadSavedSetups && state.CurrentJob.Operations.Count > 0 && activeSetup.Operations.Count == 0)
        {
            foreach (var operation in state.CurrentJob.Operations)
            {
                activeSetup.Operations.Add(operation);
            }
        }

        state.CurrentJob.Setup = activeSetup;
        state.CurrentJob.Operations = activeSetup.Operations;

        foreach (var operation in state.CurrentJob.Setups.SelectMany(setup => setup.Operations))
        {
            NormalizeOperation(operation);
        }

        if (string.IsNullOrWhiteSpace(state.CurrentJob.MachineProfileName) && state.MachineProfiles.Count > 0)
        {
            state.CurrentJob.MachineProfileName = state.MachineProfiles[0].Name;
        }
    }

    private static void NormalizeSetup(JobSetup setup, string fallbackName, bool isFirstSetup)
    {
        setup.Part ??= new();
        setup.Stock ??= new();
        setup.WorkOrigin ??= new();
        setup.WorkOffset ??= new();
        setup.Operations ??= new();
        if (string.IsNullOrWhiteSpace(setup.Name))
        {
            setup.Name = fallbackName;
        }

        if (isFirstSetup)
        {
            setup.TransferRestFromPreviousSetup = false;
        }
    }

    private static void NormalizeOperation(ToolpathOperationDefinition operation)
    {
        operation.Feature ??= new();
        operation.Feature.PathPoints ??= new();
        operation.Feature.KeepoutLoops ??= new();
        foreach (var keepoutLoop in operation.Feature.KeepoutLoops)
        {
            keepoutLoop.Points ??= new();
        }
    }

    private static void NormalizeTool(ToolDefinition tool)
    {
        if (tool.CuttingDiameter <= 0)
        {
            tool.CuttingDiameter = 6;
        }

        if (tool.ShankDiameter <= 0)
        {
            tool.ShankDiameter = tool.CuttingDiameter;
        }

        if (tool.CuttingLength <= 0)
        {
            tool.CuttingLength = 20;
        }

        if (tool.FluteLength <= 0)
        {
            tool.FluteLength = tool.CuttingLength;
        }

        if (tool.StickOut <= 0)
        {
            tool.StickOut = Math.Max(tool.CuttingLength, tool.FluteLength) + 12;
        }

        if (tool.OverallLength <= 0)
        {
            tool.OverallLength = Math.Max(tool.StickOut, Math.Max(tool.CuttingLength, tool.FluteLength)) + 20;
        }

        if (tool.FluteCount <= 0)
        {
            tool.FluteCount = tool.Style is ToolStyle.Drill or ToolStyle.SpotDrill or ToolStyle.CenterDrill ? 2 : 3;
        }

        if (tool.TipAngleDegrees <= 0)
        {
            tool.TipAngleDegrees = GetDefaultTipAngle(tool.Style);
        }

        if (tool.Style == ToolStyle.Ball && tool.CornerRadius <= 0)
        {
            tool.CornerRadius = tool.CuttingDiameter / 2d;
        }
    }

    private static double GetDefaultTipAngle(ToolStyle style)
    {
        return style switch
        {
            ToolStyle.Drill => 118,
            ToolStyle.SpotDrill => 90,
            ToolStyle.CenterDrill => 60,
            ToolStyle.VPoint or ToolStyle.VBit or ToolStyle.Engraver => 90,
            ToolStyle.Chamfer => 90,
            _ => 0
        };
    }
}

public static class SeedDataFactory
{
    public static CamApplicationState Create()
    {
        var machine = new MachineProfile
        {
            Name = "GRBL Bench Mill",
            Family = MachineFamily.Mill,
            Kinematics = KinematicsMode.ThreeAxisMill,
            Controller = "GRBL",
            SupportsAutomaticToolChange = false,
            SpindleMinRpm = 600,
            SpindleMaxRpm = 24000,
            MaxRapidRate = 5000,
            HomePoint = new Pose6D { X = 0, Y = 0, Z = 0 },
            Notes = "3-axis milling starter profile. Future indexed and simultaneous axes hang off the kinematics setting."
        };

        machine.Axes.Add(new MachineAxis { Name = "X", Type = AxisType.Linear, TravelMin = 0, TravelMax = 300, MaxFeedRate = 4000, HomePosition = 0 });
        machine.Axes.Add(new MachineAxis { Name = "Y", Type = AxisType.Linear, TravelMin = 0, TravelMax = 220, MaxFeedRate = 4000, HomePosition = 0 });
        machine.Axes.Add(new MachineAxis { Name = "Z", Type = AxisType.Linear, TravelMin = -180, TravelMax = 0, MaxFeedRate = 1800, HomePosition = 0 });

        var tools = new List<ToolDefinition>
        {
            new()
            {
                Number = 1,
                Name = "12 mm Face / Rougher",
                Style = ToolStyle.Square,
                CuttingDiameter = 12,
                ShankDiameter = 12,
                CuttingLength = 28,
                FluteLength = 28,
                StickOut = 45,
                OverallLength = 80,
                FluteCount = 3,
                MaxStepDown = 1.5,
                MaxStepOver = 7.5,
                Notes = "Use for facing and large-area roughing."
            },
            new()
            {
                Number = 2,
                Name = "6 mm Flat End Mill",
                Style = ToolStyle.Square,
                CuttingDiameter = 6,
                ShankDiameter = 6,
                CuttingLength = 18,
                FluteLength = 18,
                StickOut = 32,
                OverallLength = 65,
                FluteCount = 3,
                MaxStepDown = 2.0,
                MaxStepOver = 3.0,
                Notes = "General pocketing and profile tool."
            },
            new()
            {
                Number = 3,
                Name = "3 mm Drill",
                Style = ToolStyle.Drill,
                CuttingDiameter = 3,
                TipDiameter = 0,
                TipAngleDegrees = 118,
                CuttingLength = 20,
                FluteLength = 20,
                StickOut = 35,
                OverallLength = 60,
                FluteCount = 2,
                MaxStepDown = 4,
                MaxStepOver = 0,
                Notes = "Used for hole patterns."
            },
            new()
            {
                Number = 4,
                Name = "6 mm Ball End Mill",
                Style = ToolStyle.Ball,
                CuttingDiameter = 6,
                ShankDiameter = 6,
                CuttingLength = 18,
                FluteLength = 18,
                CornerRadius = 3,
                StickOut = 34,
                OverallLength = 65,
                FluteCount = 2,
                MaxStepDown = 1,
                MaxStepOver = 1.5,
                Notes = "Finishing and 3D surface tool."
            },
            new()
            {
                Number = 5,
                Name = "90 deg Chamfer Mill",
                Style = ToolStyle.Chamfer,
                CuttingDiameter = 8,
                ShankDiameter = 6,
                CuttingLength = 6,
                FluteLength = 6,
                TipDiameter = 1,
                TipAngleDegrees = 90,
                StickOut = 28,
                OverallLength = 50,
                FluteCount = 2,
                MaxStepDown = 0.5,
                MaxStepOver = 0.8,
                Notes = "Chamfer and edge break tool."
            }
        };

        var job = new CamJob
        {
            Name = "Demo 3-Axis Mill Job",
            Units = UnitsMode.Millimeters,
            MachineProfileName = machine.Name,
            Setup = new JobSetup
            {
                Name = "Setup 1",
                TransferRestFromPreviousSetup = false,
                Part = new PartDefinition
                {
                    Name = "Imported STEP Placeholder",
                    SourceType = PartSourceType.StepFile,
                    SourcePath = string.Empty,
                    LengthX = 120,
                    WidthY = 70,
                    HeightZ = 18
                },
                WorkOrigin = new Pose6D(),
                OriginSource = SetupOriginSource.Stock,
                OriginAnchor = SetupOriginAnchor.TopCenter,
                Stock = new StockDefinition
                {
                    Shape = StockShape.Box,
                    LengthX = 140,
                    WidthY = 90,
                    HeightZ = 25,
                    RadialAllowance = 2,
                    AxialAllowance = 1
                },
                WorkOffset = new Pose6D(),
                SafeZ = 15,
                ClearanceZ = 5,
                WorkOffsetCode = "G54",
                Notes = "WCS origin is stock top center in this starter workflow."
            }
        };

        job.Setups.Add(job.Setup);
        job.Operations = job.Setup.Operations;

        job.Operations.Add(new ToolpathOperationDefinition
        {
            Name = "Face Top",
            Type = OperationType.Facing,
            ToolNumber = 1,
            SpindleSpeed = 12000,
            FeedRate = 1500,
            PlungeRate = 250,
            StepDown = 0.5,
            StepOver = 7,
            PathCount = 1,
            SafeRetractZ = 8,
            Feature = new FeatureDefinition
            {
                Name = "Stock Top",
                Shape = FeatureShape.Rectangle,
                Depth = 0.5
            }
        });

        job.Operations.Add(new ToolpathOperationDefinition
        {
            Name = "Center Pocket",
            Type = OperationType.Pocket,
            ToolNumber = 2,
            SpindleSpeed = 16000,
            FeedRate = 900,
            PlungeRate = 250,
            StepDown = 2,
            StepOver = 2.8,
            PathCount = 1,
            UseRestMachining = true,
            SafeRetractZ = 6,
            Feature = new FeatureDefinition
            {
                Name = "Rectangular Pocket",
                Shape = FeatureShape.Rectangle,
                CenterX = 0,
                CenterY = 0,
                Length = 80,
                Width = 42,
                Depth = 8
            }
        });

        job.Operations.Add(new ToolpathOperationDefinition
        {
            Name = "Outer Profile",
            Type = OperationType.Profile,
            ToolNumber = 2,
            SpindleSpeed = 15000,
            FeedRate = 800,
            PlungeRate = 220,
            StepDown = 2,
            StepOver = 2,
            PathCount = 2,
            SafeRetractZ = 6,
            Feature = new FeatureDefinition
            {
                Name = "Outside Contour",
                Shape = FeatureShape.Rectangle,
                CenterX = 0,
                CenterY = 0,
                Length = 120,
                Width = 70,
                Depth = 18,
                InsideProfile = false
            }
        });

        job.Operations.Add(new ToolpathOperationDefinition
        {
            Name = "Bolt Pattern",
            Type = OperationType.Drill,
            ToolNumber = 3,
            SpindleSpeed = 9000,
            FeedRate = 250,
            PlungeRate = 150,
            StepDown = 4,
            StepOver = 0,
            PathCount = 1,
            SafeRetractZ = 6,
            DrillPeckDepth = 4,
            DrillRetractDistance = 1,
            DrillFullRetract = true,
            Feature = new FeatureDefinition
            {
                Name = "3x2 Hole Pattern",
                Shape = FeatureShape.HolePattern,
                CenterX = 0,
                CenterY = 0,
                Depth = 12,
                Rows = 2,
                Columns = 3,
                PitchX = 30,
                PitchY = 24
            }
        });

        return new CamApplicationState
        {
            MachineProfiles = new System.Collections.ObjectModel.ObservableCollection<MachineProfile> { machine },
            ToolLibrary = new System.Collections.ObjectModel.ObservableCollection<ToolDefinition>(tools),
            CurrentJob = job
        };
    }
}

public enum MotionMode
{
    Rapid,
    Linear
}

public sealed class ToolpathMove
{
    public MotionMode Mode { get; init; }

    public double X { get; init; }

    public double Y { get; init; }

    public double Z { get; init; }

    public double FeedRate { get; init; }

    public bool IsCutting { get; init; }

    public string Comment { get; init; } = string.Empty;
}

public sealed class OperationToolpath
{
    public JobSetup Setup { get; init; } = new();

    public ToolpathOperationDefinition Operation { get; init; } = new();

    public ToolDefinition Tool { get; init; } = new();

    public List<ToolpathMove> Moves { get; init; } = new();

    public string Summary { get; init; } = string.Empty;
}

public sealed class SimulationReport
{
    public int Width { get; init; }

    public int Height { get; init; }

    public byte[] PixelData { get; init; } = Array.Empty<byte>();

    public double RemovedVolume { get; init; }

    public double DeepestZ { get; init; }

    public int TouchedCells { get; init; }

    public string Summary { get; init; } = "No simulation data.";
}

internal readonly record struct Bounds2D(double MinX, double MaxX, double MinY, double MaxY, double TopZ);

internal readonly record struct Point3(double X, double Y, double Z);

internal readonly record struct Point2(double X, double Y);

internal readonly record struct CutInterval(double StartX, double EndX);

internal readonly record struct WaterlineSegment(Point3 Start, Point3 End);

internal static class CutterGeometry
{
    public static double Radius(ToolDefinition tool) => Math.Max(tool.CuttingDiameter, 0.5) / 2d;

    public static double BottomProfileHeightAtRadius(ToolDefinition tool, double radialDistance)
    {
        var toolRadius = Radius(tool);
        var distance = Math.Clamp(radialDistance, 0, toolRadius);

        return tool.Style switch
        {
            ToolStyle.Ball or ToolStyle.Lollipop => BallHeight(toolRadius, distance),
            ToolStyle.Bull => BullCornerHeight(tool, toolRadius, distance),
            ToolStyle.Drill or ToolStyle.SpotDrill or ToolStyle.CenterDrill
                or ToolStyle.VPoint or ToolStyle.VBit or ToolStyle.Chamfer or ToolStyle.Engraver
                => ConicalHeight(tool, toolRadius, distance),
            _ => 0
        };
    }

    public static double ConicalTipLength(ToolDefinition tool)
    {
        if (tool.Style is not (ToolStyle.Drill or ToolStyle.SpotDrill or ToolStyle.CenterDrill
            or ToolStyle.VPoint or ToolStyle.VBit or ToolStyle.Chamfer or ToolStyle.Engraver))
        {
            return 0;
        }

        var includedAngle = tool.TipAngleDegrees > 0 ? tool.TipAngleDegrees : tool.TaperAngleDegrees;
        if (includedAngle <= 0 || includedAngle >= 179)
        {
            return 0;
        }

        var majorRadius = Radius(tool);
        var tipRadius = Math.Clamp(tool.TipDiameter, 0, tool.CuttingDiameter) / 2d;
        return Math.Max(0, (majorRadius - tipRadius) / Math.Tan(DegreesToRadians(includedAngle / 2d)));
    }

    private static double BallHeight(double radius, double radialDistance)
    {
        if (radius <= 0.0001)
        {
            return 0;
        }

        var clamped = Math.Clamp(radialDistance, 0, radius);
        return radius - Math.Sqrt(Math.Max(0, (radius * radius) - (clamped * clamped)));
    }

    private static double BullCornerHeight(ToolDefinition tool, double toolRadius, double radialDistance)
    {
        var cornerRadius = Math.Clamp(tool.CornerRadius, 0, toolRadius);
        if (cornerRadius <= 0.0001)
        {
            return 0;
        }

        var flatRadius = Math.Max(0, toolRadius - cornerRadius);
        if (radialDistance <= flatRadius)
        {
            return 0;
        }

        return cornerRadius - Math.Sqrt(Math.Max(0, (cornerRadius * cornerRadius) - Math.Pow(radialDistance - flatRadius, 2)));
    }

    private static double ConicalHeight(ToolDefinition tool, double toolRadius, double radialDistance)
    {
        var includedAngle = tool.TipAngleDegrees > 0 ? tool.TipAngleDegrees : tool.TaperAngleDegrees;
        if (includedAngle <= 0 || includedAngle >= 179)
        {
            return 0;
        }

        var tipRadius = Math.Clamp(tool.TipDiameter, 0, tool.CuttingDiameter) / 2d;
        if (radialDistance <= tipRadius)
        {
            return 0;
        }

        var slope = 1d / Math.Tan(DegreesToRadians(includedAngle / 2d));
        return Math.Min(ConicalTipLength(tool), Math.Max(0, radialDistance - tipRadius) * slope);
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}

internal static class StockBoundsResolver
{
    public static Bounds2D GetStockBounds(JobSetup setup, PreviewGeometryLoader? geometryLoader)
    {
        var partGeometry = geometryLoader?.TryLoadPartGeometry(setup);
        return GetStockBounds(setup, partGeometry?.Bounds);
    }

    public static Bounds2D GetStockBounds(JobSetup setup, Rect3D? partBounds = null)
    {
        var radialAllowance = Math.Max(setup.Stock.RadialAllowance, 0);
        double centerX;
        double centerY;
        double topZ;
        double fallbackLength;
        double fallbackWidth;
        double fallbackHeight;
        double fallbackDiameter;

        if (partBounds.HasValue &&
            !partBounds.Value.IsEmpty &&
            partBounds.Value.SizeX > 0 &&
            partBounds.Value.SizeY > 0)
        {
            var bounds = partBounds.Value;
            centerX = bounds.X + (bounds.SizeX / 2d) + setup.Stock.OffsetX;
            centerY = bounds.Y + (bounds.SizeY / 2d) + setup.Stock.OffsetY;
            topZ = bounds.Z + bounds.SizeZ + setup.Stock.OffsetZ;
            fallbackLength = bounds.SizeX + (radialAllowance * 2d);
            fallbackWidth = bounds.SizeY + (radialAllowance * 2d);
            fallbackHeight = bounds.SizeZ + Math.Max(setup.Stock.AxialAllowance, 0);
            fallbackDiameter = Math.Max(bounds.SizeX, bounds.SizeY) + (radialAllowance * 2d);
        }
        else
        {
            centerX = setup.WorkOffset.X + setup.AlignmentOffsetX + setup.Stock.OffsetX;
            centerY = setup.WorkOffset.Y + setup.AlignmentOffsetY + setup.Stock.OffsetY;
            topZ = setup.WorkOffset.Z + setup.AlignmentOffsetZ + setup.Stock.OffsetZ;
            fallbackLength = Math.Max(setup.Part.LengthX, 2d) + (radialAllowance * 2d);
            fallbackWidth = Math.Max(setup.Part.WidthY, 2d) + (radialAllowance * 2d);
            fallbackHeight = Math.Max(setup.Part.HeightZ, 2d) + Math.Max(setup.Stock.AxialAllowance, 0);
            fallbackDiameter = Math.Max(setup.Part.Diameter, Math.Max(fallbackLength, fallbackWidth));
        }

        var stockDiameter = setup.Stock.Diameter > 0 ? setup.Stock.Diameter : fallbackDiameter;
        var length = setup.Stock.Shape == StockShape.Cylinder
            ? stockDiameter
            : setup.Stock.LengthX > 0 ? setup.Stock.LengthX : fallbackLength;
        var width = setup.Stock.Shape == StockShape.Cylinder
            ? stockDiameter
            : setup.Stock.WidthY > 0 ? setup.Stock.WidthY : fallbackWidth;
        var height = setup.Stock.HeightZ > 0 ? setup.Stock.HeightZ : fallbackHeight;

        var stockBounds = new Rect3D(
            centerX - (Math.Max(length, 2d) / 2d),
            centerY - (Math.Max(width, 2d) / 2d),
            topZ - Math.Max(height, 2d),
            Math.Max(length, 2d),
            Math.Max(width, 2d),
            Math.Max(height, 2d));
        var transform = BuildStockBoundsTransform(setup.Stock, stockBounds);
        if (!transform.Value.IsIdentity)
        {
            stockBounds = transform.TransformBounds(stockBounds);
        }

        return new Bounds2D(
            stockBounds.X,
            stockBounds.X + stockBounds.SizeX,
            stockBounds.Y,
            stockBounds.Y + stockBounds.SizeY,
            stockBounds.Z + stockBounds.SizeZ);
    }

    public static Bounds2D ToToolpathBounds(JobSetup setup, Bounds2D stockBounds)
    {
        return new Bounds2D(
            stockBounds.MinX,
            stockBounds.MaxX,
            stockBounds.MinY,
            stockBounds.MaxY,
            setup.WorkOffset.Z);
    }

    private static Transform3D BuildStockBoundsTransform(StockDefinition stock, Rect3D bounds)
    {
        var transformGroup = new Transform3DGroup();
        var center = new Point3D(
            bounds.X + (bounds.SizeX / 2d),
            bounds.Y + (bounds.SizeY / 2d),
            bounds.Z + (bounds.SizeZ / 2d));
        AddRotation(transformGroup, new Vector3D(1, 0, 0), stock.RotationA, center);
        AddRotation(transformGroup, new Vector3D(0, 1, 0), stock.RotationB, center);
        return transformGroup.Children.Count switch
        {
            0 => Transform3D.Identity,
            1 => transformGroup.Children[0],
            _ => transformGroup
        };
    }

    private static void AddRotation(Transform3DGroup transformGroup, Vector3D axis, double angleDegrees, Point3D center)
    {
        if (Math.Abs(angleDegrees) < 0.000001)
        {
            return;
        }

        transformGroup.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(axis, angleDegrees), center));
    }
}

public sealed class ThreeAxisToolpathPlanner
{
    private readonly PreviewGeometryLoader _geometryLoader = new();

    public List<OperationToolpath> Plan(MachineProfile machine, CamJob job, IReadOnlyList<ToolDefinition> toolLibrary)
    {
        _ = machine;
        var lookup = toolLibrary.ToDictionary(tool => tool.Number);
        var operationPlans = new List<OperationToolpath>();
        RestStockMap? restMap = null;

        foreach (var setup in GetPlanSetups(job))
        {
            var partGeometry = _geometryLoader.TryLoadPartGeometry(setup);
            var previewStockBounds = StockBoundsResolver.GetStockBounds(setup, partGeometry?.Bounds);
            var bounds = StockBoundsResolver.ToToolpathBounds(setup, previewStockBounds);
            if (restMap is null || !setup.TransferRestFromPreviousSetup || !restMap.CanRepresent(bounds))
            {
                restMap = new RestStockMap(bounds);
            }

            foreach (var operation in setup.Operations.Where(op => op.Enabled))
            {
                if (!lookup.TryGetValue(operation.ToolNumber, out var tool))
                {
                    operationPlans.Add(new OperationToolpath
                    {
                        Setup = setup,
                        Operation = operation,
                        Tool = new ToolDefinition { Number = operation.ToolNumber, Name = "Missing Tool" },
                        Summary = $"Skipped {operation.Name} because tool T{operation.ToolNumber} does not exist."
                    });
                    continue;
                }

                if (!TryValidate3AxisOperation(setup, operation, tool, partGeometry, bounds, out var validationMessage))
                {
                    operationPlans.Add(CreateSkippedToolpath(setup, operation, tool, validationMessage));
                    continue;
                }

                var moves = operation.Type switch
                {
                    OperationType.Facing => PlanFacing(setup, operation, tool, bounds),
                    OperationType.BulkRemoval => PlanBulkRemoval(setup, operation, tool, bounds),
                    OperationType.Raster => PlanRaster(setup, operation, tool, bounds, restMap),
                    OperationType.AdaptiveClearing => PlanAdaptiveClearing(setup, operation, tool, bounds, restMap),
                    OperationType.Pocket => PlanPocket(setup, operation, tool),
                    OperationType.ZLevelFinishing => PlanZLevelFinishing(setup, operation, tool, partGeometry, previewStockBounds.TopZ),
                    OperationType.Parallel3DFinishing => PlanParallel3DFinishing(setup, operation, tool, partGeometry, previewStockBounds.TopZ),
                    OperationType.ScallopFinishing => PlanScallopFinishing(setup, operation, tool, partGeometry, previewStockBounds.TopZ),
                    OperationType.PencilCleanup => PlanPencilCleanup(setup, operation, tool, partGeometry, previewStockBounds.TopZ),
                    OperationType.Profile => PlanProfile(setup, operation, tool),
                    OperationType.Contour2D => PlanContour2D(setup, operation, tool),
                    OperationType.Chamfer => PlanChamfer(setup, operation, tool),
                    OperationType.Boring => PlanBoring(setup, operation, tool),
                    OperationType.Drill => PlanDrill(setup, operation, tool),
                    _ => new List<ToolpathMove>()
                };

                if (!TryValidatePlannedMoves(moves, out var moveValidationMessage))
                {
                    operationPlans.Add(CreateSkippedToolpath(setup, operation, tool, moveValidationMessage));
                    continue;
                }

                restMap.ApplyMoves(moves, tool);

                operationPlans.Add(new OperationToolpath
                {
                    Setup = setup,
                    Operation = operation,
                    Tool = tool,
                    Moves = moves,
                    Summary = BuildOperationSummary(operation, moves)
                });
            }
        }

        return operationPlans;
    }

    private static IReadOnlyList<JobSetup> GetPlanSetups(CamJob job)
    {
        if (job.Setups.Count > 0)
        {
            return job.Setups;
        }

        if (job.Setup.Operations.Count == 0 && job.Operations.Count > 0)
        {
            foreach (var operation in job.Operations)
            {
                job.Setup.Operations.Add(operation);
            }
        }

        return new[] { job.Setup };
    }

    private static OperationToolpath CreateSkippedToolpath(
        JobSetup setup,
        ToolpathOperationDefinition operation,
        ToolDefinition tool,
        string reason)
    {
        return new OperationToolpath
        {
            Setup = setup,
            Operation = operation,
            Tool = tool,
            Summary = $"Skipped {operation.Name}: {reason}"
        };
    }

    private static bool TryValidate3AxisOperation(
        JobSetup setup,
        ToolpathOperationDefinition operation,
        ToolDefinition tool,
        LoadedPreviewGeometry? partGeometry,
        Bounds2D bounds,
        out string message)
    {
        message = string.Empty;
        if (!IsFiniteOperation(operation))
        {
            message = "one or more operation numeric settings are invalid.";
            return false;
        }

        if (!double.IsFinite(setup.WorkOffset.Z) || !double.IsFinite(setup.ClearanceZ) || !double.IsFinite(setup.SafeZ))
        {
            message = "setup Z, clearance, or safe height is invalid.";
            return false;
        }

        if (tool.CuttingDiameter <= 0 || !double.IsFinite(tool.CuttingDiameter))
        {
            message = $"tool T{tool.Number} needs a positive cutting diameter.";
            return false;
        }

        if (operation.FeedRate <= 0)
        {
            message = "feed rate must be greater than zero.";
            return false;
        }

        if (operation.PlungeRate <= 0)
        {
            message = "plunge rate must be greater than zero.";
            return false;
        }

        if (RequiresPlannerStepDown(operation.Type) && operation.StepDown <= 0)
        {
            message = "stepdown/sample step must be greater than zero.";
            return false;
        }

        if (RequiresPlannerStepOver(operation.Type) && operation.StepOver <= 0)
        {
            message = "stepover must be greater than zero.";
            return false;
        }

        if (RequiresPlannerDepth(operation.Type) && operation.Feature.Depth <= 0)
        {
            message = "feature depth must be greater than zero.";
            return false;
        }

        if (RequiresSurfaceGeometry(operation.Type) && (partGeometry is null || partGeometry.SurfaceTriangles.Count == 0 || partGeometry.Bounds.IsEmpty))
        {
            message = "this model-aware 3D operation needs loaded STEP surface geometry.";
            return false;
        }

        if (operation.Type == OperationType.Chamfer && (operation.Feature.Shape != FeatureShape.EdgePath || operation.Feature.PathPoints.Count < 2))
        {
            message = "chamfer requires a selected model edge/path.";
            return false;
        }

        if ((operation.Type is OperationType.Profile or OperationType.Contour2D)
            && operation.Feature.Shape == FeatureShape.EdgePath
            && operation.Feature.PathPoints.Count < 2)
        {
            message = $"{operation.Type} edge-path mode requires a selected model edge/path.";
            return false;
        }

        if (operation.Type == OperationType.Boring)
        {
            var minimumBoreDiameter = tool.CuttingDiameter + (Math.Max(0, operation.FinishStockRadial) * 2d);
            if (operation.Feature.Shape is not (FeatureShape.Circle or FeatureShape.HolePattern))
            {
                message = "boring requires a circular feature or hole pattern.";
                return false;
            }

            if (operation.Feature.Diameter <= minimumBoreDiameter)
            {
                message = $"bore diameter {operation.Feature.Diameter:0.###} must be larger than the tool plus radial stock allowance ({minimumBoreDiameter:0.###}).";
                return false;
            }
        }

        if (operation.Feature.Shape == FeatureShape.Circle
            && operation.Feature.InsideProfile
            && (operation.Type is OperationType.Profile or OperationType.Contour2D or OperationType.Pocket)
            && operation.Feature.Diameter <= tool.CuttingDiameter + (Math.Max(0, operation.FinishStockRadial) * 2d))
        {
            message = $"{operation.Type} circle diameter is too small for the selected tool and radial stock allowance.";
            return false;
        }

        if (operation.Feature.Shape == FeatureShape.Rectangle
            && (operation.Type is OperationType.Pocket or OperationType.Profile or OperationType.Contour2D)
            && (operation.Feature.Length <= 0 || operation.Feature.Width <= 0))
        {
            message = $"{operation.Type} rectangle length and width must be greater than zero.";
            return false;
        }

        if (operation.Type is OperationType.BulkRemoval or OperationType.Raster or OperationType.AdaptiveClearing)
        {
            var floorZ = bounds.TopZ + operation.Feature.StartZ + Math.Max(0, operation.FinishStockAxial);
            if (floorZ >= bounds.TopZ - 0.0001)
            {
                message = "selected roughing floor is at or above the stock top.";
                return false;
            }
        }

        return true;
    }

    private static bool TryValidatePlannedMoves(IReadOnlyList<ToolpathMove> moves, out string message)
    {
        message = string.Empty;
        foreach (var move in moves)
        {
            if (!double.IsFinite(move.X) || !double.IsFinite(move.Y) || !double.IsFinite(move.Z) || !double.IsFinite(move.FeedRate))
            {
                message = "planner produced a non-finite move coordinate or feed rate.";
                return false;
            }
        }

        return true;
    }

    private static bool IsFiniteOperation(ToolpathOperationDefinition operation)
    {
        return double.IsFinite(operation.SpindleSpeed)
            && double.IsFinite(operation.FeedRate)
            && double.IsFinite(operation.PlungeRate)
            && double.IsFinite(operation.StepDown)
            && double.IsFinite(operation.StepOver)
            && double.IsFinite(operation.FinishStockRadial)
            && double.IsFinite(operation.FinishStockAxial)
            && double.IsFinite(operation.SafeRetractZ)
            && double.IsFinite(operation.LeadInLength)
            && double.IsFinite(operation.LeadOutLength)
            && double.IsFinite(operation.TabWidth)
            && double.IsFinite(operation.TabHeight)
            && double.IsFinite(operation.DrillPeckDepth)
            && double.IsFinite(operation.DrillRetractDistance)
            && double.IsFinite(operation.Feature.CenterX)
            && double.IsFinite(operation.Feature.CenterY)
            && double.IsFinite(operation.Feature.StartZ)
            && double.IsFinite(operation.Feature.Depth)
            && double.IsFinite(operation.Feature.Length)
            && double.IsFinite(operation.Feature.Width)
            && double.IsFinite(operation.Feature.Diameter)
            && double.IsFinite(operation.Feature.PitchX)
            && double.IsFinite(operation.Feature.PitchY)
            && double.IsFinite(operation.Feature.RotationDegrees);
    }

    private static bool RequiresPlannerStepDown(OperationType type)
    {
        return type is not OperationType.Drill;
    }

    private static bool RequiresPlannerStepOver(OperationType type)
    {
        return type is OperationType.Facing
            or OperationType.BulkRemoval
            or OperationType.Raster
            or OperationType.AdaptiveClearing
            or OperationType.Pocket
            or OperationType.ZLevelFinishing
            or OperationType.Parallel3DFinishing
            or OperationType.ScallopFinishing;
    }

    private static bool RequiresPlannerDepth(OperationType type)
    {
        return type is OperationType.Facing
            or OperationType.BulkRemoval
            or OperationType.Raster
            or OperationType.AdaptiveClearing
            or OperationType.Pocket
            or OperationType.Profile
            or OperationType.Contour2D
            or OperationType.Boring
            or OperationType.Drill;
    }

    private static bool RequiresSurfaceGeometry(OperationType type)
    {
        return type is OperationType.ZLevelFinishing
            or OperationType.Parallel3DFinishing
            or OperationType.ScallopFinishing
            or OperationType.PencilCleanup;
    }

    private static List<ToolpathMove> PlanFacing(JobSetup setup, ToolpathOperationDefinition operation, ToolDefinition tool, Bounds2D bounds)
    {
        var toolRadius = GetToolRadius(tool);
        var safeZ = setup.WorkOffset.Z + Math.Max(setup.SafeZ, operation.SafeRetractZ);
        var clearanceZ = setup.WorkOffset.Z + setup.ClearanceZ;
        var finalZ = setup.WorkOffset.Z + operation.Feature.StartZ - Math.Max(0, operation.Feature.Depth - operation.FinishStockAxial);
        var stepDown = operation.StepDown > 0 ? operation.StepDown : Math.Max(0.5, operation.Feature.Depth);
        var depthLevels = BuildDepthLevels(setup.WorkOffset.Z + operation.Feature.StartZ, finalZ, stepDown);

        var xStart = bounds.MinX - toolRadius;
        var xEnd = bounds.MaxX + toolRadius;
        var yStart = bounds.MinY - toolRadius;
        var yEnd = bounds.MaxY + toolRadius;
        var laneYs = BuildLinearPasses(yStart, yEnd, operation.StepOver > 0 ? operation.StepOver : tool.CuttingDiameter * 0.65);
        var moves = new List<ToolpathMove>();

        foreach (var level in depthLevels)
        {
            if (laneYs.Count == 0)
            {
                continue;
            }

            var reverse = false;
            var firstY = laneYs[0];
            Rapid(moves, xStart, firstY, safeZ, $"Facing at Z{FormatValue(level)}");
            Rapid(moves, xStart, firstY, clearanceZ);
            Line(moves, xStart, firstY, level, operation.PlungeRate, false);

            for (var index = 0; index < laneYs.Count; index++)
            {
                var currentY = laneYs[index];
                var startX = reverse ? xEnd : xStart;
                var endX = reverse ? xStart : xEnd;

                if (index > 0)
                {
                    Line(moves, startX, currentY, level, operation.FeedRate, true);
                }

                Line(moves, endX, currentY, level, operation.FeedRate, true);

                if (index < laneYs.Count - 1)
                {
                    var nextY = laneYs[index + 1];
                    Line(moves, endX, nextY, level, operation.FeedRate, true);
                }

                reverse = !reverse;
            }

            Rapid(moves, reverse ? xStart : xEnd, laneYs[^1], safeZ);
        }

        return moves;
    }

    private static List<ToolpathMove> PlanBulkRemoval(JobSetup setup, ToolpathOperationDefinition operation, ToolDefinition tool, Bounds2D bounds)
    {
        var moves = new List<ToolpathMove>();
        var toolRadius = GetToolRadius(tool);
        var safeZ = setup.WorkOffset.Z + Math.Max(setup.SafeZ, operation.SafeRetractZ);
        var clearanceZ = setup.WorkOffset.Z + setup.ClearanceZ;
        var floorZ = bounds.TopZ + operation.Feature.StartZ + Math.Max(0, operation.FinishStockAxial);
        if (floorZ >= bounds.TopZ - 0.0001)
        {
            return moves;
        }

        var stepDown = operation.StepDown > 0
            ? operation.StepDown
            : Math.Max(0.5, tool.MaxStepDown > 0 ? tool.MaxStepDown : Math.Abs(bounds.TopZ - floorZ));
        var stepOver = operation.StepOver > 0
            ? operation.StepOver
            : Math.Max(toolRadius, tool.MaxStepOver > 0 ? tool.MaxStepOver : tool.CuttingDiameter * 0.55);
        var depthLevels = BuildDepthLevels(bounds.TopZ, floorZ, stepDown);
        var laneYs = BuildBulkRemovalLanes(setup, bounds, toolRadius, stepOver);
        var keepoutOffset = toolRadius + Math.Max(0, operation.FinishStockRadial);
        var keepouts = BuildBulkKeepoutPolygons(setup, operation.Feature, keepoutOffset);

        foreach (var level in depthLevels)
        {
            var reverse = false;
            var hasActiveCut = false;
            var activeX = 0d;
            var activeY = 0d;

            for (var laneIndex = 0; laneIndex < laneYs.Count; laneIndex++)
            {
                var y = laneYs[laneIndex];
                var intervals = BuildClearIntervalsAtY(setup, bounds, toolRadius, keepouts, y);
                if (reverse)
                {
                    intervals.Reverse();
                }

                foreach (var interval in intervals)
                {
                    var startX = reverse ? interval.EndX : interval.StartX;
                    var endX = reverse ? interval.StartX : interval.EndX;
                    if (Math.Abs(endX - startX) < Math.Max(0.05, toolRadius * 0.15))
                    {
                        continue;
                    }

                    if (!hasActiveCut || !CanCutCrossover(activeX, activeY, startX, y, keepouts, stepOver))
                    {
                        if (hasActiveCut)
                        {
                            Rapid(moves, activeX, activeY, clearanceZ);
                        }

                        Rapid(moves, startX, y, safeZ, $"Bulk removal at Z{FormatValue(level)}");
                        Rapid(moves, startX, y, clearanceZ);
                        Line(moves, startX, y, level, operation.PlungeRate, false);
                    }
                    else
                    {
                        Line(moves, startX, y, level, operation.FeedRate, true, "Bulk removal crossover");
                    }

                    Line(moves, endX, y, level, operation.FeedRate, true);
                    activeX = endX;
                    activeY = y;
                    hasActiveCut = true;
                }

                reverse = !reverse;
            }

            if (hasActiveCut)
            {
                Rapid(moves, activeX, activeY, safeZ);
            }
        }

        return moves;
    }

    private static List<ToolpathMove> PlanRaster(
        JobSetup setup,
        ToolpathOperationDefinition operation,
        ToolDefinition tool,
        Bounds2D bounds,
        RestStockMap restMap)
    {
        var moves = new List<ToolpathMove>();
        var toolRadius = GetToolRadius(tool);
        var safeZ = setup.WorkOffset.Z + Math.Max(setup.SafeZ, operation.SafeRetractZ);
        var clearanceZ = setup.WorkOffset.Z + setup.ClearanceZ;
        var floorZ = bounds.TopZ + operation.Feature.StartZ + Math.Max(0, operation.FinishStockAxial);
        if (floorZ >= bounds.TopZ - 0.0001)
        {
            return moves;
        }

        var stepDown = operation.StepDown > 0
            ? operation.StepDown
            : Math.Max(0.5, tool.MaxStepDown > 0 ? tool.MaxStepDown : Math.Abs(bounds.TopZ - floorZ));
        var stepOver = operation.StepOver > 0
            ? operation.StepOver
            : Math.Max(toolRadius, tool.MaxStepOver > 0 ? tool.MaxStepOver : tool.CuttingDiameter * 0.45);
        var depthLevels = BuildDepthLevels(bounds.TopZ, floorZ, stepDown);
        var keepoutOffset = toolRadius + Math.Max(0, operation.FinishStockRadial);
        var keepouts = BuildBulkKeepoutPolygons(setup, operation.Feature, keepoutOffset);

        foreach (var level in depthLevels)
        {
            var laneYs = operation.UseRestMachining && restMap.HasAppliedCuts
                ? BuildRestRasterLanes(setup, bounds, toolRadius, stepOver, restMap, level)
                : BuildBulkRemovalLanes(setup, bounds, toolRadius, stepOver);
            var reverse = false;
            var activeX = 0d;
            var activeY = 0d;
            var hasActiveCut = false;

            for (var laneIndex = 0; laneIndex < laneYs.Count; laneIndex++)
            {
                var y = laneYs[laneIndex];
                var clearIntervals = BuildClearIntervalsAtY(setup, bounds, toolRadius, keepouts, y);
                var restIntervals = operation.UseRestMachining
                    ? clearIntervals
                        .SelectMany(interval => SplitIntervalByRemainingStock(restMap, interval, y, level, stepOver))
                        .ToList()
                    : clearIntervals;

                if (reverse)
                {
                    restIntervals.Reverse();
                }

                foreach (var interval in restIntervals)
                {
                    var startX = reverse ? interval.EndX : interval.StartX;
                    var endX = reverse ? interval.StartX : interval.EndX;
                    if (Math.Abs(endX - startX) < Math.Max(0.05, toolRadius * 0.18))
                    {
                        continue;
                    }

                    if (!hasActiveCut || !CanCutCrossover(activeX, activeY, startX, y, keepouts, stepOver))
                    {
                        if (hasActiveCut)
                        {
                            Rapid(moves, activeX, activeY, clearanceZ);
                        }

                        Rapid(moves, startX, y, safeZ, $"REST raster at Z{FormatValue(level)}");
                        Rapid(moves, startX, y, clearanceZ);
                        Line(moves, startX, y, level, operation.PlungeRate, false);
                    }
                    else
                    {
                        Line(moves, startX, y, level, operation.FeedRate, true, "REST raster crossover");
                    }

                    Line(moves, endX, y, level, operation.FeedRate, true);
                    activeX = endX;
                    activeY = y;
                    hasActiveCut = true;
                }

                reverse = !reverse;
            }

            if (hasActiveCut)
            {
                Rapid(moves, activeX, activeY, safeZ);
            }
        }

        return moves;
    }

    private static List<ToolpathMove> PlanAdaptiveClearing(
        JobSetup setup,
        ToolpathOperationDefinition operation,
        ToolDefinition tool,
        Bounds2D bounds,
        RestStockMap restMap)
    {
        var moves = new List<ToolpathMove>();
        var toolRadius = GetToolRadius(tool);
        var safeZ = setup.WorkOffset.Z + Math.Max(setup.SafeZ, operation.SafeRetractZ);
        var clearanceZ = setup.WorkOffset.Z + setup.ClearanceZ;
        var floorZ = bounds.TopZ + operation.Feature.StartZ + Math.Max(0, operation.FinishStockAxial);
        if (floorZ >= bounds.TopZ - 0.0001)
        {
            return moves;
        }

        var stepDown = operation.StepDown > 0
            ? operation.StepDown
            : Math.Max(0.5, tool.MaxStepDown > 0 ? tool.MaxStepDown : Math.Abs(bounds.TopZ - floorZ));
        var engagement = operation.StepOver > 0
            ? operation.StepOver
            : Math.Max(0.35, tool.MaxStepOver > 0 ? Math.Min(tool.MaxStepOver, tool.CuttingDiameter * 0.25) : tool.CuttingDiameter * 0.18);
        engagement = Math.Clamp(engagement, Math.Max(0.1, tool.CuttingDiameter * 0.04), Math.Max(0.2, tool.CuttingDiameter * 0.45));
        var depthLevels = BuildDepthLevels(bounds.TopZ, floorZ, stepDown);
        var keepoutOffset = toolRadius + Math.Max(0, operation.FinishStockRadial);
        var keepouts = BuildBulkKeepoutPolygons(setup, operation.Feature, keepoutOffset);

        foreach (var level in depthLevels)
        {
            var laneYs = operation.UseRestMachining && restMap.HasAppliedCuts
                ? BuildRestRasterLanes(setup, bounds, toolRadius, engagement, restMap, level)
                : BuildBulkRemovalLanes(setup, bounds, toolRadius, engagement);
            var reverse = false;
            var activeX = 0d;
            var activeY = 0d;
            var hasActiveCut = false;

            foreach (var y in laneYs)
            {
                var clearIntervals = BuildClearIntervalsAtY(setup, bounds, toolRadius, keepouts, y);
                var restIntervals = operation.UseRestMachining
                    ? clearIntervals
                        .SelectMany(interval => SplitIntervalByRemainingStock(restMap, interval, y, level, engagement))
                        .ToList()
                    : clearIntervals;

                if (reverse)
                {
                    restIntervals.Reverse();
                }

                foreach (var interval in restIntervals)
                {
                    var startX = reverse ? interval.EndX : interval.StartX;
                    var endX = reverse ? interval.StartX : interval.EndX;
                    if (Math.Abs(endX - startX) < Math.Max(0.12, toolRadius * 0.35))
                    {
                        continue;
                    }

                    if (!hasActiveCut || !CanCutCrossover(activeX, activeY, startX, y, keepouts, engagement))
                    {
                        if (hasActiveCut)
                        {
                            Rapid(moves, activeX, activeY, clearanceZ);
                        }

                        Rapid(moves, startX, y, safeZ, $"Adaptive clearing at Z{FormatValue(level)}");
                        Rapid(moves, startX, y, clearanceZ);
                        Line(moves, startX, y, level, operation.PlungeRate, false);
                    }
                    else
                    {
                        Line(moves, startX, y, level, operation.FeedRate, true, "Adaptive linking move");
                    }

                    AddAdaptiveClearingInterval(moves, setup, operation, bounds, keepouts, toolRadius, engagement, startX, endX, y, level);
                    activeX = endX;
                    activeY = y;
                    hasActiveCut = true;
                }

                reverse = !reverse;
            }

            if (hasActiveCut)
            {
                Rapid(moves, activeX, activeY, safeZ);
            }
        }

        return moves;
    }

    private static List<ToolpathMove> PlanPocket(JobSetup setup, ToolpathOperationDefinition operation, ToolDefinition tool)
    {
        var toolRadius = GetToolRadius(tool);
        var safeZ = setup.WorkOffset.Z + Math.Max(setup.SafeZ, operation.SafeRetractZ);
        var clearanceZ = setup.WorkOffset.Z + setup.ClearanceZ;
        var featureTopZ = setup.WorkOffset.Z + operation.Feature.StartZ;
        var finalZ = featureTopZ - Math.Max(0, operation.Feature.Depth - operation.FinishStockAxial);
        var depthLevels = BuildDepthLevels(featureTopZ, finalZ, operation.StepDown > 0 ? operation.StepDown : operation.Feature.Depth);
        var moves = new List<ToolpathMove>();

        foreach (var level in depthLevels)
        {
            switch (operation.Feature.Shape)
            {
                case FeatureShape.Circle:
                    AddCircularPocketPass(moves, setup, operation, safeZ, clearanceZ, level, toolRadius);
                    break;
                default:
                    AddRectangularPocketPass(moves, setup, operation, safeZ, clearanceZ, level, toolRadius);
                    break;
            }
        }

        return moves;
    }

    private static List<ToolpathMove> PlanZLevelFinishing(
        JobSetup setup,
        ToolpathOperationDefinition operation,
        ToolDefinition tool,
        LoadedPreviewGeometry? partGeometry,
        double previewStockTopZ)
    {
        var moves = new List<ToolpathMove>();
        if (partGeometry is null || partGeometry.SurfaceTriangles.Count == 0 || partGeometry.Bounds.IsEmpty)
        {
            return moves;
        }

        var toolRadius = GetToolRadius(tool);
        var safeZ = setup.WorkOffset.Z + Math.Max(setup.SafeZ, operation.SafeRetractZ);
        var clearanceZ = setup.WorkOffset.Z + setup.ClearanceZ;
        var modelTopZ = PreviewZToToolZ(partGeometry.Bounds.Z + partGeometry.Bounds.SizeZ, previewStockTopZ, setup.WorkOffset.Z);
        var modelBottomZ = PreviewZToToolZ(partGeometry.Bounds.Z, previewStockTopZ, setup.WorkOffset.Z);
        var topZ = operation.Feature.Shape == FeatureShape.ModelPlane
            ? modelTopZ
            : setup.WorkOffset.Z + operation.Feature.StartZ;
        var finalZ = operation.Feature.Shape == FeatureShape.ModelPlane
            ? setup.WorkOffset.Z + operation.Feature.StartZ
            : operation.Feature.Depth > 0 ? topZ - operation.Feature.Depth : modelBottomZ;

        topZ = Math.Clamp(topZ, modelBottomZ, modelTopZ);
        finalZ = Math.Clamp(finalZ, modelBottomZ, topZ);
        if (finalZ >= topZ - 0.0001)
        {
            return moves;
        }

        var zStep = operation.StepDown > 0
            ? operation.StepDown
            : Math.Max(0.35, tool.MaxStepDown > 0 ? Math.Min(tool.MaxStepDown, (topZ - finalZ) / 6d) : (topZ - finalZ) / 10d);
        var levels = BuildDepthLevels(topZ, finalZ, zStep);
        var sliceTolerance = GetWaterlineTolerance(partGeometry.Bounds);
        var offset = toolRadius + Math.Max(0, operation.FinishStockRadial);
        var sideContactDrop = CutterGeometry.BottomProfileHeightAtRadius(tool, toolRadius);

        foreach (var level in levels)
        {
            var previewSliceZ = ToolZToPreviewZ(level, previewStockTopZ, setup.WorkOffset.Z);
            var loops = BuildWaterlineLoops(partGeometry.SurfaceTriangles, previewSliceZ, sliceTolerance);
            foreach (var loop in loops)
            {
                if (loop.Count < 2 || GetPathLength(loop) < Math.Max(toolRadius, sliceTolerance * 4d))
                {
                    continue;
                }

                var path = loop
                    .Select(point => new Point3(point.X, point.Y, level - sideContactDrop + Math.Max(0, operation.FinishStockAxial)))
                    .ToList();
                path = OffsetProfilePath(path, offset, insideProfile: false);
                path = OrientContourPath(path, insideProfile: false, operation.ClimbMilling);
                AddZLevelLoop(moves, path, operation, safeZ, clearanceZ, level);
            }
        }

        return moves;
    }

    private static List<ToolpathMove> PlanParallel3DFinishing(
        JobSetup setup,
        ToolpathOperationDefinition operation,
        ToolDefinition tool,
        LoadedPreviewGeometry? partGeometry,
        double previewStockTopZ)
    {
        var moves = new List<ToolpathMove>();
        if (partGeometry is null || partGeometry.SurfaceTriangles.Count == 0 || partGeometry.Bounds.IsEmpty)
        {
            return moves;
        }

        var safeZ = setup.WorkOffset.Z + Math.Max(setup.SafeZ, operation.SafeRetractZ);
        var clearanceZ = setup.WorkOffset.Z + setup.ClearanceZ;
        var modelTopZ = PreviewZToToolZ(partGeometry.Bounds.Z + partGeometry.Bounds.SizeZ, previewStockTopZ, setup.WorkOffset.Z);
        var modelBottomZ = PreviewZToToolZ(partGeometry.Bounds.Z, previewStockTopZ, setup.WorkOffset.Z);
        var lowerLimitZ = operation.Feature.Shape == FeatureShape.ModelPlane
            ? setup.WorkOffset.Z + operation.Feature.StartZ
            : operation.Feature.Depth > 0 ? modelTopZ - operation.Feature.Depth : modelBottomZ;
        lowerLimitZ = Math.Clamp(lowerLimitZ, modelBottomZ, modelTopZ);

        var stepOver = operation.StepOver > 0
            ? operation.StepOver
            : Math.Max(0.35, tool.MaxStepOver > 0 ? Math.Min(tool.MaxStepOver, tool.CuttingDiameter * 0.22) : tool.CuttingDiameter * 0.18);
        var sampleStep = operation.StepDown > 0
            ? operation.StepDown
            : Math.Max(0.35, Math.Min(stepOver, tool.CuttingDiameter * 0.35));
        stepOver = Math.Max(0.1, stepOver);
        sampleStep = Math.Max(0.1, sampleStep);

        var angle = DegreesToRadians(operation.Feature.RotationDegrees);
        var ux = Math.Cos(angle);
        var uy = Math.Sin(angle);
        var vx = -uy;
        var vy = ux;
        var (minU, maxU, minV, maxV) = GetRotatedBounds(partGeometry.Bounds, ux, uy, vx, vy);
        minU -= stepOver;
        maxU += stepOver;
        minV -= stepOver;
        maxV += stepOver;

        var lanes = BuildLinearPasses(minV, maxV, stepOver);
        var reverse = false;
        foreach (var laneV in lanes)
        {
            var lanePoints = BuildParallelSurfaceLane(
                partGeometry.SurfaceTriangles,
                tool,
                previewStockTopZ,
                setup.WorkOffset.Z,
                minU,
                maxU,
                laneV,
                ux,
                uy,
                vx,
                vy,
                sampleStep,
                lowerLimitZ,
                operation.FinishStockAxial,
                reverse);

            foreach (var segment in SplitParallelLane(lanePoints, sampleStep, stepOver))
            {
                AddParallelSurfaceSegment(moves, segment, operation, safeZ, clearanceZ);
            }

            reverse = !reverse;
        }

        return moves;
    }

    private static List<ToolpathMove> PlanScallopFinishing(
        JobSetup setup,
        ToolpathOperationDefinition operation,
        ToolDefinition tool,
        LoadedPreviewGeometry? partGeometry,
        double previewStockTopZ)
    {
        var moves = new List<ToolpathMove>();
        if (partGeometry is null || partGeometry.SurfaceTriangles.Count == 0 || partGeometry.Bounds.IsEmpty)
        {
            return moves;
        }

        var safeZ = setup.WorkOffset.Z + Math.Max(setup.SafeZ, operation.SafeRetractZ);
        var clearanceZ = setup.WorkOffset.Z + setup.ClearanceZ;
        var modelTopZ = PreviewZToToolZ(partGeometry.Bounds.Z + partGeometry.Bounds.SizeZ, previewStockTopZ, setup.WorkOffset.Z);
        var modelBottomZ = PreviewZToToolZ(partGeometry.Bounds.Z, previewStockTopZ, setup.WorkOffset.Z);
        var lowerLimitZ = operation.Feature.Shape == FeatureShape.ModelPlane
            ? setup.WorkOffset.Z + operation.Feature.StartZ
            : operation.Feature.Depth > 0 ? modelTopZ - operation.Feature.Depth : modelBottomZ;
        lowerLimitZ = Math.Clamp(lowerLimitZ, modelBottomZ, modelTopZ);

        var stepOver = operation.StepOver > 0
            ? operation.StepOver
            : Math.Max(0.25, tool.MaxStepOver > 0 ? Math.Min(tool.MaxStepOver, tool.CuttingDiameter * 0.12) : tool.CuttingDiameter * 0.1);
        var sampleSpacing = operation.StepDown > 0
            ? operation.StepDown
            : Math.Max(0.3, Math.Min(stepOver, tool.CuttingDiameter * 0.25));
        stepOver = Math.Max(0.08, stepOver);
        sampleSpacing = Math.Max(0.08, sampleSpacing);

        var useSelectedEnvelope = operation.Feature.Shape == FeatureShape.ModelPlane
            && operation.Feature.Length > 0
            && operation.Feature.Width > 0;
        var centerX = useSelectedEnvelope
            ? setup.WorkOffset.X + setup.AlignmentOffsetX + operation.Feature.CenterX
            : partGeometry.Bounds.X + (partGeometry.Bounds.SizeX / 2d);
        var centerY = useSelectedEnvelope
            ? setup.WorkOffset.Y + setup.AlignmentOffsetY + operation.Feature.CenterY
            : partGeometry.Bounds.Y + (partGeometry.Bounds.SizeY / 2d);
        var radiusX = useSelectedEnvelope
            ? Math.Max(operation.Feature.Length / 2d, stepOver)
            : Math.Max(partGeometry.Bounds.SizeX / 2d, stepOver);
        var radiusY = useSelectedEnvelope
            ? Math.Max(operation.Feature.Width / 2d, stepOver)
            : Math.Max(partGeometry.Bounds.SizeY / 2d, stepOver);
        var maxRadius = Math.Max(radiusX, radiusY);
        var ringCount = Math.Clamp((int)Math.Ceiling(maxRadius / stepOver), 1, 260);
        var startAngle = DegreesToRadians(operation.Feature.RotationDegrees);

        for (var ring = ringCount; ring >= 1; ring--)
        {
            var scale = ring / (double)ringCount;
            var path = BuildScallopRing(
                partGeometry.SurfaceTriangles,
                tool,
                previewStockTopZ,
                setup.WorkOffset.Z,
                centerX,
                centerY,
                radiusX * scale,
                radiusY * scale,
                startAngle,
                sampleSpacing,
                lowerLimitZ,
                operation.FinishStockAxial);

            foreach (var segment in SplitParallelLane(path, sampleSpacing, stepOver))
            {
                AddScallopSurfaceSegment(moves, segment, operation, safeZ, clearanceZ);
            }
        }

        return moves;
    }

    private static List<ToolpathMove> PlanPencilCleanup(
        JobSetup setup,
        ToolpathOperationDefinition operation,
        ToolDefinition tool,
        LoadedPreviewGeometry? partGeometry,
        double previewStockTopZ)
    {
        var moves = new List<ToolpathMove>();
        if (partGeometry is null || partGeometry.Bounds.IsEmpty)
        {
            return moves;
        }

        var safeZ = setup.WorkOffset.Z + Math.Max(setup.SafeZ, operation.SafeRetractZ);
        var clearanceZ = setup.WorkOffset.Z + setup.ClearanceZ;
        var modelTopZ = PreviewZToToolZ(partGeometry.Bounds.Z + partGeometry.Bounds.SizeZ, previewStockTopZ, setup.WorkOffset.Z);
        var modelBottomZ = PreviewZToToolZ(partGeometry.Bounds.Z, previewStockTopZ, setup.WorkOffset.Z);
        var lowerLimitZ = operation.Feature.Shape == FeatureShape.ModelPlane
            ? setup.WorkOffset.Z + operation.Feature.StartZ
            : modelBottomZ;
        lowerLimitZ = Math.Clamp(lowerLimitZ, modelBottomZ, modelTopZ);

        var candidatePaths = operation.Feature.Shape == FeatureShape.EdgePath && operation.Feature.PathPoints.Count >= 2
            ? new List<List<Point3>> { BuildSelectedPencilPath(setup, operation, previewStockTopZ) }
            : BuildAutomaticPencilPaths(partGeometry.EdgeGeometries, previewStockTopZ, setup.WorkOffset.Z, lowerLimitZ, modelTopZ, tool, operation.FinishStockAxial);

        foreach (var candidatePath in candidatePaths)
        {
            var cleanPath = RemoveNearDuplicatePoints(candidatePath, Math.Max(GetWaterlineTolerance(partGeometry.Bounds), 0.01));
            if (cleanPath.Count < 2 || GetPathLength(cleanPath) < Math.Max(0.35, GetToolRadius(tool) * 0.4))
            {
                continue;
            }

            var toolCenterPath = BuildPencilToolCenterPath(
                cleanPath,
                operation,
                tool,
                partGeometry,
                previewStockTopZ,
                setup.WorkOffset.Z);
            if (toolCenterPath.Count < 2 || GetPathLength(toolCenterPath) < Math.Max(0.35, GetToolRadius(tool) * 0.4))
            {
                continue;
            }

            foreach (var passPath in BuildPencilPasses(toolCenterPath, operation))
            {
                foreach (var safePassPath in BuildModelSafePencilPasses(
                    passPath,
                    operation,
                    tool,
                    partGeometry.SurfaceTriangles,
                    previewStockTopZ,
                    setup.WorkOffset.Z))
                {
                    AddPencilCleanupPath(moves, safePassPath, operation, safeZ, clearanceZ);
                }
            }
        }

        return moves;
    }

    private static List<ToolpathMove> PlanProfile(JobSetup setup, ToolpathOperationDefinition operation, ToolDefinition tool)
    {
        var toolRadius = GetToolRadius(tool);
        var safeZ = setup.WorkOffset.Z + Math.Max(setup.SafeZ, operation.SafeRetractZ);
        var clearanceZ = setup.WorkOffset.Z + setup.ClearanceZ;
        var featureTopZ = setup.WorkOffset.Z + operation.Feature.StartZ;
        var finalZ = featureTopZ - Math.Max(0, operation.Feature.Depth - operation.FinishStockAxial);
        var depthLevels = BuildDepthLevels(featureTopZ, finalZ, operation.StepDown > 0 ? operation.StepDown : operation.Feature.Depth);
        var moves = new List<ToolpathMove>();

        foreach (var level in depthLevels)
        {
            var passCount = Math.Max(1, operation.PathCount);
            for (var pass = 0; pass < passCount; pass++)
            {
                switch (operation.Feature.Shape)
                {
                    case FeatureShape.EdgePath:
                        AddEdgeProfilePass(moves, setup, operation, tool, safeZ, clearanceZ, level);
                        break;
                    case FeatureShape.Circle:
                        AddCircularProfilePass(moves, setup, operation, safeZ, clearanceZ, level, toolRadius);
                        break;
                    default:
                        AddRectangularProfilePass(moves, setup, operation, safeZ, clearanceZ, level, toolRadius);
                        break;
                }
            }
        }

        return moves;
    }

    private static List<ToolpathMove> PlanContour2D(JobSetup setup, ToolpathOperationDefinition operation, ToolDefinition tool)
    {
        var toolRadius = GetToolRadius(tool);
        var safeZ = setup.WorkOffset.Z + Math.Max(setup.SafeZ, operation.SafeRetractZ);
        var clearanceZ = setup.WorkOffset.Z + setup.ClearanceZ;
        var featureTopZ = setup.WorkOffset.Z + operation.Feature.StartZ;
        var finalZ = featureTopZ - Math.Max(0, operation.Feature.Depth - operation.FinishStockAxial);
        var depthLevels = BuildDepthLevels(featureTopZ, finalZ, operation.StepDown > 0 ? operation.StepDown : operation.Feature.Depth);
        var moves = new List<ToolpathMove>();

        foreach (var level in depthLevels)
        {
            var passCount = Math.Max(1, operation.PathCount);
            for (var pass = 0; pass < passCount; pass++)
            {
                AddContour2DPass(moves, setup, operation, safeZ, clearanceZ, level, finalZ, toolRadius);
            }
        }

        return moves;
    }

    private static List<ToolpathMove> PlanChamfer(JobSetup setup, ToolpathOperationDefinition operation, ToolDefinition tool)
    {
        var moves = new List<ToolpathMove>();
        if (operation.Feature.Shape != FeatureShape.EdgePath || operation.Feature.PathPoints.Count < 2)
        {
            return moves;
        }

        var safeZ = setup.WorkOffset.Z + Math.Max(setup.SafeZ, operation.SafeRetractZ);
        var clearanceZ = setup.WorkOffset.Z + setup.ClearanceZ;
        var edgeTopZ = setup.WorkOffset.Z + operation.Feature.StartZ;
        var requestedWidth = operation.Feature.Depth > 0
            ? operation.Feature.Depth
            : Math.Max(0.25, tool.CuttingDiameter * 0.125);
        var chamferWidth = Math.Min(requestedWidth, GetMaxChamferWidth(tool));
        if (chamferWidth <= 0.0001)
        {
            return moves;
        }

        var axialPerRadial = GetChamferAxialPerRadial(tool);
        var finalAxialDepth = chamferWidth * axialPerRadial;
        var passCount = Math.Max(1, operation.PathCount);
        if (operation.StepDown > 0.0001)
        {
            passCount = Math.Max(passCount, (int)Math.Ceiling(finalAxialDepth / operation.StepDown));
        }

        for (var pass = 1; pass <= passCount; pass++)
        {
            var passWidth = chamferWidth * pass / passCount;
            var passAxialDepth = passWidth * axialPerRadial;
            var level = edgeTopZ - Math.Max(0, passAxialDepth - operation.FinishStockAxial);
            var centerlineOffset = GetChamferTipRadius(tool) + passWidth + operation.FinishStockRadial;
            AddChamferEdgePass(moves, setup, operation, safeZ, clearanceZ, level, centerlineOffset, passWidth);
        }

        return moves;
    }

    private static List<ToolpathMove> PlanBoring(JobSetup setup, ToolpathOperationDefinition operation, ToolDefinition tool)
    {
        var moves = new List<ToolpathMove>();
        if (operation.Feature.Shape is not (FeatureShape.Circle or FeatureShape.HolePattern))
        {
            return moves;
        }

        var toolRadius = GetToolRadius(tool);
        var boreRadius = (operation.Feature.Diameter / 2d) - toolRadius - Math.Max(0, operation.FinishStockRadial);
        if (boreRadius <= Math.Max(0.05, toolRadius * 0.05))
        {
            return moves;
        }

        var safeZ = setup.WorkOffset.Z + Math.Max(setup.SafeZ, operation.SafeRetractZ);
        var clearanceZ = setup.WorkOffset.Z + setup.ClearanceZ;
        var topZ = setup.WorkOffset.Z + operation.Feature.StartZ;
        var finalZ = topZ - Math.Max(0, operation.Feature.Depth - operation.FinishStockAxial);
        var stepDown = operation.StepDown > 0 ? operation.StepDown : Math.Max(0.1, operation.Feature.Depth);
        var depthLevels = BuildDepthLevels(topZ, finalZ, stepDown);

        foreach (var point in GetFeaturePoints(setup, operation.Feature))
        {
            foreach (var level in depthLevels)
            {
                AddBoringPass(moves, point.X, point.Y, boreRadius, operation, safeZ, clearanceZ, level);
            }
        }

        return moves;
    }

    private static List<ToolpathMove> PlanDrill(JobSetup setup, ToolpathOperationDefinition operation, ToolDefinition tool)
    {
        var safeZ = setup.WorkOffset.Z + Math.Max(setup.SafeZ, operation.SafeRetractZ);
        var clearanceZ = setup.WorkOffset.Z + setup.ClearanceZ;
        var topZ = setup.WorkOffset.Z + operation.Feature.StartZ;
        var fullDiameterTipAllowance = GetConicalTipLength(tool);
        var finalZ = topZ - operation.Feature.Depth - fullDiameterTipAllowance;
        var peckDepth = operation.DrillPeckDepth > 0
            ? operation.DrillPeckDepth
            : operation.StepDown > 0 ? operation.StepDown : operation.Feature.Depth;
        var peckLevels = BuildDepthLevels(topZ, finalZ, peckDepth);
        var retractDistance = Math.Max(operation.DrillRetractDistance, 0);
        var moves = new List<ToolpathMove>();

        foreach (var point in GetFeaturePoints(setup, operation.Feature))
        {
            Rapid(moves, point.X, point.Y, safeZ, $"Drill {operation.Feature.Name}");
            Rapid(moves, point.X, point.Y, clearanceZ);

            foreach (var peckLevel in peckLevels)
            {
                Line(moves, point.X, point.Y, peckLevel, operation.PlungeRate > 0 ? operation.PlungeRate : operation.FeedRate, true);
                if (peckLevel > finalZ + 0.0001)
                {
                    var retractZ = operation.DrillFullRetract
                        ? clearanceZ
                        : Math.Min(clearanceZ, peckLevel + retractDistance);
                    if (retractZ > peckLevel + 0.0001)
                    {
                        Rapid(moves, point.X, point.Y, retractZ, operation.DrillFullRetract ? "Full peck retract" : "Peck chip break retract");
                    }
                }
            }

            Rapid(moves, point.X, point.Y, safeZ);
        }

        return moves;
    }

    private static void AddBoringPass(
        List<ToolpathMove> moves,
        double centerX,
        double centerY,
        double radius,
        ToolpathOperationDefinition operation,
        double safeZ,
        double clearanceZ,
        double level)
    {
        var startX = centerX + radius;
        var startY = centerY;
        var springPasses = Math.Max(1, operation.PathCount);

        Rapid(moves, centerX, centerY, safeZ, $"Bore {operation.Feature.Name} at Z{FormatValue(level)}");
        Rapid(moves, centerX, centerY, clearanceZ);
        Line(moves, centerX, centerY, level, operation.PlungeRate > 0 ? operation.PlungeRate : operation.FeedRate, true);
        Line(moves, startX, startY, level, operation.FeedRate, true);

        for (var pass = 0; pass < springPasses; pass++)
        {
            AddCircularPath(moves, centerX, centerY, radius, level, operation.FeedRate, operation.ClimbMilling);
        }

        Rapid(moves, startX, startY, safeZ);
    }

    private static void AddRectangularPocketPass(
        List<ToolpathMove> moves,
        JobSetup setup,
        ToolpathOperationDefinition operation,
        double safeZ,
        double clearanceZ,
        double level,
        double toolRadius)
    {
        var centerX = setup.WorkOffset.X + setup.AlignmentOffsetX + operation.Feature.CenterX;
        var centerY = setup.WorkOffset.Y + setup.AlignmentOffsetY + operation.Feature.CenterY;
        var minX = centerX - (operation.Feature.Length / 2d) + toolRadius + operation.FinishStockRadial;
        var maxX = centerX + (operation.Feature.Length / 2d) - toolRadius - operation.FinishStockRadial;
        var minY = centerY - (operation.Feature.Width / 2d) + toolRadius + operation.FinishStockRadial;
        var maxY = centerY + (operation.Feature.Width / 2d) - toolRadius - operation.FinishStockRadial;

        if (minX > maxX)
        {
            minX = maxX = centerX;
        }

        if (minY > maxY)
        {
            minY = maxY = centerY;
        }

        var laneYs = BuildLinearPasses(minY, maxY, operation.StepOver > 0 ? operation.StepOver : toolRadius);
        if (laneYs.Count == 0)
        {
            return;
        }

        var reverse = false;
        Rapid(moves, minX, laneYs[0], safeZ, $"Pocket at Z{FormatValue(level)}");
        Rapid(moves, minX, laneYs[0], clearanceZ);
        Line(moves, minX, laneYs[0], level, operation.PlungeRate, false);

        for (var index = 0; index < laneYs.Count; index++)
        {
            var currentY = laneYs[index];
            var startX = reverse ? maxX : minX;
            var endX = reverse ? minX : maxX;

            if (index > 0)
            {
                Line(moves, startX, currentY, level, operation.FeedRate, true);
            }

            Line(moves, endX, currentY, level, operation.FeedRate, true);

            if (index < laneYs.Count - 1)
            {
                var nextY = laneYs[index + 1];
                Line(moves, endX, nextY, level, operation.FeedRate, true);
            }

            reverse = !reverse;
        }

        Rapid(moves, reverse ? minX : maxX, laneYs[^1], safeZ);
    }

    private static void AddCircularPocketPass(
        List<ToolpathMove> moves,
        JobSetup setup,
        ToolpathOperationDefinition operation,
        double safeZ,
        double clearanceZ,
        double level,
        double toolRadius)
    {
        var centerX = setup.WorkOffset.X + setup.AlignmentOffsetX + operation.Feature.CenterX;
        var centerY = setup.WorkOffset.Y + setup.AlignmentOffsetY + operation.Feature.CenterY;
        var radius = Math.Max(0, (operation.Feature.Diameter / 2d) - toolRadius - operation.FinishStockRadial);
        var radialStep = operation.StepOver > 0 ? operation.StepOver : Math.Max(toolRadius, 1);

        for (var currentRadius = radius; currentRadius >= 0.5; currentRadius -= radialStep)
        {
            var startX = centerX + currentRadius;
            var startY = centerY;
            Rapid(moves, startX, startY, safeZ, $"Circular pocket at Z{FormatValue(level)}");
            Rapid(moves, startX, startY, clearanceZ);
            Line(moves, startX, startY, level, operation.PlungeRate, false);
            AddCircularPath(moves, centerX, centerY, currentRadius, level, operation.FeedRate, operation.ClimbMilling);
            Rapid(moves, startX, startY, safeZ);
        }

        if (radius < 0.5)
        {
            Rapid(moves, centerX, centerY, safeZ);
            Rapid(moves, centerX, centerY, clearanceZ);
            Line(moves, centerX, centerY, level, operation.PlungeRate, true);
            Rapid(moves, centerX, centerY, safeZ);
        }
    }

    private static void AddRectangularProfilePass(
        List<ToolpathMove> moves,
        JobSetup setup,
        ToolpathOperationDefinition operation,
        double safeZ,
        double clearanceZ,
        double level,
        double toolRadius)
    {
        var centerX = setup.WorkOffset.X + setup.AlignmentOffsetX + operation.Feature.CenterX;
        var centerY = setup.WorkOffset.Y + setup.AlignmentOffsetY + operation.Feature.CenterY;
        var offset = toolRadius + operation.FinishStockRadial;
        var inside = operation.Feature.InsideProfile;
        var minX = centerX - (operation.Feature.Length / 2d) + (inside ? offset : -offset);
        var maxX = centerX + (operation.Feature.Length / 2d) - (inside ? offset : -offset);
        var minY = centerY - (operation.Feature.Width / 2d) + (inside ? offset : -offset);
        var maxY = centerY + (operation.Feature.Width / 2d) - (inside ? offset : -offset);

        var points = new[]
        {
            new Point3(minX, minY, level),
            new Point3(maxX, minY, level),
            new Point3(maxX, maxY, level),
            new Point3(minX, maxY, level),
            new Point3(minX, minY, level)
        };

        Rapid(moves, points[0].X, points[0].Y, safeZ, $"Profile at Z{FormatValue(level)}");
        Rapid(moves, points[0].X, points[0].Y, clearanceZ);
        Line(moves, points[0].X, points[0].Y, points[0].Z, operation.PlungeRate, false);

        for (var index = 1; index < points.Length; index++)
        {
            Line(moves, points[index].X, points[index].Y, points[index].Z, operation.FeedRate, true);
        }

        Rapid(moves, points[0].X, points[0].Y, safeZ);
    }

    private static void AddEdgeProfilePass(
        List<ToolpathMove> moves,
        JobSetup setup,
        ToolpathOperationDefinition operation,
        ToolDefinition tool,
        double safeZ,
        double clearanceZ,
        double level)
    {
        var points = operation.Feature.PathPoints
            .Select(point => new Point3(
                setup.WorkOffset.X + setup.AlignmentOffsetX + point.X,
                setup.WorkOffset.Y + setup.AlignmentOffsetY + point.Y,
                level))
            .Where(point => !double.IsNaN(point.X) && !double.IsNaN(point.Y))
            .ToList();

        points = OffsetProfilePath(points, GetToolRadius(tool) + operation.FinishStockRadial, operation.Feature.InsideProfile);

        if (points.Count < 2)
        {
            return;
        }

        Rapid(moves, points[0].X, points[0].Y, safeZ, $"Profile model edge at Z{FormatValue(level)}");
        Rapid(moves, points[0].X, points[0].Y, clearanceZ);
        Line(moves, points[0].X, points[0].Y, points[0].Z, operation.PlungeRate, false);

        for (var index = 1; index < points.Count; index++)
        {
            Line(moves, points[index].X, points[index].Y, points[index].Z, operation.FeedRate, true);
        }

        Rapid(moves, points[^1].X, points[^1].Y, safeZ);
    }

    private static void AddContour2DPass(
        List<ToolpathMove> moves,
        JobSetup setup,
        ToolpathOperationDefinition operation,
        double safeZ,
        double clearanceZ,
        double level,
        double finalZ,
        double toolRadius)
    {
        var offset = toolRadius + operation.FinishStockRadial;
        var points = BuildContour2DPath(setup, operation, level, offset);
        points = OrientContourPath(points, operation.Feature.InsideProfile, operation.ClimbMilling);
        if (points.Count < 2)
        {
            return;
        }

        var start = points[0];
        var leadStart = GetLeadPoint(points, Math.Max(0, operation.LeadInLength), leadIn: true) ?? start;
        var leadEnd = GetLeadPoint(points, Math.Max(0, operation.LeadOutLength), leadIn: false);
        var tabZ = Math.Min(setup.WorkOffset.Z + operation.Feature.StartZ, finalZ + Math.Max(0, operation.TabHeight));

        Rapid(moves, leadStart.X, leadStart.Y, safeZ, $"2D contour at Z{FormatValue(level)}");
        Rapid(moves, leadStart.X, leadStart.Y, clearanceZ);
        Line(moves, leadStart.X, leadStart.Y, leadStart.Z, operation.PlungeRate, false);
        if (!PointsEqual2D(leadStart, start))
        {
            Line(moves, start.X, start.Y, start.Z, operation.FeedRate, true, "Contour lead-in");
        }

        AddContourPathMoves(moves, points, operation, level, tabZ);

        if (leadEnd is not null)
        {
            Line(moves, leadEnd.Value.X, leadEnd.Value.Y, leadEnd.Value.Z, operation.FeedRate, true, "Contour lead-out");
            Rapid(moves, leadEnd.Value.X, leadEnd.Value.Y, safeZ);
        }
        else
        {
            Rapid(moves, points[^1].X, points[^1].Y, safeZ);
        }
    }

    private static List<Point3> BuildContour2DPath(JobSetup setup, ToolpathOperationDefinition operation, double level, double offset)
    {
        switch (operation.Feature.Shape)
        {
            case FeatureShape.EdgePath:
                var edgePoints = operation.Feature.PathPoints
                    .Select(point => new Point3(
                        setup.WorkOffset.X + setup.AlignmentOffsetX + point.X,
                        setup.WorkOffset.Y + setup.AlignmentOffsetY + point.Y,
                        level))
                    .Where(point => !double.IsNaN(point.X) && !double.IsNaN(point.Y))
                    .ToList();
                return OffsetProfilePath(edgePoints, offset, operation.Feature.InsideProfile);
            case FeatureShape.Circle:
                return BuildCircularContourPath(setup, operation, level, offset);
            default:
                return BuildRectangularContourPath(setup, operation, level, offset);
        }
    }

    private static List<Point3> BuildRectangularContourPath(JobSetup setup, ToolpathOperationDefinition operation, double level, double offset)
    {
        var centerX = setup.WorkOffset.X + setup.AlignmentOffsetX + operation.Feature.CenterX;
        var centerY = setup.WorkOffset.Y + setup.AlignmentOffsetY + operation.Feature.CenterY;
        var inside = operation.Feature.InsideProfile;
        var minX = centerX - (operation.Feature.Length / 2d) + (inside ? offset : -offset);
        var maxX = centerX + (operation.Feature.Length / 2d) - (inside ? offset : -offset);
        var minY = centerY - (operation.Feature.Width / 2d) + (inside ? offset : -offset);
        var maxY = centerY + (operation.Feature.Width / 2d) - (inside ? offset : -offset);

        return new List<Point3>
        {
            new(minX, minY, level),
            new(maxX, minY, level),
            new(maxX, maxY, level),
            new(minX, maxY, level),
            new(minX, minY, level)
        };
    }

    private static List<Point3> BuildCircularContourPath(JobSetup setup, ToolpathOperationDefinition operation, double level, double offset)
    {
        var centerX = setup.WorkOffset.X + setup.AlignmentOffsetX + operation.Feature.CenterX;
        var centerY = setup.WorkOffset.Y + setup.AlignmentOffsetY + operation.Feature.CenterY;
        var baseRadius = Math.Max(0.5, operation.Feature.Diameter / 2d);
        var radius = operation.Feature.InsideProfile ? Math.Max(0.5, baseRadius - offset) : baseRadius + offset;
        var segments = Math.Clamp((int)Math.Ceiling((Math.PI * 2d * radius) / 1.5), 48, 160);
        var points = new List<Point3>(segments + 1);

        for (var index = 0; index <= segments; index++)
        {
            var angle = Math.PI * 2d * index / segments;
            points.Add(new Point3(
                centerX + (Math.Cos(angle) * radius),
                centerY + (Math.Sin(angle) * radius),
                level));
        }

        return points;
    }

    private static List<Point3> OrientContourPath(IReadOnlyList<Point3> sourcePoints, bool insideProfile, bool climbMilling)
    {
        var points = sourcePoints.ToList();
        if (!IsClosedPath(points) || points.Count < 4)
        {
            return points;
        }

        var body = points.Take(points.Count - 1).ToList();
        var currentCcw = CalculateSignedArea(body) > 0;
        var desiredCcw = insideProfile ? climbMilling : !climbMilling;
        if (currentCcw == desiredCcw)
        {
            return points;
        }

        body.Reverse();
        body.Add(body[0]);
        return body;
    }

    private static Point3? GetLeadPoint(IReadOnlyList<Point3> points, double length, bool leadIn)
    {
        if (length <= 0.0001 || points.Count < 2)
        {
            return null;
        }

        var anchor = leadIn ? points[0] : points[^1];
        var neighbor = leadIn ? points[1] : points[^2];
        var tangent = leadIn
            ? Normalize2D(neighbor.X - anchor.X, neighbor.Y - anchor.Y)
            : Normalize2D(anchor.X - neighbor.X, anchor.Y - neighbor.Y);
        if (Math.Abs(tangent.X) < 0.000001 && Math.Abs(tangent.Y) < 0.000001)
        {
            return null;
        }

        var direction = leadIn ? -1d : 1d;
        return new Point3(
            anchor.X + (tangent.X * length * direction),
            anchor.Y + (tangent.Y * length * direction),
            anchor.Z);
    }

    private static void AddContourPathMoves(
        List<ToolpathMove> moves,
        IReadOnlyList<Point3> points,
        ToolpathOperationDefinition operation,
        double level,
        double tabZ)
    {
        var tabWidth = Math.Max(0, operation.TabWidth);
        var useTabs = IsClosedPath(points)
            && tabWidth > 0.0001
            && operation.TabHeight > 0.0001
            && tabZ > level + 0.0001;
        var tabIntervals = useTabs ? BuildTabIntervals(GetPathLength(points), tabWidth) : new List<CutInterval>();
        var traveled = 0d;

        for (var index = 1; index < points.Count; index++)
        {
            var start = points[index - 1];
            var end = points[index];
            var segmentLength = Distance2D(start, end);
            if (segmentLength <= 0.000001)
            {
                continue;
            }

            if (tabIntervals.Count == 0)
            {
                Line(moves, end.X, end.Y, end.Z, operation.FeedRate, true);
            }
            else
            {
                AddContourSegmentWithTabs(moves, start, end, traveled, tabIntervals, level, tabZ, operation.FeedRate);
            }

            traveled += segmentLength;
        }
    }

    private static List<CutInterval> BuildTabIntervals(double pathLength, double tabWidth)
    {
        var intervals = new List<CutInterval>();
        if (pathLength <= tabWidth + 0.0001)
        {
            return intervals;
        }

        const int tabCount = 4;
        var halfWidth = Math.Min(tabWidth / 2d, pathLength / (tabCount * 3d));
        for (var index = 0; index < tabCount; index++)
        {
            var center = (index + 0.5) * pathLength / tabCount;
            var start = center - halfWidth;
            var end = center + halfWidth;
            intervals.Add(new CutInterval(Math.Max(0, start), Math.Min(pathLength, end)));
        }

        return intervals;
    }

    private static void AddContourSegmentWithTabs(
        List<ToolpathMove> moves,
        Point3 start,
        Point3 end,
        double traveled,
        IReadOnlyList<CutInterval> tabIntervals,
        double level,
        double tabZ,
        double feedRate)
    {
        var segmentLength = Distance2D(start, end);
        var splitDistances = new List<double> { 0, segmentLength };
        foreach (var tab in tabIntervals)
        {
            var localStart = tab.StartX - traveled;
            var localEnd = tab.EndX - traveled;
            if (localEnd <= 0 || localStart >= segmentLength)
            {
                continue;
            }

            splitDistances.Add(Math.Clamp(localStart, 0, segmentLength));
            splitDistances.Add(Math.Clamp(localEnd, 0, segmentLength));
        }

        splitDistances = splitDistances
            .Where(distance => distance >= -0.000001 && distance <= segmentLength + 0.000001)
            .Select(distance => Math.Clamp(distance, 0, segmentLength))
            .DistinctBy(distance => Math.Round(distance, 5))
            .OrderBy(distance => distance)
            .ToList();

        for (var index = 1; index < splitDistances.Count; index++)
        {
            var startDistance = splitDistances[index - 1];
            var endDistance = splitDistances[index];
            if (endDistance - startDistance <= 0.000001)
            {
                continue;
            }

            var midDistance = traveled + ((startDistance + endDistance) / 2d);
            var inTab = tabIntervals.Any(tab => midDistance >= tab.StartX && midDistance <= tab.EndX);
            var t = endDistance / segmentLength;
            Line(
                moves,
                start.X + ((end.X - start.X) * t),
                start.Y + ((end.Y - start.Y) * t),
                inTab ? tabZ : level,
                feedRate,
                true,
                inTab ? "Contour tab" : string.Empty);
        }
    }

    private static void AddZLevelLoop(
        List<ToolpathMove> moves,
        IReadOnlyList<Point3> points,
        ToolpathOperationDefinition operation,
        double safeZ,
        double clearanceZ,
        double level)
    {
        if (points.Count < 2)
        {
            return;
        }

        var start = points[0];
        var leadStart = GetLeadPoint(points, Math.Max(0, operation.LeadInLength), leadIn: true) ?? start;
        var leadEnd = GetLeadPoint(points, Math.Max(0, operation.LeadOutLength), leadIn: false);

        Rapid(moves, leadStart.X, leadStart.Y, safeZ, $"Z-level finish at Z{FormatValue(level)}");
        Rapid(moves, leadStart.X, leadStart.Y, clearanceZ);
        Line(moves, leadStart.X, leadStart.Y, leadStart.Z, operation.PlungeRate, false);
        if (!PointsEqual2D(leadStart, start))
        {
            Line(moves, start.X, start.Y, start.Z, operation.FeedRate, true, "Z-level lead-in");
        }

        for (var index = 1; index < points.Count; index++)
        {
            Line(moves, points[index].X, points[index].Y, points[index].Z, operation.FeedRate, true);
        }

        if (leadEnd is not null)
        {
            Line(moves, leadEnd.Value.X, leadEnd.Value.Y, leadEnd.Value.Z, operation.FeedRate, true, "Z-level lead-out");
            Rapid(moves, leadEnd.Value.X, leadEnd.Value.Y, safeZ);
        }
        else
        {
            Rapid(moves, points[^1].X, points[^1].Y, safeZ);
        }
    }

    private static void AddParallelSurfaceSegment(
        List<ToolpathMove> moves,
        IReadOnlyList<Point3> points,
        ToolpathOperationDefinition operation,
        double safeZ,
        double clearanceZ)
    {
        if (points.Count < 2)
        {
            return;
        }

        Rapid(moves, points[0].X, points[0].Y, safeZ, "3D parallel finish");
        Rapid(moves, points[0].X, points[0].Y, clearanceZ);
        Line(moves, points[0].X, points[0].Y, points[0].Z, operation.PlungeRate, false);

        for (var index = 1; index < points.Count; index++)
        {
            Line(moves, points[index].X, points[index].Y, points[index].Z, operation.FeedRate, true);
        }

        Rapid(moves, points[^1].X, points[^1].Y, safeZ);
    }

    private static void AddScallopSurfaceSegment(
        List<ToolpathMove> moves,
        IReadOnlyList<Point3> points,
        ToolpathOperationDefinition operation,
        double safeZ,
        double clearanceZ)
    {
        if (points.Count < 2)
        {
            return;
        }

        Rapid(moves, points[0].X, points[0].Y, safeZ, "Scallop finish");
        Rapid(moves, points[0].X, points[0].Y, clearanceZ);
        Line(moves, points[0].X, points[0].Y, points[0].Z, operation.PlungeRate, false);

        for (var index = 1; index < points.Count; index++)
        {
            Line(moves, points[index].X, points[index].Y, points[index].Z, operation.FeedRate, true);
        }

        Rapid(moves, points[^1].X, points[^1].Y, safeZ);
    }

    private static void AddPencilCleanupPath(
        List<ToolpathMove> moves,
        IReadOnlyList<Point3> points,
        ToolpathOperationDefinition operation,
        double safeZ,
        double clearanceZ)
    {
        if (points.Count < 2)
        {
            return;
        }

        var first = points[0];
        Rapid(moves, first.X, first.Y, safeZ, "Pencil cleanup");
        Rapid(moves, first.X, first.Y, clearanceZ);
        Line(moves, first.X, first.Y, first.Z, operation.PlungeRate, false);

        for (var index = 1; index < points.Count; index++)
        {
            Line(moves, points[index].X, points[index].Y, points[index].Z, operation.FeedRate, true);
        }

        Rapid(moves, points[^1].X, points[^1].Y, safeZ);
    }

    private static List<Point3> BuildSelectedPencilPath(
        JobSetup setup,
        ToolpathOperationDefinition operation,
        double previewStockTopZ)
    {
        _ = previewStockTopZ;
        return operation.Feature.PathPoints
            .Select(point => new Point3(
                setup.WorkOffset.X + setup.AlignmentOffsetX + point.X,
                setup.WorkOffset.Y + setup.AlignmentOffsetY + point.Y,
                setup.WorkOffset.Z + point.Z + Math.Max(0, operation.FinishStockAxial)))
            .Where(point => !double.IsNaN(point.X) && !double.IsNaN(point.Y) && !double.IsNaN(point.Z))
            .ToList();
    }

    private static List<List<Point3>> BuildAutomaticPencilPaths(
        IReadOnlyList<PreviewEdgeGeometry> edges,
        double previewStockTopZ,
        double workOffsetZ,
        double lowerLimitZ,
        double modelTopZ,
        ToolDefinition tool,
        double axialStock)
    {
        var paths = new List<List<Point3>>();
        var minLength = Math.Max(0.6, GetToolRadius(tool) * 0.45);
        var topSkip = Math.Max(0.08, GetToolRadius(tool) * 0.12);

        foreach (var edge in edges)
        {
            if (edge.Points.Count < 2)
            {
                continue;
            }

            var path = edge.Points
                .Select(point => new Point3(
                    point.X,
                    point.Y,
                    PreviewZToToolZ(point.Z, previewStockTopZ, workOffsetZ) + Math.Max(0, axialStock)))
                .Where(point => !double.IsNaN(point.X) && !double.IsNaN(point.Y) && !double.IsNaN(point.Z))
                .ToList();
            if (path.Count < 2 || GetPathLength(path) < minLength)
            {
                continue;
            }

            var maxZ = path.Max(point => point.Z);
            var minZ = path.Min(point => point.Z);
            if (maxZ < lowerLimitZ - 0.0001 || minZ > modelTopZ - topSkip)
            {
                continue;
            }

            paths.Add(path);
        }

        return paths;
    }

    private static List<Point3> BuildPencilToolCenterPath(
        IReadOnlyList<Point3> modelEdgePath,
        ToolpathOperationDefinition operation,
        ToolDefinition tool,
        LoadedPreviewGeometry partGeometry,
        double previewStockTopZ,
        double workOffsetZ)
    {
        var centerlineOffset = GetToolRadius(tool) + Math.Max(0, operation.FinishStockRadial);
        var preferredPath = ApplySourceZToOffsetPath(
            OffsetProfilePath(modelEdgePath, centerlineOffset, operation.Feature.InsideProfile),
            modelEdgePath);

        var alternatePath = ApplySourceZToOffsetPath(
            OffsetProfilePath(modelEdgePath, centerlineOffset, !operation.Feature.InsideProfile),
            modelEdgePath);

        if (alternatePath.Count >= 2
            && ScorePencilToolCenterPath(alternatePath, tool, partGeometry.SurfaceTriangles, previewStockTopZ, workOffsetZ, operation.FinishStockAxial)
            < ScorePencilToolCenterPath(preferredPath, tool, partGeometry.SurfaceTriangles, previewStockTopZ, workOffsetZ, operation.FinishStockAxial) - 0.001)
        {
            return alternatePath;
        }

        return preferredPath;
    }

    private static List<Point3> ApplySourceZToOffsetPath(IReadOnlyList<Point3> offsetPath, IReadOnlyList<Point3> sourcePath)
    {
        return offsetPath
            .Select((point, index) => new Point3(
                point.X,
                point.Y,
                sourcePath[Math.Min(index, sourcePath.Count - 1)].Z))
            .ToList();
    }

    private static double ScorePencilToolCenterPath(
        IReadOnlyList<Point3> path,
        ToolDefinition tool,
        IReadOnlyList<PreviewTriangle> triangles,
        double previewStockTopZ,
        double workOffsetZ,
        double axialStock)
    {
        if (triangles.Count == 0)
        {
            return 0;
        }

        var score = 0d;
        for (var index = 0; index < path.Count; index++)
        {
            score += ScorePencilToolCenterPoint(path[index], tool, triangles, previewStockTopZ, workOffsetZ, axialStock);
            if (index == 0)
            {
                continue;
            }

            var previous = path[index - 1];
            var midpoint = new Point3(
                (previous.X + path[index].X) * 0.5,
                (previous.Y + path[index].Y) * 0.5,
                (previous.Z + path[index].Z) * 0.5);
            score += ScorePencilToolCenterPoint(midpoint, tool, triangles, previewStockTopZ, workOffsetZ, axialStock);
        }

        return score;
    }

    private static double ScorePencilToolCenterPoint(
        Point3 point,
        ToolDefinition tool,
        IReadOnlyList<PreviewTriangle> triangles,
        double previewStockTopZ,
        double workOffsetZ,
        double axialStock)
    {
        if (!TryGetToolCompensatedSurfaceZAtXY(
            triangles,
            tool,
            previewStockTopZ,
            workOffsetZ,
            point.X,
            point.Y,
            Math.Max(0, axialStock) + 0.02,
            out var requiredZ))
        {
            return 0;
        }

        var raise = requiredZ - point.Z;
        if (raise > 0.02)
        {
            return 100 + (raise * 10);
        }

        return raise > -0.02 ? 1 : 0;
    }

    private static IEnumerable<List<Point3>> BuildPencilPasses(IReadOnlyList<Point3> sourcePath, ToolpathOperationDefinition operation)
    {
        yield return sourcePath.ToList();

        var extraPasses = Math.Max(0, operation.PathCount - 1);
        var stepOver = Math.Max(0, operation.StepOver);
        if (extraPasses == 0 || stepOver <= 0.0001 || sourcePath.Count < 2)
        {
            yield break;
        }

        for (var pass = 1; pass <= extraPasses; pass++)
        {
            var offset = stepOver * pass;
            var offsetPath = OffsetProfilePath(sourcePath, offset, operation.Feature.InsideProfile)
                .Select((point, index) => new Point3(point.X, point.Y, sourcePath[Math.Min(index, sourcePath.Count - 1)].Z))
                .ToList();
            if (offsetPath.Count >= 2)
            {
                yield return offsetPath;
            }
        }
    }

    private static List<List<Point3>> BuildModelSafePencilPasses(
        IReadOnlyList<Point3> sourcePath,
        ToolpathOperationDefinition operation,
        ToolDefinition tool,
        IReadOnlyList<PreviewTriangle> triangles,
        double previewStockTopZ,
        double workOffsetZ)
    {
        var segments = new List<List<Point3>>();
        if (sourcePath.Count < 2)
        {
            return segments;
        }

        var toolRadius = GetToolRadius(tool);
        var sampleSpacing = Math.Clamp(toolRadius * 0.35, 0.15, 1.25);
        var maxAllowedRaise = operation.Feature.Shape == FeatureShape.EdgePath
            ? double.PositiveInfinity
            : Math.Max(1.0, toolRadius * 1.25);
        var current = new List<Point3>();

        for (var index = 0; index < sourcePath.Count; index++)
        {
            if (index == 0)
            {
                AppendSafePencilPoint(sourcePath[index]);
                continue;
            }

            var start = sourcePath[index - 1];
            var end = sourcePath[index];
            var steps = Math.Max(1, (int)Math.Ceiling(Distance2D(start, end) / sampleSpacing));
            for (var step = 1; step <= steps; step++)
            {
                var t = step / (double)steps;
                AppendSafePencilPoint(new Point3(
                    start.X + ((end.X - start.X) * t),
                    start.Y + ((end.Y - start.Y) * t),
                    start.Z + ((end.Z - start.Z) * t)));
            }
        }

        FlushSegment();
        return segments;

        void AppendSafePencilPoint(Point3 point)
        {
            if (!TryGetSafePencilPoint(
                point,
                tool,
                triangles,
                previewStockTopZ,
                workOffsetZ,
                operation.FinishStockAxial,
                out var safePoint,
                out var raise)
                || raise > maxAllowedRaise)
            {
                FlushSegment();
                return;
            }

            if (current.Count == 0
                || !PointsNear2D(current[^1], safePoint, 0.0001)
                || Math.Abs(current[^1].Z - safePoint.Z) > 0.0001)
            {
                current.Add(safePoint);
            }
        }

        void FlushSegment()
        {
            if (current.Count >= 2 && GetPathLength(current) > Math.Max(0.25, toolRadius * 0.2))
            {
                segments.Add(current.ToList());
            }

            current.Clear();
        }
    }

    private static bool TryGetSafePencilPoint(
        Point3 sourcePoint,
        ToolDefinition tool,
        IReadOnlyList<PreviewTriangle> triangles,
        double previewStockTopZ,
        double workOffsetZ,
        double axialStock,
        out Point3 safePoint,
        out double raise)
    {
        safePoint = sourcePoint;
        raise = 0;
        if (triangles.Count == 0)
        {
            return true;
        }

        if (!TryGetToolCompensatedSurfaceZAtXY(
            triangles,
            tool,
            previewStockTopZ,
            workOffsetZ,
            sourcePoint.X,
            sourcePoint.Y,
            Math.Max(0, axialStock) + 0.02,
            out var requiredZ))
        {
            return true;
        }

        var safeZ = Math.Max(sourcePoint.Z, requiredZ);
        raise = safeZ - sourcePoint.Z;
        safePoint = new Point3(sourcePoint.X, sourcePoint.Y, safeZ);
        return true;
    }

    private static List<Point3> BuildScallopRing(
        IReadOnlyList<PreviewTriangle> triangles,
        ToolDefinition tool,
        double previewStockTopZ,
        double workOffsetZ,
        double centerX,
        double centerY,
        double radiusX,
        double radiusY,
        double startAngle,
        double sampleSpacing,
        double lowerLimitZ,
        double axialStock)
    {
        var approximatePerimeter = Math.PI * (3d * (radiusX + radiusY) - Math.Sqrt((3d * radiusX + radiusY) * (radiusX + 3d * radiusY)));
        var sampleCount = Math.Clamp((int)Math.Ceiling(approximatePerimeter / sampleSpacing), 32, 720);
        var points = new List<Point3>(sampleCount + 1);

        for (var sample = 0; sample <= sampleCount; sample++)
        {
            var angle = startAngle + (Math.PI * 2d * sample / sampleCount);
            var x = centerX + (Math.Cos(angle) * radiusX);
            var y = centerY + (Math.Sin(angle) * radiusY);
            if (!TryBuildToolCompensatedSurfacePoint(
                triangles,
                tool,
                previewStockTopZ,
                workOffsetZ,
                x,
                y,
                lowerLimitZ,
                axialStock,
                out var point))
            {
                points.Add(new Point3(double.NaN, double.NaN, double.NaN));
                continue;
            }

            points.Add(point);
        }

        return points;
    }

    private static List<Point3> BuildParallelSurfaceLane(
        IReadOnlyList<PreviewTriangle> triangles,
        ToolDefinition tool,
        double previewStockTopZ,
        double workOffsetZ,
        double minU,
        double maxU,
        double laneV,
        double ux,
        double uy,
        double vx,
        double vy,
        double sampleStep,
        double lowerLimitZ,
        double axialStock,
        bool reverse)
    {
        var points = new List<Point3>();
        var sampleCount = Math.Max(1, (int)Math.Ceiling((maxU - minU) / sampleStep));
        for (var sample = 0; sample <= sampleCount; sample++)
        {
            var t = sample / (double)sampleCount;
            var u = reverse
                ? maxU - ((maxU - minU) * t)
                : minU + ((maxU - minU) * t);
            var x = (u * ux) + (laneV * vx);
            var y = (u * uy) + (laneV * vy);
            if (!TryBuildToolCompensatedSurfacePoint(
                triangles,
                tool,
                previewStockTopZ,
                workOffsetZ,
                x,
                y,
                lowerLimitZ,
                axialStock,
                out var point))
            {
                points.Add(new Point3(double.NaN, double.NaN, double.NaN));
                continue;
            }

            points.Add(point);
        }

        return points;
    }

    private static bool TryBuildToolCompensatedSurfacePoint(
        IReadOnlyList<PreviewTriangle> triangles,
        ToolDefinition tool,
        double previewStockTopZ,
        double workOffsetZ,
        double x,
        double y,
        double lowerLimitZ,
        double axialStock,
        out Point3 point)
    {
        point = new Point3(double.NaN, double.NaN, double.NaN);
        if (!TryGetTopSurfaceZAtXY(triangles, x, y, out var centerPreviewZ))
        {
            return false;
        }

        var centerSurfaceZ = PreviewZToToolZ(centerPreviewZ, previewStockTopZ, workOffsetZ);
        if (centerSurfaceZ < lowerLimitZ - 0.0001)
        {
            return false;
        }

        if (!TryGetToolCompensatedSurfaceZAtXY(triangles, tool, previewStockTopZ, workOffsetZ, x, y, axialStock, out var toolZ))
        {
            return false;
        }

        point = new Point3(x, y, Math.Max(toolZ, centerSurfaceZ + Math.Max(0, axialStock)));
        return true;
    }

    private static bool TryGetToolCompensatedSurfaceZAtXY(
        IReadOnlyList<PreviewTriangle> triangles,
        ToolDefinition tool,
        double previewStockTopZ,
        double workOffsetZ,
        double x,
        double y,
        double axialStock,
        out double toolZ)
    {
        var found = false;
        var requiredZ = double.NegativeInfinity;
        var toolRadius = GetToolRadius(tool);

        ConsiderSurfaceSample(x, y, 0);
        var radialSteps = tool.Style is ToolStyle.Ball or ToolStyle.Bull or ToolStyle.Lollipop ? 3 : 2;
        for (var ring = 1; ring <= radialSteps; ring++)
        {
            var sampleRadius = toolRadius * ring / radialSteps;
            var angularSteps = Math.Clamp(
                (int)Math.Ceiling((Math.PI * 2d * sampleRadius) / Math.Max(toolRadius * 0.55, 0.5)),
                8,
                20);

            for (var angleIndex = 0; angleIndex < angularSteps; angleIndex++)
            {
                var angle = Math.PI * 2d * angleIndex / angularSteps;
                ConsiderSurfaceSample(
                    x + (Math.Cos(angle) * sampleRadius),
                    y + (Math.Sin(angle) * sampleRadius),
                    sampleRadius);
            }
        }

        toolZ = requiredZ;
        return found;

        void ConsiderSurfaceSample(double sampleX, double sampleY, double radialDistance)
        {
            if (!TryGetTopSurfaceZAtXY(triangles, sampleX, sampleY, out var samplePreviewZ))
            {
                return;
            }

            var sampleSurfaceZ = PreviewZToToolZ(samplePreviewZ, previewStockTopZ, workOffsetZ);
            var profileHeight = CutterGeometry.BottomProfileHeightAtRadius(tool, radialDistance);
            requiredZ = Math.Max(requiredZ, sampleSurfaceZ - profileHeight + Math.Max(0, axialStock));
            found = true;
        }
    }

    private static List<List<Point3>> SplitParallelLane(IReadOnlyList<Point3> lanePoints, double sampleStep, double stepOver)
    {
        var segments = new List<List<Point3>>();
        var current = new List<Point3>();
        var maxGap = Math.Max(sampleStep * 1.85, 0.25);
        var maxZJump = Math.Max(stepOver * 3.5, 2.5);

        foreach (var point in lanePoints)
        {
            if (double.IsNaN(point.X) || double.IsNaN(point.Y) || double.IsNaN(point.Z))
            {
                AddParallelSegmentIfUseful(segments, current);
                current = new List<Point3>();
                continue;
            }

            if (current.Count > 0)
            {
                var previous = current[^1];
                var xyGap = Distance2D(previous, point);
                if (xyGap > maxGap || Math.Abs(previous.Z - point.Z) > maxZJump)
                {
                    AddParallelSegmentIfUseful(segments, current);
                    current = new List<Point3>();
                }
            }

            current.Add(point);
        }

        AddParallelSegmentIfUseful(segments, current);
        return segments;
    }

    private static void AddParallelSegmentIfUseful(List<List<Point3>> segments, List<Point3> segment)
    {
        if (segment.Count >= 2 && GetPathLength(segment) > 0.25)
        {
            segments.Add(segment);
        }
    }

    private static bool TryGetTopSurfaceZAtXY(IReadOnlyList<PreviewTriangle> triangles, double x, double y, out double z)
    {
        var found = false;
        z = double.NegativeInfinity;
        foreach (var triangle in triangles)
        {
            if (TryGetTriangleZAtXY(triangle, x, y, out var candidateZ) && candidateZ > z)
            {
                z = candidateZ;
                found = true;
            }
        }

        return found;
    }

    private static bool TryGetTriangleZAtXY(PreviewTriangle triangle, double x, double y, out double z)
    {
        const double tolerance = 0.000001;
        var x1 = triangle.A.X;
        var y1 = triangle.A.Y;
        var x2 = triangle.B.X;
        var y2 = triangle.B.Y;
        var x3 = triangle.C.X;
        var y3 = triangle.C.Y;
        var denominator = ((y2 - y3) * (x1 - x3)) + ((x3 - x2) * (y1 - y3));
        z = 0;
        if (Math.Abs(denominator) <= tolerance)
        {
            return false;
        }

        var a = (((y2 - y3) * (x - x3)) + ((x3 - x2) * (y - y3))) / denominator;
        var b = (((y3 - y1) * (x - x3)) + ((x1 - x3) * (y - y3))) / denominator;
        var c = 1d - a - b;
        if (a < -0.0001 || b < -0.0001 || c < -0.0001)
        {
            return false;
        }

        z = (a * triangle.A.Z) + (b * triangle.B.Z) + (c * triangle.C.Z);
        return true;
    }

    private static (double MinU, double MaxU, double MinV, double MaxV) GetRotatedBounds(
        Rect3D bounds,
        double ux,
        double uy,
        double vx,
        double vy)
    {
        var corners = new[]
        {
            new Point2(bounds.X, bounds.Y),
            new Point2(bounds.X + bounds.SizeX, bounds.Y),
            new Point2(bounds.X, bounds.Y + bounds.SizeY),
            new Point2(bounds.X + bounds.SizeX, bounds.Y + bounds.SizeY)
        };
        var projected = corners
            .Select(point => (
                U: (point.X * ux) + (point.Y * uy),
                V: (point.X * vx) + (point.Y * vy)))
            .ToList();

        return (
            projected.Min(point => point.U),
            projected.Max(point => point.U),
            projected.Min(point => point.V),
            projected.Max(point => point.V));
    }

    private static List<List<Point3>> BuildWaterlineLoops(
        IReadOnlyList<PreviewTriangle> triangles,
        double sliceZ,
        double tolerance)
    {
        var segments = new List<WaterlineSegment>();
        foreach (var triangle in triangles)
        {
            if (TrySliceTriangle(triangle, sliceZ, tolerance, out var segment))
            {
                segments.Add(segment);
            }
        }

        return ConnectWaterlineSegments(segments, tolerance)
            .Where(loop => loop.Count >= 2)
            .ToList();
    }

    private static bool TrySliceTriangle(
        PreviewTriangle triangle,
        double sliceZ,
        double tolerance,
        out WaterlineSegment segment)
    {
        var intersections = new List<Point3>();
        AddTriangleEdgeIntersection(intersections, triangle.A, triangle.B, sliceZ, tolerance);
        AddTriangleEdgeIntersection(intersections, triangle.B, triangle.C, sliceZ, tolerance);
        AddTriangleEdgeIntersection(intersections, triangle.C, triangle.A, sliceZ, tolerance);

        segment = default;
        if (intersections.Count < 2)
        {
            return false;
        }

        var first = intersections[0];
        var second = intersections[1];
        var maxDistance = Distance2D(first, second);
        for (var i = 0; i < intersections.Count; i++)
        {
            for (var j = i + 1; j < intersections.Count; j++)
            {
                var distance = Distance2D(intersections[i], intersections[j]);
                if (distance > maxDistance)
                {
                    first = intersections[i];
                    second = intersections[j];
                    maxDistance = distance;
                }
            }
        }

        if (maxDistance <= tolerance * 0.2)
        {
            return false;
        }

        segment = new WaterlineSegment(first, second);
        return true;
    }

    private static void AddTriangleEdgeIntersection(
        List<Point3> intersections,
        Point3D start,
        Point3D end,
        double sliceZ,
        double tolerance)
    {
        var startOffset = start.Z - sliceZ;
        var endOffset = end.Z - sliceZ;
        if (Math.Abs(start.Z - end.Z) <= tolerance * 0.05)
        {
            return;
        }

        if ((startOffset > tolerance && endOffset > tolerance) ||
            (startOffset < -tolerance && endOffset < -tolerance))
        {
            return;
        }

        var t = (sliceZ - start.Z) / (end.Z - start.Z);
        if (t < -0.0001 || t > 1.0001)
        {
            return;
        }

        var point = new Point3(
            start.X + ((end.X - start.X) * t),
            start.Y + ((end.Y - start.Y) * t),
            sliceZ);
        if (!intersections.Any(existing => Distance2D(existing, point) <= tolerance * 0.35))
        {
            intersections.Add(point);
        }
    }

    private static List<List<Point3>> ConnectWaterlineSegments(IReadOnlyList<WaterlineSegment> sourceSegments, double tolerance)
    {
        var remaining = sourceSegments.ToList();
        var loops = new List<List<Point3>>();
        while (remaining.Count > 0)
        {
            var current = remaining[0];
            remaining.RemoveAt(0);
            var path = new List<Point3> { current.Start, current.End };
            var extended = true;

            while (extended)
            {
                extended = false;
                for (var index = 0; index < remaining.Count; index++)
                {
                    var segment = remaining[index];
                    if (PointsNear2D(path[^1], segment.Start, tolerance))
                    {
                        path.Add(segment.End);
                    }
                    else if (PointsNear2D(path[^1], segment.End, tolerance))
                    {
                        path.Add(segment.Start);
                    }
                    else if (PointsNear2D(path[0], segment.End, tolerance))
                    {
                        path.Insert(0, segment.Start);
                    }
                    else if (PointsNear2D(path[0], segment.Start, tolerance))
                    {
                        path.Insert(0, segment.End);
                    }
                    else
                    {
                        continue;
                    }

                    remaining.RemoveAt(index);
                    extended = true;
                    break;
                }
            }

            path = RemoveNearDuplicatePoints(path, tolerance * 0.3);
            if (path.Count >= 3 && PointsNear2D(path[0], path[^1], tolerance))
            {
                path[^1] = path[0];
            }

            if (path.Count >= 2)
            {
                loops.Add(path);
            }
        }

        return loops;
    }

    private static List<Point3> RemoveNearDuplicatePoints(IReadOnlyList<Point3> points, double tolerance)
    {
        var result = new List<Point3>();
        foreach (var point in points)
        {
            if (result.Count == 0 || !PointsNear2D(result[^1], point, tolerance))
            {
                result.Add(point);
            }
        }

        return result;
    }

    private static bool PointsNear2D(Point3 first, Point3 second, double tolerance)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return (dx * dx) + (dy * dy) <= tolerance * tolerance;
    }

    private static double ToolZToPreviewZ(double toolZ, double previewStockTopZ, double workOffsetZ)
    {
        return toolZ + (previewStockTopZ - workOffsetZ);
    }

    private static double PreviewZToToolZ(double previewZ, double previewStockTopZ, double workOffsetZ)
    {
        return previewZ - (previewStockTopZ - workOffsetZ);
    }

    private static double GetWaterlineTolerance(Rect3D bounds)
    {
        var largest = Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
        return Math.Clamp(largest * 0.0008, 0.015, 0.35);
    }

    private static void AddAdaptiveClearingInterval(
        List<ToolpathMove> moves,
        JobSetup setup,
        ToolpathOperationDefinition operation,
        Bounds2D bounds,
        IReadOnlyList<List<Point2>> keepouts,
        double toolRadius,
        double engagement,
        double startX,
        double endX,
        double y,
        double level)
    {
        var distance = Math.Abs(endX - startX);
        if (distance <= 0.000001)
        {
            return;
        }

        var minAdvance = Math.Max(0.35, toolRadius * 0.45);
        var maxAdvance = Math.Max(minAdvance, toolRadius * 1.35);
        var advance = Math.Min(maxAdvance, Math.Max(minAdvance, engagement * 2.5));
        var loopCount = Math.Max(1, (int)Math.Ceiling(distance / advance));
        var samples = Math.Max(loopCount * 8, 8);
        var amplitude = Math.Min(Math.Max(engagement * 0.45, 0.04), Math.Max(toolRadius * 0.55, 0.05));

        for (var sample = 1; sample <= samples; sample++)
        {
            var t = sample / (double)samples;
            var x = startX + ((endX - startX) * t);
            var wave = Math.Sin(t * loopCount * Math.PI * 2d) * amplitude;
            var candidateY = y + wave;
            var targetY = IsAdaptivePointAllowed(setup, bounds, toolRadius, keepouts, x, candidateY)
                ? candidateY
                : y;

            Line(
                moves,
                x,
                targetY,
                level,
                operation.FeedRate,
                true,
                sample == 1 ? "Adaptive constant-engagement peel" : string.Empty);
        }
    }

    private static void AddChamferEdgePass(
        List<ToolpathMove> moves,
        JobSetup setup,
        ToolpathOperationDefinition operation,
        double safeZ,
        double clearanceZ,
        double level,
        double centerlineOffset,
        double chamferWidth)
    {
        var points = operation.Feature.PathPoints
            .Select(point => new Point3(
                setup.WorkOffset.X + setup.AlignmentOffsetX + point.X,
                setup.WorkOffset.Y + setup.AlignmentOffsetY + point.Y,
                level))
            .Where(point => !double.IsNaN(point.X) && !double.IsNaN(point.Y))
            .ToList();

        points = OffsetProfilePath(points, centerlineOffset, operation.Feature.InsideProfile);
        if (points.Count < 2)
        {
            return;
        }

        Rapid(moves, points[0].X, points[0].Y, safeZ, $"Chamfer model edge W{FormatValue(chamferWidth)} at Z{FormatValue(level)}");
        Rapid(moves, points[0].X, points[0].Y, clearanceZ);
        Line(moves, points[0].X, points[0].Y, points[0].Z, operation.PlungeRate, false);

        for (var index = 1; index < points.Count; index++)
        {
            Line(moves, points[index].X, points[index].Y, points[index].Z, operation.FeedRate, true);
        }

        Rapid(moves, points[^1].X, points[^1].Y, safeZ);
    }

    private static List<Point3> OffsetProfilePath(IReadOnlyList<Point3> sourcePoints, double offset, bool insideProfile)
    {
        if (sourcePoints.Count < 2 || offset <= 0.0001)
        {
            return sourcePoints.ToList();
        }

        var points = sourcePoints.ToList();
        var closed = IsClosedPath(points);
        if (closed)
        {
            points.RemoveAt(points.Count - 1);
        }

        if (points.Count < 2)
        {
            return sourcePoints.ToList();
        }

        var signedArea = closed ? CalculateSignedArea(points) : 0d;
        var useLeftNormal = closed
            ? insideProfile == signedArea > 0
            : insideProfile;
        var segmentNormals = new List<(double X, double Y)>();
        var segmentCount = closed ? points.Count : points.Count - 1;

        for (var index = 0; index < segmentCount; index++)
        {
            var start = points[index];
            var end = points[(index + 1) % points.Count];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length <= 0.000001)
            {
                segmentNormals.Add((0, 0));
                continue;
            }

            var leftX = -dy / length;
            var leftY = dx / length;
            segmentNormals.Add(useLeftNormal ? (leftX, leftY) : (-leftX, -leftY));
        }

        var offsetPoints = new List<Point3>();
        for (var index = 0; index < points.Count; index++)
        {
            (double X, double Y) normal;
            if (!closed && index == 0)
            {
                normal = segmentNormals[0];
            }
            else if (!closed && index == points.Count - 1)
            {
                normal = segmentNormals[^1];
            }
            else
            {
                var previous = segmentNormals[(index - 1 + segmentNormals.Count) % segmentNormals.Count];
                var current = segmentNormals[index % segmentNormals.Count];
                normal = Normalize2D(previous.X + current.X, previous.Y + current.Y);
                if (Math.Abs(normal.X) < 0.000001 && Math.Abs(normal.Y) < 0.000001)
                {
                    normal = current;
                }
            }

            offsetPoints.Add(new Point3(
                points[index].X + (normal.X * offset),
                points[index].Y + (normal.Y * offset),
                points[index].Z));
        }

        if (closed && offsetPoints.Count > 0)
        {
            offsetPoints.Add(offsetPoints[0]);
        }

        return offsetPoints;
    }

    private static (double X, double Y) Normalize2D(double x, double y)
    {
        var length = Math.Sqrt((x * x) + (y * y));
        return length <= 0.000001 ? (0, 0) : (x / length, y / length);
    }

    private static double Distance2D(Point3 start, Point3 end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double GetPathLength(IReadOnlyList<Point3> points)
    {
        var length = 0d;
        for (var index = 1; index < points.Count; index++)
        {
            length += Distance2D(points[index - 1], points[index]);
        }

        return length;
    }

    private static bool IsClosedPath(IReadOnlyList<Point3> points)
    {
        if (points.Count < 3)
        {
            return false;
        }

        var first = points[0];
        var last = points[^1];
        var dx = last.X - first.X;
        var dy = last.Y - first.Y;
        return (dx * dx) + (dy * dy) <= 0.0001;
    }

    private static double CalculateSignedArea(IReadOnlyList<Point3> points)
    {
        var area = 0d;
        for (var index = 0; index < points.Count; index++)
        {
            var current = points[index];
            var next = points[(index + 1) % points.Count];
            area += (current.X * next.Y) - (next.X * current.Y);
        }

        return area / 2d;
    }

    private static void AddCircularProfilePass(
        List<ToolpathMove> moves,
        JobSetup setup,
        ToolpathOperationDefinition operation,
        double safeZ,
        double clearanceZ,
        double level,
        double toolRadius)
    {
        var centerX = setup.WorkOffset.X + setup.AlignmentOffsetX + operation.Feature.CenterX;
        var centerY = setup.WorkOffset.Y + setup.AlignmentOffsetY + operation.Feature.CenterY;
        var offset = toolRadius + operation.FinishStockRadial;
        var baseRadius = operation.Feature.Diameter / 2d;
        var radius = operation.Feature.InsideProfile ? Math.Max(0.5, baseRadius - offset) : baseRadius + offset;
        var startX = centerX + radius;
        var startY = centerY;

        Rapid(moves, startX, startY, safeZ, $"Circular profile at Z{FormatValue(level)}");
        Rapid(moves, startX, startY, clearanceZ);
        Line(moves, startX, startY, level, operation.PlungeRate, false);
        AddCircularPath(moves, centerX, centerY, radius, level, operation.FeedRate, operation.ClimbMilling);
        Rapid(moves, startX, startY, safeZ);
    }

    private static void AddCircularPath(
        List<ToolpathMove> moves,
        double centerX,
        double centerY,
        double radius,
        double level,
        double feedRate,
        bool clockwise)
    {
        const int segments = 48;
        for (var segment = 1; segment <= segments; segment++)
        {
            var angle = clockwise
                ? -segment * ((Math.PI * 2) / segments)
                : segment * ((Math.PI * 2) / segments);
            var x = centerX + (Math.Cos(angle) * radius);
            var y = centerY + (Math.Sin(angle) * radius);
            Line(moves, x, y, level, feedRate, true);
        }
    }

    private static string BuildOperationSummary(ToolpathOperationDefinition operation, IReadOnlyList<ToolpathMove> moves)
    {
        if (moves.Count == 0)
        {
            return $"{operation.Name}: no motion generated.";
        }

        double cutDistance = 0;
        for (var index = 1; index < moves.Count; index++)
        {
            if (!moves[index].IsCutting)
            {
                continue;
            }

            cutDistance += Distance(moves[index - 1], moves[index]);
        }

        return $"{operation.Name}: {moves.Count} moves, {cutDistance:0.##} mm of cutting motion.";
    }

    private static List<double> BuildBulkRemovalLanes(JobSetup setup, Bounds2D bounds, double toolRadius, double stepOver)
    {
        var centerY = (bounds.MinY + bounds.MaxY) / 2d;
        var radius = ((bounds.MaxY - bounds.MinY) / 2d) + toolRadius;
        var minY = setup.Stock.Shape == StockShape.Cylinder
            ? centerY - radius
            : bounds.MinY - toolRadius;
        var maxY = setup.Stock.Shape == StockShape.Cylinder
            ? centerY + radius
            : bounds.MaxY + toolRadius;

        return BuildLinearPasses(minY, maxY, stepOver);
    }

    private static List<double> BuildRestRasterLanes(
        JobSetup setup,
        Bounds2D bounds,
        double toolRadius,
        double stepOver,
        RestStockMap restMap,
        double level)
    {
        var regularLanes = BuildBulkRemovalLanes(setup, bounds, toolRadius, stepOver);
        var restRows = restMap.GetMaterialRowsAbove(level + 0.01);
        if (restRows.Count == 0)
        {
            return regularLanes;
        }

        var stockMinY = setup.Stock.Shape == StockShape.Cylinder
            ? bounds.MinY - toolRadius
            : bounds.MinY - toolRadius;
        var stockMaxY = setup.Stock.Shape == StockShape.Cylinder
            ? bounds.MaxY + toolRadius
            : bounds.MaxY + toolRadius;
        var maxGap = Math.Max(Math.Min(Math.Abs(stepOver), Math.Max(toolRadius, restMap.CellSize)), restMap.CellSize);
        var lanes = new List<double>();

        foreach (var restY in restRows.Where(y => y >= stockMinY - toolRadius && y <= stockMaxY + toolRadius))
        {
            AddLaneIfDistinct(lanes, Math.Clamp(restY, stockMinY, stockMaxY), restMap.CellSize * 0.35);
        }

        foreach (var regularLane in regularLanes)
        {
            if (restRows.Any(restY => Math.Abs(restY - regularLane) <= maxGap * 0.5))
            {
                AddLaneIfDistinct(lanes, regularLane, restMap.CellSize * 0.35);
            }
        }

        lanes.Sort();
        return lanes.Count == 0 ? regularLanes : FillLargeLaneGaps(lanes, maxGap);
    }

    private static void AddLaneIfDistinct(List<double> lanes, double y, double tolerance)
    {
        if (lanes.Any(existing => Math.Abs(existing - y) <= tolerance))
        {
            return;
        }

        lanes.Add(y);
    }

    private static List<double> FillLargeLaneGaps(IReadOnlyList<double> sortedLanes, double maxGap)
    {
        if (sortedLanes.Count < 2)
        {
            return sortedLanes.ToList();
        }

        var lanes = new List<double> { sortedLanes[0] };
        for (var index = 1; index < sortedLanes.Count; index++)
        {
            var previous = lanes[^1];
            var current = sortedLanes[index];
            var gap = current - previous;
            if (gap > maxGap)
            {
                var fillerCount = (int)Math.Floor(gap / maxGap);
                for (var filler = 1; filler <= fillerCount; filler++)
                {
                    var y = previous + (maxGap * filler);
                    if (y < current - 0.0001)
                    {
                        lanes.Add(y);
                    }
                }
            }

            lanes.Add(current);
        }

        return lanes;
    }

    private static List<List<Point2>> BuildBulkKeepoutPolygons(JobSetup setup, FeatureDefinition feature, double keepoutOffset)
    {
        var keepouts = new List<List<Point2>>();
        foreach (var loop in feature.KeepoutLoops)
        {
            if (loop.Points.Count < 3)
            {
                continue;
            }

            var points = loop.Points
                .Select(point => new Point3(
                    setup.WorkOffset.X + setup.AlignmentOffsetX + point.X,
                    setup.WorkOffset.Y + setup.AlignmentOffsetY + point.Y,
                    0))
                .ToList();

            if (!IsClosedPath(points))
            {
                points.Add(points[0]);
            }

            var offsetPoints = OffsetProfilePath(points, keepoutOffset, insideProfile: false);
            var polygon = offsetPoints
                .Take(offsetPoints.Count > 1 && PointsEqual2D(offsetPoints[0], offsetPoints[^1]) ? offsetPoints.Count - 1 : offsetPoints.Count)
                .Select(point => new Point2(point.X, point.Y))
                .Where(point => !double.IsNaN(point.X) && !double.IsNaN(point.Y))
                .ToList();

            if (polygon.Count >= 3)
            {
                keepouts.Add(polygon);
            }
        }

        return keepouts;
    }

    private static List<CutInterval> BuildClearIntervalsAtY(
        JobSetup setup,
        Bounds2D bounds,
        double toolRadius,
        IReadOnlyList<List<Point2>> keepouts,
        double y)
    {
        var intervals = GetStockIntervalsAtY(setup, bounds, toolRadius, y);
        foreach (var keepout in keepouts)
        {
            foreach (var blocked in GetPolygonIntervalsAtY(keepout, y))
            {
                intervals = SubtractInterval(intervals, blocked);
                if (intervals.Count == 0)
                {
                    return intervals;
                }
            }
        }

        return intervals
            .Where(interval => interval.EndX - interval.StartX > Math.Max(0.05, toolRadius * 0.12))
            .ToList();
    }

    private static bool IsAdaptivePointAllowed(
        JobSetup setup,
        Bounds2D bounds,
        double toolRadius,
        IReadOnlyList<List<Point2>> keepouts,
        double x,
        double y)
    {
        var intervals = BuildClearIntervalsAtY(setup, bounds, toolRadius, keepouts, y);
        return intervals.Any(interval => x >= interval.StartX - 0.0001 && x <= interval.EndX + 0.0001);
    }

    private static List<CutInterval> SplitIntervalByRemainingStock(
        RestStockMap restMap,
        CutInterval interval,
        double y,
        double level,
        double stepOver)
    {
        _ = stepOver;
        var sampleStep = Math.Max(restMap.CellSize * 0.65, 0.05);
        var minLength = Math.Max(restMap.CellSize * 0.8, 0.05);
        var intervals = new List<CutInterval>();
        var active = false;
        var start = interval.StartX;
        var lastActive = interval.StartX;

        for (var x = interval.StartX; x <= interval.EndX + 0.0001; x += sampleStep)
        {
            var sampleX = Math.Min(x, interval.EndX);
            var hasMaterial = restMap.HasMaterialAbove(sampleX, y, level + 0.01);
            if (hasMaterial)
            {
                if (!active)
                {
                    start = Math.Max(interval.StartX, sampleX - (sampleStep * 0.5));
                    active = true;
                }

                lastActive = sampleX;
            }
            else if (active)
            {
                var end = Math.Min(interval.EndX, lastActive + (sampleStep * 0.5));
                if (end - start >= minLength)
                {
                    intervals.Add(new CutInterval(start, end));
                }

                active = false;
            }

            if (sampleX >= interval.EndX)
            {
                break;
            }
        }

        if (active)
        {
            var end = interval.EndX;
            if (end - start >= minLength)
            {
                intervals.Add(new CutInterval(start, end));
            }
        }

        return intervals;
    }

    private static List<CutInterval> GetStockIntervalsAtY(JobSetup setup, Bounds2D bounds, double toolRadius, double y)
    {
        if (setup.Stock.Shape != StockShape.Cylinder)
        {
            return new List<CutInterval>
            {
                new(bounds.MinX - toolRadius, bounds.MaxX + toolRadius)
            };
        }

        var centerX = (bounds.MinX + bounds.MaxX) / 2d;
        var centerY = (bounds.MinY + bounds.MaxY) / 2d;
        var radius = ((bounds.MaxX - bounds.MinX) / 2d) + toolRadius;
        var dy = y - centerY;
        var spanSquared = (radius * radius) - (dy * dy);
        if (spanSquared < 0)
        {
            return new List<CutInterval>();
        }

        var span = Math.Sqrt(spanSquared);
        return new List<CutInterval> { new(centerX - span, centerX + span) };
    }

    private static List<CutInterval> GetPolygonIntervalsAtY(IReadOnlyList<Point2> polygon, double y)
    {
        var intersections = new List<double>();
        for (var index = 0; index < polygon.Count; index++)
        {
            var start = polygon[index];
            var end = polygon[(index + 1) % polygon.Count];
            if (Math.Abs(end.Y - start.Y) < 0.000001)
            {
                continue;
            }

            var crosses = (start.Y <= y && end.Y > y) || (end.Y <= y && start.Y > y);
            if (!crosses)
            {
                continue;
            }

            var t = (y - start.Y) / (end.Y - start.Y);
            intersections.Add(start.X + ((end.X - start.X) * t));
        }

        intersections.Sort();
        var intervals = new List<CutInterval>();
        for (var index = 0; index + 1 < intersections.Count; index += 2)
        {
            var start = intersections[index];
            var end = intersections[index + 1];
            if (end > start)
            {
                intervals.Add(new CutInterval(start, end));
            }
        }

        return intervals;
    }

    private static List<CutInterval> SubtractInterval(IReadOnlyList<CutInterval> source, CutInterval blocked)
    {
        var result = new List<CutInterval>();
        foreach (var interval in source)
        {
            if (blocked.EndX <= interval.StartX || blocked.StartX >= interval.EndX)
            {
                result.Add(interval);
                continue;
            }

            if (blocked.StartX > interval.StartX)
            {
                result.Add(new CutInterval(interval.StartX, Math.Min(blocked.StartX, interval.EndX)));
            }

            if (blocked.EndX < interval.EndX)
            {
                result.Add(new CutInterval(Math.Max(blocked.EndX, interval.StartX), interval.EndX));
            }
        }

        return result;
    }

    private static bool CanCutCrossover(
        double startX,
        double startY,
        double endX,
        double endY,
        IReadOnlyList<List<Point2>> keepouts,
        double stepOver)
    {
        var dx = endX - startX;
        var dy = endY - startY;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        if (distance > Math.Max(stepOver * 2.75, 2.5))
        {
            return false;
        }

        var samples = Math.Max(2, (int)Math.Ceiling(distance / Math.Max(stepOver / 3d, 0.5)));
        for (var sample = 1; sample < samples; sample++)
        {
            var t = sample / (double)samples;
            var point = new Point2(startX + (dx * t), startY + (dy * t));
            if (keepouts.Any(keepout => IsPointInsidePolygon(point, keepout)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPointInsidePolygon(Point2 point, IReadOnlyList<Point2> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var current = polygon[i];
            var previous = polygon[j];
            if (((current.Y > point.Y) != (previous.Y > point.Y))
                && point.X < ((previous.X - current.X) * (point.Y - current.Y) / (previous.Y - current.Y)) + current.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool PointsEqual2D(Point3 first, Point3 second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return (dx * dx) + (dy * dy) <= 0.000001;
    }

    private static List<Point3> GetFeaturePoints(JobSetup setup, FeatureDefinition feature)
    {
        var centerX = setup.WorkOffset.X + setup.AlignmentOffsetX + feature.CenterX;
        var centerY = setup.WorkOffset.Y + setup.AlignmentOffsetY + feature.CenterY;
        var points = new List<Point3>();

        if (feature.Shape != FeatureShape.HolePattern)
        {
            points.Add(new Point3(centerX, centerY, setup.WorkOffset.Z + feature.StartZ));
            return points;
        }

        var totalPitchX = (Math.Max(feature.Columns, 1) - 1) * feature.PitchX;
        var totalPitchY = (Math.Max(feature.Rows, 1) - 1) * feature.PitchY;
        var originX = centerX - (totalPitchX / 2d);
        var originY = centerY - (totalPitchY / 2d);

        for (var row = 0; row < Math.Max(feature.Rows, 1); row++)
        {
            for (var column = 0; column < Math.Max(feature.Columns, 1); column++)
            {
                points.Add(new Point3(
                    originX + (column * feature.PitchX),
                    originY + (row * feature.PitchY),
                    setup.WorkOffset.Z + feature.StartZ));
            }
        }

        return points;
    }

    private static List<double> BuildLinearPasses(double start, double end, double step)
    {
        var passes = new List<double>();
        var safeStep = Math.Max(0.25, Math.Abs(step));

        if (end < start)
        {
            (start, end) = (end, start);
        }

        for (var current = start; current < end - 0.0001; current += safeStep)
        {
            passes.Add(current);
        }

        passes.Add(end);
        return passes.Distinct().ToList();
    }

    private static List<double> BuildDepthLevels(double startZ, double finalZ, double stepDown)
    {
        var safeStepDown = Math.Max(0.1, Math.Abs(stepDown));
        var levels = new List<double>();

        if (finalZ >= startZ)
        {
            levels.Add(finalZ);
            return levels;
        }

        var current = startZ;
        while ((current - safeStepDown) > finalZ)
        {
            current -= safeStepDown;
            levels.Add(current);
        }

        if (levels.Count == 0 || Math.Abs(levels[^1] - finalZ) > 0.0001)
        {
            levels.Add(finalZ);
        }

        return levels;
    }

    private static double GetToolRadius(ToolDefinition tool) => CutterGeometry.Radius(tool);

    private static double GetChamferTipRadius(ToolDefinition tool)
    {
        var majorDiameter = Math.Max(tool.CuttingDiameter, 0.5);
        return Math.Clamp(tool.TipDiameter, 0, majorDiameter) / 2d;
    }

    private static double GetMaxChamferWidth(ToolDefinition tool)
    {
        var maxWidth = GetToolRadius(tool) - GetChamferTipRadius(tool);
        return maxWidth > 0.0001 ? maxWidth : GetToolRadius(tool);
    }

    private static double GetChamferAxialPerRadial(ToolDefinition tool)
    {
        var includedAngle = tool.TipAngleDegrees > 0 ? tool.TipAngleDegrees : tool.TaperAngleDegrees;
        if (includedAngle <= 0 || includedAngle >= 179)
        {
            includedAngle = 90;
        }

        return 1d / Math.Tan(DegreesToRadians(includedAngle / 2d));
    }

    private static double GetConicalTipLength(ToolDefinition tool)
    {
        return CutterGeometry.ConicalTipLength(tool);
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

    private static void Rapid(List<ToolpathMove> moves, double x, double y, double z, string comment = "")
    {
        moves.Add(new ToolpathMove
        {
            Mode = MotionMode.Rapid,
            X = x,
            Y = y,
            Z = z,
            FeedRate = 0,
            IsCutting = false,
            Comment = comment
        });
    }

    private static void Line(List<ToolpathMove> moves, double x, double y, double z, double feedRate, bool isCutting, string comment = "")
    {
        moves.Add(new ToolpathMove
        {
            Mode = MotionMode.Linear,
            X = x,
            Y = y,
            Z = z,
            FeedRate = feedRate,
            IsCutting = isCutting,
            Comment = comment
        });
    }

    private static double Distance(ToolpathMove start, ToolpathMove end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var dz = end.Z - start.Z;
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private static string FormatValue(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed class RestStockMap
    {
        private const int Width = 220;
        private const int Height = 150;
        private readonly double[,] _heights = new double[Width, Height];

        public RestStockMap(Bounds2D bounds)
        {
            Bounds = bounds;
            CellSizeX = (Bounds.MaxX - Bounds.MinX) / Math.Max(Width - 1, 1);
            CellSizeY = (Bounds.MaxY - Bounds.MinY) / Math.Max(Height - 1, 1);
            CellSize = Math.Max(Math.Min(CellSizeX, CellSizeY), 0.05);
            for (var x = 0; x < Width; x++)
            {
                for (var y = 0; y < Height; y++)
                {
                    _heights[x, y] = Bounds.TopZ;
                }
            }
        }

        private Bounds2D Bounds { get; }

        private double CellSizeX { get; }

        private double CellSizeY { get; }

        public double CellSize { get; }

        public bool HasAppliedCuts { get; private set; }

        public bool CanRepresent(Bounds2D bounds)
        {
            var tolerance = Math.Max(CellSize * 2d, 0.25);
            return Math.Abs(bounds.MinX - Bounds.MinX) <= tolerance
                && Math.Abs(bounds.MaxX - Bounds.MaxX) <= tolerance
                && Math.Abs(bounds.MinY - Bounds.MinY) <= tolerance
                && Math.Abs(bounds.MaxY - Bounds.MaxY) <= tolerance
                && Math.Abs(bounds.TopZ - Bounds.TopZ) <= tolerance;
        }

        public bool HasMaterialAbove(double x, double y, double z)
        {
            if (!TryGetIndex(x, y, out var gridX, out var gridY))
            {
                return false;
            }

            return _heights[gridX, gridY] > z + 0.0001;
        }

        public IReadOnlyList<double> GetMaterialRowsAbove(double z)
        {
            var rows = new List<double>();
            for (var gridY = 0; gridY < Height; gridY++)
            {
                var hasMaterial = false;
                for (var gridX = 0; gridX < Width; gridX++)
                {
                    if (_heights[gridX, gridY] > z + 0.0001)
                    {
                        hasMaterial = true;
                        break;
                    }
                }

                if (hasMaterial)
                {
                    rows.Add(Bounds.MinY + (gridY * CellSizeY));
                }
            }

            return rows;
        }

        public void ApplyMoves(IReadOnlyList<ToolpathMove> moves, ToolDefinition tool)
        {
            var radius = CutterGeometry.Radius(tool);
            for (var index = 1; index < moves.Count; index++)
            {
                var previous = moves[index - 1];
                var current = moves[index];
                if (!current.IsCutting || current.Mode != MotionMode.Linear)
                {
                    continue;
                }

                HasAppliedCuts = true;
                RasterizeSegment(previous, current, tool, radius);
            }
        }

        private void RasterizeSegment(ToolpathMove start, ToolpathMove end, ToolDefinition tool, double radius)
        {
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var dz = end.Z - start.Z;
            var segmentLength = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            var samples = Math.Max(1, (int)Math.Ceiling(segmentLength / Math.Max(CellSize * 0.6, 0.4)));

            for (var sample = 0; sample <= samples; sample++)
            {
                var t = sample / (double)samples;
                var x = start.X + ((end.X - start.X) * t);
                var y = start.Y + ((end.Y - start.Y) * t);
                var z = start.Z + ((end.Z - start.Z) * t);
                var minX = Math.Max(0, (int)Math.Floor(((x - radius) - Bounds.MinX) / CellSizeX));
                var maxX = Math.Min(Width - 1, (int)Math.Ceiling(((x + radius) - Bounds.MinX) / CellSizeX));
                var minY = Math.Max(0, (int)Math.Floor(((y - radius) - Bounds.MinY) / CellSizeY));
                var maxY = Math.Min(Height - 1, (int)Math.Ceiling(((y + radius) - Bounds.MinY) / CellSizeY));

                for (var gridX = minX; gridX <= maxX; gridX++)
                {
                    for (var gridY = minY; gridY <= maxY; gridY++)
                    {
                        var worldX = Bounds.MinX + (gridX * CellSizeX);
                        var worldY = Bounds.MinY + (gridY * CellSizeY);
                        var distanceX = worldX - x;
                        var distanceY = worldY - y;
                        var radialDistance = Math.Sqrt((distanceX * distanceX) + (distanceY * distanceY));
                        if (radialDistance <= radius)
                        {
                            var cutHeight = z + CutterGeometry.BottomProfileHeightAtRadius(tool, radialDistance);
                            _heights[gridX, gridY] = Math.Min(_heights[gridX, gridY], cutHeight);
                        }
                    }
                }
            }
        }

        private bool TryGetIndex(double x, double y, out int gridX, out int gridY)
        {
            gridX = (int)Math.Round((x - Bounds.MinX) / CellSizeX);
            gridY = (int)Math.Round((y - Bounds.MinY) / CellSizeY);
            if (gridX < 0 || gridX >= Width || gridY < 0 || gridY >= Height)
            {
                return false;
            }

            return true;
        }
    }
}

public sealed class GrblPostProcessor
{
    public IReadOnlyList<string> BuildProgram(MachineProfile machine, CamJob job, IReadOnlyList<OperationToolpath> operations)
    {
        var lines = new List<string>
        {
            FormatComment("GRBL Cam generated program"),
            FormatComment($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}"),
            FormatComment($"Machine: {machine.Name} | Kinematics: {machine.Kinematics} | Controller: {machine.Controller}"),
            FormatComment($"Job: {job.Name}"),
            job.Units == UnitsMode.Inches ? "G20" : "G21",
            "G90",
            "G17",
            "G94",
            "M5"
        };

        var activeTool = -1;
        JobSetup? activeSetup = null;
        var spindleRunning = false;
        var emittedMotion = false;
        var lastFeed = double.NaN;

        foreach (var operation in operations)
        {
            if (!ReferenceEquals(activeSetup, operation.Setup))
            {
                if (activeSetup is not null)
                {
                    if (emittedMotion)
                    {
                        AddSafeRetract(lines, activeSetup, null);
                    }

                    if (spindleRunning)
                    {
                        lines.Add("M5");
                        spindleRunning = false;
                    }

                    lines.Add(string.Empty);
                    lines.Add($"M0 {FormatComment($"Re-fixture for {operation.Setup.Name}")}");
                    activeTool = -1;
                    lastFeed = double.NaN;
                }

                activeSetup = operation.Setup;
                lines.Add(string.Empty);
                lines.Add(FormatComment($"Setup: {operation.Setup.Name}"));
                lines.Add(FormatComment($"WCS: {NormalizeWorkOffsetCode(operation.Setup.WorkOffsetCode)} | Origin X{FormatValue(operation.Setup.WorkOrigin.X)} Y{FormatValue(operation.Setup.WorkOrigin.Y)} Z{FormatValue(operation.Setup.WorkOrigin.Z)}"));
                lines.Add(FormatComment($"Part rotation A{FormatValue(operation.Setup.Part.RotationA)} B{FormatValue(operation.Setup.Part.RotationB)}"));
                AddRotaryMappingComment(lines, machine);
                lines.Add(NormalizeWorkOffsetCode(operation.Setup.WorkOffsetCode));
                AddIndexedRotaryPosition(lines, machine, operation.Setup);
                if (operation.Setup.TransferRestFromPreviousSetup)
                {
                    lines.Add(FormatComment("REST machining state carries forward from previous setups"));
                }

                if (operation.Setup.FlipAxisX || operation.Setup.FlipAxisY || operation.Setup.FlipAxisZ)
                {
                    lines.Add(FormatComment($"Axis flip: X{FormatAxisSign(operation.Setup.FlipAxisX)} Y{FormatAxisSign(operation.Setup.FlipAxisY)} Z{FormatAxisSign(operation.Setup.FlipAxisZ)}"));
                }
            }

            lines.Add(string.Empty);
            lines.Add(FormatComment($"Operation: {operation.Operation.Name} | {operation.Operation.Type}"));
            lines.Add(FormatComment($"Tool {operation.Tool.Number}: {operation.Tool.Name} | {operation.Tool.Style} | D{FormatValue(operation.Tool.CuttingDiameter)}"));
            lines.Add(FormatComment($"RPM {FormatValue(operation.Operation.SpindleSpeed)} | Feed {FormatValue(operation.Operation.FeedRate)} | Plunge {FormatValue(operation.Operation.PlungeRate)} | StepDn {FormatValue(operation.Operation.StepDown)} | StepOv {FormatValue(operation.Operation.StepOver)}"));
            lines.Add(FormatComment($"Feature: {operation.Operation.Feature.Shape} | StartZ {FormatValue(operation.Operation.Feature.StartZ)} | Depth {FormatValue(operation.Operation.Feature.Depth)} | Dia {FormatValue(operation.Operation.Feature.Diameter)}"));
            if (HasOperationRotaryIndex(operation.Operation))
            {
                lines.Add(FormatComment($"Operation index offset A{FormatValue(operation.Operation.RotaryIndexA)} B{FormatValue(operation.Operation.RotaryIndexB)}"));
            }

            if (operation.Moves.Count == 0)
            {
                lines.Add(FormatComment(string.IsNullOrWhiteSpace(operation.Summary)
                    ? $"Skipped {operation.Operation.Name}: no motion generated"
                    : operation.Summary));
                continue;
            }

            if (HasOperationRotaryIndex(operation.Operation))
            {
                if (emittedMotion)
                {
                    AddSafeRetract(lines, operation.Setup, operation.Operation);
                }

                if (spindleRunning)
                {
                    lines.Add("M5");
                    spindleRunning = false;
                }

                AddIndexedRotaryPosition(lines, machine, operation.Setup, operation.Operation);
                lastFeed = double.NaN;
            }

            if (activeTool != operation.Tool.Number)
            {
                AddSafeRetract(lines, operation.Setup, operation.Operation);
                if (spindleRunning)
                {
                    lines.Add("M5");
                    spindleRunning = false;
                }

                if (machine.SupportsAutomaticToolChange)
                {
                    lines.Add($"T{operation.Tool.Number} M6");
                }
                else
                {
                    lines.Add(FormatComment($"Install tool {operation.Tool.Number}: {operation.Tool.Name}"));
                    lines.Add("M0");
                }

                activeTool = operation.Tool.Number;
                lastFeed = double.NaN;
            }

            AddSafeRetract(lines, operation.Setup, operation.Operation);
            lines.Add($"S{FormatValue(operation.Operation.SpindleSpeed)} M3");
            spindleRunning = true;

            foreach (var move in operation.Moves)
            {
                if (!string.IsNullOrWhiteSpace(move.Comment))
                {
                    lines.Add(FormatComment(move.Comment));
                }

                var command = move.Mode == MotionMode.Rapid ? "G0" : "G1";
                var feed = string.Empty;
                if (move.Mode == MotionMode.Linear)
                {
                    var feedRate = move.FeedRate > 0 ? move.FeedRate : operation.Operation.FeedRate;
                    if (!NearlyEqual(feedRate, lastFeed))
                    {
                        feed = $" F{FormatValue(feedRate)}";
                        lastFeed = feedRate;
                    }
                }

                var programX = ToProgramCoordinate(move.X, operation.Setup.WorkOrigin.X, operation.Setup.FlipAxisX);
                var programY = ToProgramCoordinate(move.Y, operation.Setup.WorkOrigin.Y, operation.Setup.FlipAxisY);
                var programZ = ToProgramCoordinate(move.Z, operation.Setup.WorkOrigin.Z, operation.Setup.FlipAxisZ);
                lines.Add($"{command} X{FormatValue(programX)} Y{FormatValue(programY)} Z{FormatValue(programZ)}{feed}");
                emittedMotion = true;
            }
        }

        if (activeSetup is not null && emittedMotion)
        {
            AddSafeRetract(lines, activeSetup, null);
        }

        if (spindleRunning)
        {
            lines.Add("M5");
        }

        lines.Add(string.Empty);
        lines.Add(FormatComment("Return to machine home using G53 machine coordinates"));
        lines.Add($"G53 G0 Z{FormatValue(machine.HomePoint.Z)}");
        lines.Add($"G53 G0 X{FormatValue(machine.HomePoint.X)} Y{FormatValue(machine.HomePoint.Y)}");
        AddMachineRotaryHome(lines, machine);
        lines.Add("M30");

        return lines;
    }

    private static void AddSafeRetract(List<string> lines, JobSetup setup, ToolpathOperationDefinition? operation)
    {
        var safeWorldZ = setup.WorkOffset.Z + Math.Max(setup.SafeZ, operation?.SafeRetractZ ?? setup.SafeZ);
        var programZ = ToProgramCoordinate(safeWorldZ, setup.WorkOrigin.Z, setup.FlipAxisZ);
        lines.Add($"G0 Z{FormatValue(programZ)}");
    }

    private static void AddIndexedRotaryPosition(List<string> lines, MachineProfile machine, JobSetup setup, ToolpathOperationDefinition? operation = null)
    {
        var hasRequestedIndex = HasSetupRotaryIndex(setup) || HasOperationRotaryIndex(operation);
        if (!SupportsIndexedRotary(machine))
        {
            if (hasRequestedIndex)
            {
                lines.Add(FormatComment("Rotary index is preview/setup-only because the selected machine is not indexed rotary capable."));
            }

            return;
        }

        var rotaryWords = BuildRotaryWords(machine, setup.Part, operation);
        if (rotaryWords.Count == 0)
        {
            if (hasRequestedIndex)
            {
                lines.Add(FormatComment("Setup rotation requested, but no A/B rotary axis is configured on the machine profile."));
            }

            return;
        }

        lines.Add(FormatComment(operation is null
            ? "Indexed rotary positioning for this setup"
            : "Indexed rotary positioning for this operation"));
        lines.Add($"G0 {string.Join(" ", rotaryWords)}");
    }

    private static void AddMachineRotaryHome(List<string> lines, MachineProfile machine)
    {
        var rotaryWords = machine.Axes
            .Where(axis => axis.Type == AxisType.Rotary && IsSupportedRotaryAxisName(axis.Name) && double.IsFinite(axis.HomePosition))
            .Select(axis => $"{NormalizeRotaryAxisName(axis.Name)}{FormatValue(axis.HomePosition)}")
            .ToList();
        if (rotaryWords.Count > 0)
        {
            lines.Add($"G53 G0 {string.Join(" ", rotaryWords)}");
        }
    }

    private static void AddRotaryMappingComment(List<string> lines, MachineProfile machine)
    {
        var mappings = machine.Axes
            .Where(axis => axis.Type == AxisType.Rotary && IsSupportedRotaryAxisName(axis.Name))
            .Select(axis => $"{NormalizeRotaryAxisName(axis.Name)} around {axis.RotatesAround}")
            .ToList();
        if (mappings.Count > 0)
        {
            lines.Add(FormatComment($"Rotary map: {string.Join(", ", mappings)}"));
        }
    }

    private static List<string> BuildRotaryWords(MachineProfile machine, PartDefinition part, ToolpathOperationDefinition? operation = null)
    {
        var words = new List<string>();
        foreach (var axis in machine.Axes.Where(axis => axis.Type == AxisType.Rotary))
        {
            var axisName = NormalizeRotaryAxisName(axis.Name);
            if (!IsSupportedRotaryAxisName(axisName))
            {
                continue;
            }

            var angle = axisName switch
            {
                "A" => part.RotationA + (operation?.RotaryIndexA ?? 0),
                "B" => part.RotationB + (operation?.RotaryIndexB ?? 0),
                _ => 0
            };

            words.Add($"{axisName}{FormatValue(angle)}");
            if (!AllowsMultipleIndexedRotaryAxes(machine))
            {
                break;
            }
        }

        return words;
    }

    private static bool SupportsIndexedRotary(MachineProfile machine)
    {
        return machine.Kinematics is KinematicsMode.FourAxisIndexedMill
            or KinematicsMode.ThreePlusOneMill
            or KinematicsMode.ThreePlusTwoMill
            or KinematicsMode.FiveAxisSimultaneousMill;
    }

    private static bool AllowsMultipleIndexedRotaryAxes(MachineProfile machine)
    {
        return machine.Kinematics is KinematicsMode.ThreePlusTwoMill or KinematicsMode.FiveAxisSimultaneousMill;
    }

    private static bool HasSetupRotaryIndex(JobSetup setup)
    {
        return Math.Abs(setup.Part.RotationA) > 0.0001
            || Math.Abs(setup.Part.RotationB) > 0.0001;
    }

    private static bool HasOperationRotaryIndex(ToolpathOperationDefinition? operation)
    {
        return operation is not null
            && (Math.Abs(operation.RotaryIndexA) > 0.0001
                || Math.Abs(operation.RotaryIndexB) > 0.0001);
    }

    private static bool IsSupportedRotaryAxisName(string? axisName)
    {
        var normalized = NormalizeRotaryAxisName(axisName);
        return normalized is "A" or "B";
    }

    private static string NormalizeRotaryAxisName(string? axisName)
    {
        return string.IsNullOrWhiteSpace(axisName)
            ? string.Empty
            : axisName.Trim().Substring(0, 1).ToUpperInvariant();
    }

    private static double ToProgramCoordinate(double worldValue, double originValue, bool flipAxis)
    {
        var localValue = worldValue - originValue;
        return flipAxis ? -localValue : localValue;
    }

    private static string NormalizeWorkOffsetCode(string? workOffsetCode)
    {
        if (string.IsNullOrWhiteSpace(workOffsetCode))
        {
            return "G54";
        }

        var normalized = workOffsetCode.Trim().ToUpperInvariant();
        return normalized is "G54" or "G55" or "G56" or "G57" or "G58" or "G59"
            ? normalized
            : "G54";
    }

    private static string FormatAxisSign(bool flipped) => flipped ? "-" : "+";

    private static string FormatComment(string comment)
    {
        var sanitized = comment
            .Replace('(', '[')
            .Replace(')', ']')
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        return $"({sanitized})";
    }

    private static bool NearlyEqual(double first, double second)
    {
        return double.IsFinite(first) && double.IsFinite(second) && Math.Abs(first - second) <= 0.0001;
    }

    private static string FormatValue(double value)
    {
        return (double.IsFinite(value) ? value : 0d).ToString("0.####", CultureInfo.InvariantCulture);
    }
}

public sealed class StockSimulationEngine
{
    private readonly PreviewGeometryLoader _geometryLoader = new();

    public SimulationReport Simulate(CamJob job, IReadOnlyList<OperationToolpath> toolpaths)
    {
        var bounds = GetSimulationBounds(job, toolpaths, _geometryLoader);
        const int width = 240;
        const int height = 160;
        var heights = new double[width, height];

        ResetHeights(heights, bounds.TopZ);

        JobSetup? activeSetup = null;
        foreach (var toolpath in toolpaths)
        {
            if (!ReferenceEquals(activeSetup, toolpath.Setup))
            {
                if (activeSetup is not null && !toolpath.Setup.TransferRestFromPreviousSetup)
                {
                    ResetHeights(heights, bounds.TopZ);
                }

                activeSetup = toolpath.Setup;
            }

            var radius = CutterGeometry.Radius(toolpath.Tool);
            for (var index = 1; index < toolpath.Moves.Count; index++)
            {
                var previous = toolpath.Moves[index - 1];
                var current = toolpath.Moves[index];

                if (!current.IsCutting || current.Mode != MotionMode.Linear)
                {
                    continue;
                }

                RasterizeSegment(previous, current, toolpath.Tool, radius, bounds, heights);
            }
        }

        var touchedCells = 0;
        var deepestZ = bounds.TopZ;
        var removedVolume = 0d;
        var stepX = (bounds.MaxX - bounds.MinX) / Math.Max(width - 1, 1);
        var stepY = (bounds.MaxY - bounds.MinY) / Math.Max(height - 1, 1);
        var cellArea = stepX * stepY;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var currentHeight = heights[x, y];
                if (currentHeight < bounds.TopZ - 0.0001)
                {
                    touchedCells++;
                    deepestZ = Math.Min(deepestZ, currentHeight);
                    removedVolume += (bounds.TopZ - currentHeight) * cellArea;
                }
            }
        }

        var pixelData = BuildPixelData(heights, bounds.TopZ, deepestZ);
        var summary = touchedCells == 0
            ? "Simulation ready. Generate toolpaths to preview stock removal."
            : $"Approx. removed volume: {removedVolume:0.##} mm^3 | Deepest Z: {deepestZ:0.###} | Cells touched: {touchedCells}";

        return new SimulationReport
        {
            Width = width,
            Height = height,
            PixelData = pixelData,
            RemovedVolume = removedVolume,
            DeepestZ = deepestZ,
            TouchedCells = touchedCells,
            Summary = summary
        };
    }

    private static void ResetHeights(double[,] heights, double topZ)
    {
        for (var x = 0; x < heights.GetLength(0); x++)
        {
            for (var y = 0; y < heights.GetLength(1); y++)
            {
                heights[x, y] = topZ;
            }
        }
    }

    private static byte[] BuildPixelData(double[,] heights, double topZ, double deepestZ)
    {
        var width = heights.GetLength(0);
        var height = heights.GetLength(1);
        var pixelData = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var currentHeight = heights[x, y];
                var normalized = deepestZ < topZ - 0.0001
                    ? (currentHeight - deepestZ) / (topZ - deepestZ)
                    : 1d;
                normalized = Math.Clamp(normalized, 0d, 1d);
                var blue = (byte)(120 + (90 * normalized));
                var green = (byte)(105 + (100 * normalized));
                var red = (byte)(55 + (165 * normalized));
                var offset = ((height - 1 - y) * width * 4) + (x * 4);
                pixelData[offset] = blue;
                pixelData[offset + 1] = green;
                pixelData[offset + 2] = red;
                pixelData[offset + 3] = 255;
            }
        }

        return pixelData;
    }

    private static void RasterizeSegment(
        ToolpathMove start,
        ToolpathMove end,
        ToolDefinition tool,
        double radius,
        Bounds2D bounds,
        double[,] heights)
    {
        var width = heights.GetLength(0);
        var height = heights.GetLength(1);
        var cellSizeX = (bounds.MaxX - bounds.MinX) / Math.Max(width - 1, 1);
        var cellSizeY = (bounds.MaxY - bounds.MinY) / Math.Max(height - 1, 1);
        var segmentLength = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2) + Math.Pow(end.Z - start.Z, 2));
        var steps = Math.Max(1, (int)Math.Ceiling(segmentLength / Math.Max(Math.Min(cellSizeX, cellSizeY), 0.5)));

        for (var step = 0; step <= steps; step++)
        {
            var t = step / (double)steps;
            var sampleX = Lerp(start.X, end.X, t);
            var sampleY = Lerp(start.Y, end.Y, t);
            var sampleZ = Lerp(start.Z, end.Z, t);

            var minGridX = Math.Max(0, (int)Math.Floor(((sampleX - radius) - bounds.MinX) / cellSizeX));
            var maxGridX = Math.Min(width - 1, (int)Math.Ceiling(((sampleX + radius) - bounds.MinX) / cellSizeX));
            var minGridY = Math.Max(0, (int)Math.Floor(((sampleY - radius) - bounds.MinY) / cellSizeY));
            var maxGridY = Math.Min(height - 1, (int)Math.Ceiling(((sampleY + radius) - bounds.MinY) / cellSizeY));

            for (var x = minGridX; x <= maxGridX; x++)
            {
                for (var y = minGridY; y <= maxGridY; y++)
                {
                    var worldX = bounds.MinX + (x * cellSizeX);
                    var worldY = bounds.MinY + (y * cellSizeY);
                    var dx = worldX - sampleX;
                    var dy = worldY - sampleY;
                    var radialDistance = Math.Sqrt((dx * dx) + (dy * dy));
                    if (radialDistance <= radius)
                    {
                        var cutHeight = sampleZ + CutterGeometry.BottomProfileHeightAtRadius(tool, radialDistance);
                        heights[x, y] = Math.Min(heights[x, y], cutHeight);
                    }
                }
            }
        }
    }

    private static double Lerp(double start, double end, double amount) => start + ((end - start) * amount);

    private static Bounds2D GetSimulationBounds(CamJob job, IReadOnlyList<OperationToolpath> toolpaths, PreviewGeometryLoader geometryLoader)
    {
        var setups = toolpaths
            .Select(toolpath => toolpath.Setup)
            .Where(setup => setup is not null)
            .Distinct()
            .ToList();
        if (setups.Count == 0)
        {
            setups = job.Setups.Count > 0 ? job.Setups.ToList() : new List<JobSetup> { job.Setup };
        }

        var bounds = StockBoundsResolver.ToToolpathBounds(
            setups[0],
            StockBoundsResolver.GetStockBounds(setups[0], geometryLoader));
        foreach (var setup in setups.Skip(1))
        {
            var next = StockBoundsResolver.ToToolpathBounds(
                setup,
                StockBoundsResolver.GetStockBounds(setup, geometryLoader));
            bounds = new Bounds2D(
                Math.Min(bounds.MinX, next.MinX),
                Math.Max(bounds.MaxX, next.MaxX),
                Math.Min(bounds.MinY, next.MinY),
                Math.Max(bounds.MaxY, next.MaxY),
                Math.Max(bounds.TopZ, next.TopZ));
        }

        return bounds;
    }
}
