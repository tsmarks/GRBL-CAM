using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GRBL_Cam.Models;

public enum MachineFamily
{
    Mill,
    Lathe
}

public enum KinematicsMode
{
    ThreeAxisMill,
    FourAxisIndexedMill,
    ThreePlusOneMill,
    ThreePlusTwoMill,
    FiveAxisSimultaneousMill,
    TwoAxisLathe,
    MillTurn
}

public enum AxisType
{
    Linear,
    Rotary
}

public enum RotaryAxisDirection
{
    X,
    Y
}

public enum RotaryAxisMount
{
    Table,
    Head
}

public enum ToolStyle
{
    Square,
    Ball,
    Bull,
    Lollipop,
    Taper,
    VPoint,
    VBit,
    Chamfer,
    Drill,
    SpotDrill,
    CenterDrill,
    FaceMill,
    RoughingEndMill,
    Dovetail,
    Keyseat,
    Reamer,
    Tap,
    Engraver,
    ThreadMill,
    SlittingSaw
}

public enum StockShape
{
    Box,
    Cylinder,
    ImportedSolid
}

public enum PartSourceType
{
    PrimitiveBox,
    PrimitiveCylinder,
    StepFile,
    ImportedSolid
}

public enum UnitsMode
{
    Millimeters,
    Inches
}

public enum OperationType
{
    Facing,
    BulkRemoval,
    Raster,
    AdaptiveClearing,
    Pocket,
    ZLevelFinishing,
    Parallel3DFinishing,
    ScallopFinishing,
    PencilCleanup,
    Profile,
    Contour2D,
    Chamfer,
    Boring,
    Drill
}

public enum FeatureShape
{
    Rectangle,
    Circle,
    HolePattern,
    EdgePath,
    ModelPlane
}

public enum SetupOriginSource
{
    Stock,
    Model
}

public enum SetupOriginAnchor
{
    PickedPoint,
    Center,
    TopCenter,
    BottomCenter,
    TopFrontLeft,
    TopFrontRight,
    TopBackLeft,
    TopBackRight,
    TopFrontMidpoint,
    TopBackMidpoint,
    TopLeftMidpoint,
    TopRightMidpoint,
    BottomFrontLeft,
    BottomFrontRight,
    BottomBackLeft,
    BottomBackRight
}

public sealed class CamApplicationState
{
    public string ApplicationVersion { get; set; } = "0.1.0";

    public ObservableCollection<MachineProfile> MachineProfiles { get; set; } = new();

    public ObservableCollection<ToolDefinition> ToolLibrary { get; set; } = new();

    public CamJob CurrentJob { get; set; } = new();
}

public sealed class MachineProfile
{
    public string Name { get; set; } = "New Machine";

    public MachineFamily Family { get; set; } = MachineFamily.Mill;

    public KinematicsMode Kinematics { get; set; } = KinematicsMode.ThreeAxisMill;

    public string Controller { get; set; } = "GRBL";

    public bool SupportsAutomaticToolChange { get; set; }

    public double SpindleMinRpm { get; set; } = 500;

    public double SpindleMaxRpm { get; set; } = 24000;

    public double MaxRapidRate { get; set; } = 5000;

    public Pose6D HomePoint { get; set; } = new();

    public ObservableCollection<MachineAxis> Axes { get; set; } = new();

    public string Notes { get; set; } = string.Empty;
}

public sealed class MachineAxis : INotifyPropertyChanged
{
    private string _name = "X";
    private AxisType _type = AxisType.Linear;
    private double _travelMin;
    private double _travelMax = 300;
    private double _maxFeedRate = 3000;
    private double _homePosition;
    private RotaryAxisDirection _rotatesAround = RotaryAxisDirection.X;
    private RotaryAxisMount _mount = RotaryAxisMount.Table;
    private double _pivotOffsetX;
    private double _pivotOffsetY;
    private double _pivotOffsetZ;
    private double _zeroOffset;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public AxisType Type
    {
        get => _type;
        set => SetField(ref _type, value);
    }

    public double TravelMin
    {
        get => _travelMin;
        set => SetField(ref _travelMin, value);
    }

    public double TravelMax
    {
        get => _travelMax;
        set => SetField(ref _travelMax, value);
    }

    public double MaxFeedRate
    {
        get => _maxFeedRate;
        set => SetField(ref _maxFeedRate, value);
    }

    public double HomePosition
    {
        get => _homePosition;
        set => SetField(ref _homePosition, value);
    }

    public RotaryAxisDirection RotatesAround
    {
        get => _rotatesAround;
        set => SetField(ref _rotatesAround, value);
    }

    public RotaryAxisMount Mount
    {
        get => _mount;
        set => SetField(ref _mount, value);
    }

    public double PivotOffsetX
    {
        get => _pivotOffsetX;
        set => SetField(ref _pivotOffsetX, value);
    }

    public double PivotOffsetY
    {
        get => _pivotOffsetY;
        set => SetField(ref _pivotOffsetY, value);
    }

    public double PivotOffsetZ
    {
        get => _pivotOffsetZ;
        set => SetField(ref _pivotOffsetZ, value);
    }

    public double ZeroOffset
    {
        get => _zeroOffset;
        set => SetField(ref _zeroOffset, value);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName ?? string.Empty));
        return true;
    }
}

public sealed class ToolDefinition
{
    public int Number { get; set; } = 1;

    public string Name { get; set; } = "Tool";

    public ToolStyle Style { get; set; } = ToolStyle.Square;

    public double CuttingDiameter { get; set; } = 6;

    public double CuttingLength { get; set; } = 20;

    public double FluteLength { get; set; } = 20;

    public double CornerRadius { get; set; }

    public double TipDiameter { get; set; }

    public double TipAngleDegrees { get; set; }

    public double TaperAngleDegrees { get; set; }

    public double NeckDiameter { get; set; }

    public double NeckLength { get; set; }

    public double StickOut { get; set; } = 35;

    public double OverallLength { get; set; } = 60;

    public double ShankDiameter { get; set; } = 6;

    public int FluteCount { get; set; } = 2;

    public double MaxStepDown { get; set; } = 2;

    public double MaxStepOver { get; set; } = 3;

    public string Notes { get; set; } = string.Empty;
}

public sealed class CamJob
{
    public string Name { get; set; } = "New Job";

    public UnitsMode Units { get; set; } = UnitsMode.Millimeters;

    public string MachineProfileName { get; set; } = string.Empty;

    public ObservableCollection<JobSetup> Setups { get; set; } = new();

    // Backward-compatible active setup aliases for older saved state and legacy bindings.
    public JobSetup Setup { get; set; } = new();

    public ObservableCollection<ToolpathOperationDefinition> Operations { get; set; } = new();
}

public sealed class JobSetup
{
    public string Name { get; set; } = "Setup";

    public bool TransferRestFromPreviousSetup { get; set; } = true;

    public PartDefinition Part { get; set; } = new();

    public StockDefinition Stock { get; set; } = new();

    public ObservableCollection<ToolpathOperationDefinition> Operations { get; set; } = new();

    public Pose6D WorkOrigin { get; set; } = new();

    public SetupOriginSource OriginSource { get; set; } = SetupOriginSource.Stock;

    public SetupOriginAnchor OriginAnchor { get; set; } = SetupOriginAnchor.TopCenter;

    public bool FlipAxisX { get; set; }

    public bool FlipAxisY { get; set; }

    public bool FlipAxisZ { get; set; }

    public Pose6D WorkOffset { get; set; } = new();

    public double AlignmentOffsetX { get; set; }

    public double AlignmentOffsetY { get; set; }

    public double AlignmentOffsetZ { get; set; }

    public double SafeZ { get; set; } = 15;

    public double ClearanceZ { get; set; } = 5;

    public string WorkOffsetCode { get; set; } = "G54";

    public string Notes { get; set; } = string.Empty;
}

public sealed class PartDefinition
{
    public string Name { get; set; } = "Imported Model";

    public PartSourceType SourceType { get; set; } = PartSourceType.StepFile;

    public string SourcePath { get; set; } = string.Empty;

    public double LengthX { get; set; } = 100;

    public double WidthY { get; set; } = 60;

    public double HeightZ { get; set; } = 18;

    public double Diameter { get; set; } = 50;

    public double RotationA { get; set; }

    public double RotationB { get; set; }

    public double RotationC { get; set; }
}

public sealed class StockDefinition
{
    public StockShape Shape { get; set; } = StockShape.Box;

    public bool ShowInPreview { get; set; } = true;

    public string ImportedSolidPath { get; set; } = string.Empty;

    public double LengthX { get; set; } = 140;

    public double WidthY { get; set; } = 90;

    public double HeightZ { get; set; } = 25;

    public double Diameter { get; set; } = 90;

    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    public double OffsetZ { get; set; }

    public double RotationA { get; set; }

    public double RotationB { get; set; }

    public double RotationC { get; set; }

    public double RadialAllowance { get; set; } = 2;

    public double AxialAllowance { get; set; } = 1;
}

public sealed class Pose6D
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }

    public double A { get; set; }

    public double B { get; set; }

    public double C { get; set; }
}

public sealed class ToolpathOperationDefinition : INotifyPropertyChanged
{
    private OperationType _type = OperationType.Facing;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; set; } = "Operation";

    public OperationType Type
    {
        get => _type;
        set
        {
            if (_type == value)
            {
                return;
            }

            _type = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Type)));
        }
    }

    public bool Enabled { get; set; } = true;

    public int ToolNumber { get; set; } = 1;

    public double SpindleSpeed { get; set; } = 12000;

    public double FeedRate { get; set; } = 1200;

    public double PlungeRate { get; set; } = 250;

    public double StepDown { get; set; } = 2;

    public double StepOver { get; set; } = 3;

    public int PathCount { get; set; } = 1;

    public double FinishStockRadial { get; set; }

    public double FinishStockAxial { get; set; }

    public bool ClimbMilling { get; set; } = true;

    public bool UseRestMachining { get; set; }

    public double SafeRetractZ { get; set; } = 5;

    public double LeadInLength { get; set; } = 2;

    public double LeadOutLength { get; set; } = 2;

    public double RotaryIndexA { get; set; }

    public double RotaryIndexB { get; set; }

    public double RotaryIndexC { get; set; }

    public double TabWidth { get; set; }

    public double TabHeight { get; set; }

    public double DrillPeckDepth { get; set; }

    public double DrillRetractDistance { get; set; } = 1;

    public bool DrillFullRetract { get; set; } = true;

    public FeatureDefinition Feature { get; set; } = new();

    public string Notes { get; set; } = string.Empty;
}

public sealed class FeatureDefinition
{
    public string Name { get; set; } = "Feature";

    public FeatureShape Shape { get; set; } = FeatureShape.Rectangle;

    public double CenterX { get; set; }

    public double CenterY { get; set; }

    public double StartZ { get; set; }

    public double Depth { get; set; } = 2;

    public double Length { get; set; } = 40;

    public double Width { get; set; } = 20;

    public double Diameter { get; set; } = 18;

    public bool InsideProfile { get; set; } = true;

    public int Rows { get; set; } = 1;

    public int Columns { get; set; } = 1;

    public double PitchX { get; set; } = 20;

    public double PitchY { get; set; } = 20;

    public double RotationDegrees { get; set; }

    public List<FeaturePathPoint> PathPoints { get; set; } = new();

    public List<FeaturePath> KeepoutLoops { get; set; } = new();
}

public sealed class FeaturePath
{
    public List<FeaturePathPoint> Points { get; set; } = new();
}

public sealed class FeaturePathPoint
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }
}
