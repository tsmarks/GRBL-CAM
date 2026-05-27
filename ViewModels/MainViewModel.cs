using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using GRBL_Cam.Models;
using GRBL_Cam.Services;

namespace GRBL_Cam.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly CamStateStore _stateStore;
    private readonly ThreeAxisToolpathPlanner _toolpathPlanner;
    private readonly GrblPostProcessor _postProcessor;
    private readonly StockSimulationEngine _simulationEngine;
    private readonly JobPreviewSceneBuilder _previewSceneBuilder;
    private readonly DispatcherTimer _playbackTimer;
    private JobPreviewSceneData? _previewSceneData;
    private IReadOnlyList<OperationToolpath> _previewToolpaths = Array.Empty<OperationToolpath>();
    private string _generatedGCode = string.Empty;
    private string _statusMessage = "Ready.";
    private string _projectSummary = string.Empty;
    private string _toolpathDiagnostics = "Toolpath diagnostics will appear after preview or program generation.";
    private string _simulationSummary = "Simulation ready.";
    private string _previewSummary = "3D preview ready.";
    private string _playbackStatus = "Toolpath playback ready.";
    private BitmapSource? _simulationBitmap;
    private Model3DGroup? _previewSceneModel;
    private Model3DGroup? _previewOverlaySceneModel;
    private MachineProfile? _selectedMachineProfile;
    private ToolDefinition? _selectedTool;
    private JobSetup? _selectedSetup;
    private ToolpathOperationDefinition? _selectedOperation;
    private ToolpathOperationDefinition? _hookedSelectedOperation;
    private MachineProfile? _hookedSelectedMachineProfile;
    private ObservableCollection<ToolpathOperationDefinition>? _hookedOperations;
    private double _toolpathPlaybackProgress;
    private double _playbackPosition;
    private double _playbackSpeed = 1d;
    private int _playbackSegmentCount;
    private bool _isPlaybackActive;
    private bool _isPlaybackRunning;
    private bool _isUpdatingPlaybackProgress;
    private bool _isPickingSetupOrigin;
    private bool _suppressPreviewRefresh;

    public MainViewModel()
    {
        _stateStore = new CamStateStore();
        _toolpathPlanner = new ThreeAxisToolpathPlanner();
        _postProcessor = new GrblPostProcessor();
        _simulationEngine = new StockSimulationEngine();
        _previewSceneBuilder = new JobPreviewSceneBuilder();
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _playbackTimer.Tick += (_, _) => AdvancePlayback(_playbackSpeed);
        State = _stateStore.LoadOrCreate();
        _selectedSetup = CurrentJob.Setups.FirstOrDefault() ?? CurrentJob.Setup;
        SyncActiveSetupAliases();

        AddMachineProfileCommand = new RelayCommand(AddMachineProfile);
        RemoveMachineProfileCommand = new RelayCommand(RemoveMachineProfile, () => SelectedMachineProfile is not null && MachineProfiles.Count > 1);
        ConfigureIndexedFourthAxisCommand = new RelayCommand(ConfigureIndexedFourthAxis, () => SelectedMachineProfile is not null);
        AddToolCommand = new RelayCommand(AddTool);
        RemoveToolCommand = new RelayCommand(RemoveTool, () => SelectedTool is not null && ToolLibrary.Count > 0);
        AddSetupCommand = new RelayCommand(AddSetup);
        RemoveSetupCommand = new RelayCommand(RemoveSetup, () => SelectedSetup is not null && Setups.Count > 1);
        ApplySetupOriginCommand = new RelayCommand(ApplySetupOriginPreset);
        PickSetupOriginCommand = new RelayCommand(StartPickingSetupOrigin);
        AddOperationCommand = new RelayCommand(AddOperation);
        RemoveOperationCommand = new RelayCommand(RemoveOperation, () => SelectedOperation is not null && ActiveOperations.Count > 0);
        RefreshPreviewCommand = new RelayCommand(RefreshPreview);
        GenerateProgramCommand = new RelayCommand(GenerateProgram);
        PlayToolpathCommand = new RelayCommand(PlayToolpath);
        PauseToolpathCommand = new RelayCommand(PauseToolpath);
        StepToolpathCommand = new RelayCommand(StepToolpath);
        StopToolpathCommand = new RelayCommand(StopToolpath);
        SaveStateCommand = new RelayCommand(SaveState);

        HookCollections();

        _suppressPreviewRefresh = true;
        SelectedMachineProfile = ResolveSelectedMachine();
        SelectedTool = ToolLibrary.FirstOrDefault();
        SelectedOperation = ActiveOperations.FirstOrDefault();
        _suppressPreviewRefresh = false;

        PreviewSummary = "Workspace loaded. Click Refresh Preview to load the saved 3D model.";
        StatusMessage = "Ready. Startup skipped saved preview/toolpath generation to keep the GUI responsive.";
        RefreshProjectSummary();
    }

    public CamApplicationState State { get; }

    public ObservableCollection<MachineProfile> MachineProfiles => State.MachineProfiles;

    public ObservableCollection<ToolDefinition> ToolLibrary => State.ToolLibrary;

    public CamJob CurrentJob => State.CurrentJob;

    public ObservableCollection<JobSetup> Setups => CurrentJob.Setups;

    public JobSetup CurrentSetup => SelectedSetup ?? CurrentJob.Setups.FirstOrDefault() ?? CurrentJob.Setup;

    public ObservableCollection<ToolpathOperationDefinition> ActiveOperations => CurrentSetup.Operations;

    public Array MachineFamilies => Enum.GetValues(typeof(MachineFamily));

    public Array KinematicsModes => Enum.GetValues(typeof(KinematicsMode));

    public Array AxisTypes => Enum.GetValues(typeof(AxisType));

    public Array RotaryAxisDirections => Enum.GetValues(typeof(RotaryAxisDirection));

    public Array RotaryAxisMounts => Enum.GetValues(typeof(RotaryAxisMount));

    public Array ToolStyles => Enum.GetValues(typeof(ToolStyle));

    public Array OperationTypes => Enum.GetValues(typeof(OperationType));

    public Array FeatureShapes => Enum.GetValues(typeof(FeatureShape));

    public Array StockShapes => Enum.GetValues(typeof(StockShape));

    public Array PartSourceTypes => Enum.GetValues(typeof(PartSourceType));

    public Array UnitModes => Enum.GetValues(typeof(UnitsMode));

    public Array SetupOriginSources => Enum.GetValues(typeof(SetupOriginSource));

    public Array SetupOriginAnchors => Enum.GetValues(typeof(SetupOriginAnchor));

    public MachineProfile? SelectedMachineProfile
    {
        get => _selectedMachineProfile;
        set
        {
            if (SetProperty(ref _selectedMachineProfile, value))
            {
                if (value is not null)
                {
                    CurrentJob.MachineProfileName = value.Name;
                    NormalizeMachineRotaryAxesForKinematics(value);
                }

                HookSelectedMachineProfileNotifications(value);
                ClearUnsupportedRotaryIndexes();
                NotifyRotaryUiProperties();
                RefreshProjectSummary();
                RaiseCommandStates();
            }
        }
    }

    public ToolDefinition? SelectedTool
    {
        get => _selectedTool;
        set
        {
            if (SetProperty(ref _selectedTool, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public JobSetup? SelectedSetup
    {
        get => _selectedSetup;
        set
        {
            if (SetProperty(ref _selectedSetup, value))
            {
                SyncActiveSetupAliases();
                HookActiveOperationsCollection();
                OnPropertyChanged(nameof(CurrentSetup));
                OnPropertyChanged(nameof(ActiveOperations));
                SelectedOperation = ActiveOperations.FirstOrDefault();
                RefreshProjectSummary();
                RaiseCommandStates();
                if (!_suppressPreviewRefresh)
                {
                    RefreshPreview();
                }
            }
        }
    }

    public ToolpathOperationDefinition? SelectedOperation
    {
        get => _selectedOperation;
        set
        {
            if (SetProperty(ref _selectedOperation, value))
            {
                HookSelectedOperationNotifications(value);
                NotifyOperationSpecificUiProperties();
                RaiseCommandStates();
                if (!_suppressPreviewRefresh)
                {
                    RefreshPreview();
                }
            }
        }
    }

    public bool IsPickingSetupOrigin
    {
        get => _isPickingSetupOrigin;
        private set => SetProperty(ref _isPickingSetupOrigin, value);
    }

    public string GeneratedGCode
    {
        get => _generatedGCode;
        private set => SetProperty(ref _generatedGCode, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ProjectSummary
    {
        get => _projectSummary;
        private set => SetProperty(ref _projectSummary, value);
    }

    public string ToolpathDiagnostics
    {
        get => _toolpathDiagnostics;
        private set => SetProperty(ref _toolpathDiagnostics, value);
    }

    public string SimulationSummary
    {
        get => _simulationSummary;
        private set => SetProperty(ref _simulationSummary, value);
    }

    public string PreviewSummary
    {
        get => _previewSummary;
        private set => SetProperty(ref _previewSummary, value);
    }

    public string PlaybackStatus
    {
        get => _playbackStatus;
        private set => SetProperty(ref _playbackStatus, value);
    }

    public double ToolpathPlaybackProgress
    {
        get => _toolpathPlaybackProgress;
        set
        {
            if (!SetProperty(ref _toolpathPlaybackProgress, Math.Clamp(value, 0, 100)))
            {
                return;
            }

            if (!_isUpdatingPlaybackProgress)
            {
                SeekPlayback(_toolpathPlaybackProgress);
            }
        }
    }

    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        set => SetProperty(ref _playbackSpeed, Math.Clamp(value, 0.1, 25d));
    }

    public bool IsPlaybackActive
    {
        get => _isPlaybackActive;
        private set => SetProperty(ref _isPlaybackActive, value);
    }

    public bool IsPlaybackRunning
    {
        get => _isPlaybackRunning;
        private set => SetProperty(ref _isPlaybackRunning, value);
    }

    public BitmapSource? SimulationBitmap
    {
        get => _simulationBitmap;
        private set => SetProperty(ref _simulationBitmap, value);
    }

    public Model3DGroup? PreviewSceneModel
    {
        get => _previewSceneModel;
        private set => SetProperty(ref _previewSceneModel, value);
    }

    public Model3DGroup? PreviewOverlaySceneModel
    {
        get => _previewOverlaySceneModel;
        private set => SetProperty(ref _previewOverlaySceneModel, value);
    }

    public Rect3D PreviewBounds => _previewSceneData?.Bounds ?? Rect3D.Empty;

    public Visibility OperationStepSettingsVisibility => SelectedOperation?.Type == OperationType.Drill ? Visibility.Collapsed : Visibility.Visible;

    public Visibility FinishStockSettingsVisibility => SelectedOperation?.Type == OperationType.Drill ? Visibility.Collapsed : Visibility.Visible;

    public Visibility LeadSettingsVisibility => IsSelectedOperationType(
        OperationType.Profile,
        OperationType.Contour2D,
        OperationType.Chamfer,
        OperationType.ZLevelFinishing) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TabSettingsVisibility => IsSelectedOperationType(
        OperationType.Profile,
        OperationType.Contour2D) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ClimbToggleVisibility => IsSelectedOperationType(
        OperationType.Pocket,
        OperationType.Profile,
        OperationType.Contour2D,
        OperationType.Chamfer,
        OperationType.Boring,
        OperationType.ZLevelFinishing) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RestToggleVisibility => IsSelectedOperationType(
        OperationType.BulkRemoval,
        OperationType.Raster,
        OperationType.AdaptiveClearing) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FeatureSizeVisibility => IsSelectedOperationType(
        OperationType.Facing,
        OperationType.BulkRemoval,
        OperationType.Raster,
        OperationType.AdaptiveClearing,
        OperationType.Pocket) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FeatureDiameterVisibility => IsSelectedOperationType(
        OperationType.Drill,
        OperationType.Boring,
        OperationType.Pocket,
        OperationType.Profile,
        OperationType.Contour2D,
        OperationType.Chamfer) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FeatureDiameterSideVisibility =>
        FeatureDiameterVisibility == Visibility.Visible || InsideProfileVisibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility InsideProfileVisibility => IsSelectedOperationType(
        OperationType.Pocket,
        OperationType.Profile,
        OperationType.Contour2D,
        OperationType.Chamfer,
        OperationType.PencilCleanup) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DrillSettingsVisibility => IsSelectedOperationType(OperationType.Drill) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PatternSettingsVisibility => IsSelectedOperationType(OperationType.Drill, OperationType.Boring) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RotationSettingsVisibility => IsSelectedOperationType(
        OperationType.Raster,
        OperationType.AdaptiveClearing,
        OperationType.Parallel3DFinishing,
        OperationType.ScallopFinishing) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IndexedRotarySettingsVisibility => SelectedMachineProfile is not null && GetExposedRotaryAxisNames(SelectedMachineProfile).Any()
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility RotaryAIndexVisibility => IsRotaryAxisExposed("A")
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility RotaryBIndexVisibility => IsRotaryAxisExposed("B")
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string RotaryIndexLabel
    {
        get
        {
            var axes = GetExposedRotaryAxisNames(SelectedMachineProfile)
                .Select(FormatRotaryAxisLabel)
                .ToList();
            return axes.Count == 0
                ? "Index"
                : $"Index {string.Join(" / ", axes)}";
        }
    }

    public ICommand AddMachineProfileCommand { get; }

    public ICommand RemoveMachineProfileCommand { get; }

    public ICommand ConfigureIndexedFourthAxisCommand { get; }

    public ICommand AddToolCommand { get; }

    public ICommand RemoveToolCommand { get; }

    public ICommand AddSetupCommand { get; }

    public ICommand RemoveSetupCommand { get; }

    public ICommand ApplySetupOriginCommand { get; }

    public ICommand PickSetupOriginCommand { get; }

    public ICommand AddOperationCommand { get; }

    public ICommand RemoveOperationCommand { get; }

    public ICommand RefreshPreviewCommand { get; }

    public ICommand GenerateProgramCommand { get; }

    public ICommand PlayToolpathCommand { get; }

    public ICommand PauseToolpathCommand { get; }

    public ICommand StepToolpathCommand { get; }

    public ICommand StopToolpathCommand { get; }

    public ICommand SaveStateCommand { get; }

    public void SaveState()
    {
        SyncActiveSetupAliases();
        _stateStore.Save(State);
        StatusMessage = $"Saved CAM workspace to {_stateStore.StateFilePath}.";
        RefreshProjectSummary();
    }

    public void ExportGCodeTo(string path)
    {
        if (string.IsNullOrWhiteSpace(GeneratedGCode))
        {
            GenerateProgram();
        }

        File.WriteAllText(path, GeneratedGCode);
        StatusMessage = $"Exported GRBL program to {path}.";
    }

    public void SetPartSourcePath(string path)
    {
        var modelChanged = !string.Equals(CurrentSetup.Part.SourcePath, path, StringComparison.OrdinalIgnoreCase);
        CurrentSetup.Part.SourceType = ResolvePartSourceType(path);
        CurrentSetup.Part.SourcePath = path;
        CurrentSetup.Part.Name = Path.GetFileNameWithoutExtension(path);

        if (modelChanged)
        {
            ClearModelPreviewState();
        }

        StatusMessage = modelChanged
            ? $"Selected model source: {Path.GetFileName(path)}. Cleared old feature/toolpath preview state for the new model."
            : $"Selected model source: {Path.GetFileName(path)}";
        RefreshPreview(rebuildToolpaths: !modelChanged);
        RefreshProjectSummary();
    }

    public void SetStockSolidPath(string path)
    {
        CurrentSetup.Stock.Shape = StockShape.ImportedSolid;
        CurrentSetup.Stock.ImportedSolidPath = path;
        StatusMessage = $"Selected stock solid: {Path.GetFileName(path)}";
        RefreshPreview();
        RefreshProjectSummary();
    }

    public void RefreshPreview()
    {
        RefreshPreview(rebuildToolpaths: true);
    }

    private void RefreshPreview(bool rebuildToolpaths)
    {
        SyncActiveSetupAliases();
        CurrentSetup.Part.SourceType = ResolvePartSourceType(CurrentSetup.Part.SourcePath, CurrentSetup.Part.SourceType);
        if (rebuildToolpaths)
        {
            _previewToolpaths = BuildPreviewToolpaths();
        }

        if (IsPlaybackActive)
        {
            SyncPlaybackSegmentCount();
        }

        var playback = IsPlaybackActive
            ? new ToolpathPlaybackSnapshot
            {
                IsActive = true,
                SegmentPosition = _playbackPosition
            }
            : null;
        _previewSceneData = _previewSceneBuilder.Build(CurrentJob, SelectedMachineProfile, SelectedOperation, _previewToolpaths, playback);
        PreviewSceneModel = _previewSceneData.Scene;
        PreviewOverlaySceneModel = _previewSceneData.OverlayScene;
        PreviewSummary = _previewSceneData.Summary;
        OnPropertyChanged(nameof(PreviewBounds));
        ToolpathDiagnostics = BuildToolpathDiagnostics(_previewToolpaths);
        RefreshProjectSummary(_previewToolpaths);
    }

    public void NotifyPreviewInputsChanged()
    {
        if (SelectedMachineProfile is not null)
        {
            NormalizeMachineRotaryAxesForKinematics(SelectedMachineProfile);
        }

        ClearUnsupportedRotaryIndexes();
        NotifyRotaryUiProperties();
        RefreshPreview();
        RefreshProjectSummary();
    }

    private void ClearModelPreviewState()
    {
        _playbackTimer.Stop();
        _previewToolpaths = Array.Empty<OperationToolpath>();
        _playbackPosition = 0;
        _playbackSegmentCount = 0;
        IsPlaybackActive = false;
        IsPlaybackRunning = false;
        UpdatePlaybackProgress();
        PlaybackStatus = "Toolpath playback reset for the new model.";
        SimulationSummary = "Simulation reset for the new model.";
        ToolpathDiagnostics = "Toolpath diagnostics reset for the new model. Click Refresh Preview after importing or rotating the part.";
        SimulationBitmap = null;

        if (_selectedOperation is not null)
        {
            _selectedOperation = null;
            OnPropertyChanged(nameof(SelectedOperation));
        }

        RaiseCommandStates();
    }

    public bool TrySelectOperationFromPreview(Model3D? model)
    {
        if (model is null || _previewSceneData is null)
        {
            return false;
        }

        if (!_previewSceneData.SelectableModels.TryGetValue(model, out var operation))
        {
            return false;
        }

        if (!ReferenceEquals(SelectedOperation, operation))
        {
            SelectedOperation = operation;
        }

        return true;
    }

    public bool TrySelectSetupOriginFromPreview(Model3D? model, Point3D point)
    {
        if (!IsPickingSetupOrigin || model is null || _previewSceneData is null)
        {
            return false;
        }

        if (!_previewSceneData.OriginSelectableModels.TryGetValue(model, out var source))
        {
            return false;
        }

        CurrentSetup.OriginSource = source;
        CurrentSetup.OriginAnchor = SetupOriginAnchor.PickedPoint;
        SetCurrentSetupOrigin(point);
        IsPickingSetupOrigin = false;
        RefreshPreview();
        StatusMessage =
            $"Set {CurrentSetup.Name} origin from {source} pick at X{point.X:0.###}, Y{point.Y:0.###}, Z{point.Z:0.###}.";
        return true;
    }

    public bool TrySelectFeatureFromPreview(Model3D? model, Point3D point)
    {
        if (model is null || _previewSceneData is null || SelectedOperation is null)
        {
            return false;
        }

        if (!_previewSceneData.FeatureSelectableModels.Contains(model))
        {
            return false;
        }

        if (SelectedOperation.Type is OperationType.Profile or OperationType.Contour2D or OperationType.Chamfer)
        {
            if (_previewSceneData.EdgeSelectableModels.TryGetValue(model, out var edgeGeometry))
            {
                return TryApplyProfileEdgeSelection(edgeGeometry, point);
            }

            StatusMessage = $"{SelectedOperation.Type} operations are model-edge aware now. Click a visible model edge to choose the path.";
            return false;
        }

        if (SelectedOperation.Type == OperationType.PencilCleanup
            && _previewSceneData.EdgeSelectableModels.TryGetValue(model, out var pencilEdgeGeometry))
        {
            return TryApplyProfileEdgeSelection(pencilEdgeGeometry, point);
        }

        if (SelectedOperation.Type is OperationType.BulkRemoval or OperationType.Raster or OperationType.AdaptiveClearing or OperationType.ZLevelFinishing or OperationType.Parallel3DFinishing or OperationType.ScallopFinishing or OperationType.PencilCleanup)
        {
            return TryApplyBulkRemovalPlaneSelection(point);
        }

        if (SelectedOperation.Type is OperationType.Drill or OperationType.Boring)
        {
            _previewSceneData.EdgeSelectableModels.TryGetValue(model, out var edgeGeometry);
            if (TryApplyDrillEdgeSelection(edgeGeometry, point))
            {
                return true;
            }
        }

        var feature = SelectedOperation.Feature;
        feature.CenterX = point.X - CurrentSetup.WorkOffset.X - CurrentSetup.AlignmentOffsetX;
        feature.CenterY = point.Y - CurrentSetup.WorkOffset.Y - CurrentSetup.AlignmentOffsetY;
        feature.StartZ = SnapNearZero(point.Z - _previewSceneData.StockTopZ);

        if (SelectedOperation.Type is (OperationType.Drill or OperationType.Boring) && feature.Shape != FeatureShape.HolePattern)
        {
            feature.Shape = FeatureShape.Circle;
        }
        else if ((SelectedOperation.Type == OperationType.Pocket || SelectedOperation.Type == OperationType.Profile || SelectedOperation.Type == OperationType.Contour2D || SelectedOperation.Type == OperationType.Chamfer)
            && feature.Shape == FeatureShape.HolePattern)
        {
            feature.Shape = FeatureShape.Circle;
        }

        NotifySelectedFeatureChanged();
        RefreshPreview();
        StatusMessage =
            $"Placed {SelectedOperation.Name} feature at X{feature.CenterX:0.###}, Y{feature.CenterY:0.###}, Start Z{feature.StartZ:0.###}. Picked Z{point.Z:0.###}; stock top Z{_previewSceneData.StockTopZ:0.###}.";
        return true;
    }

    private bool TryApplyDrillEdgeSelection(PreviewEdgeGeometry? edgeGeometry, Point3D hitPoint)
    {
        if (SelectedOperation is null || _previewSceneData is null)
        {
            return false;
        }

        var allCircles = BuildDrillCircleEdges(_previewSceneData.EdgeGeometries, _previewSceneData.PartBounds);
        if (allCircles.Count == 0)
        {
            StatusMessage = SelectedOperation.Type == OperationType.Boring
                ? "No circular STEP edges were found for model-aware boring."
                : "No circular STEP edges were found for model-aware drilling.";
            return false;
        }

        if (edgeGeometry is null
            || !TryGetDrillCircle(edgeGeometry, GetEdgeTolerance(_previewSceneData.EdgeGeometries), _previewSceneData.PartBounds, out var selectedCircle))
        {
            selectedCircle = allCircles
                .OrderBy(circle => DistanceToDrillCircle(hitPoint, circle))
                .First();
        }

        var matchingCircles = allCircles
            .Where(circle => IsMatchingDrillCircle(selectedCircle, circle))
            .OrderByDescending(circle => SignedDistanceAlongNormal(selectedCircle, circle.Center))
            .ToList();
        var entryCircle = selectedCircle;
        var exitCircle = matchingCircles
            .Where(circle => DistanceSquared3D(circle.Center, entryCircle.Center) > Math.Pow(Math.Max(entryCircle.Radius * 0.02, 0.01), 2))
            .Where(circle => SignedDistanceAlongNormal(entryCircle, circle.Center) < -Math.Max(entryCircle.Radius * 0.02, 0.01))
            .OrderBy(circle => SignedDistanceAlongNormal(selectedCircle, circle.Center))
            .FirstOrDefault();
        var drillAxis = ResolveDrillAxis(entryCircle, exitCircle);
        var entryAxis = entryCircle.Normal.LengthSquared > 0.000001 ? entryCircle.Normal : drillAxis;
        var rotaryMessage = ApplyDrillRotaryAutoIndex(entryAxis, out var visualDeltaX, out var visualDeltaY);
        var alignedEntryCenter = TransformByVisualRotaryDelta(entryCircle.Center, visualDeltaX, visualDeltaY);
        var alignedExitCenter = exitCircle.Source is null
            ? (Point3D?)null
            : TransformByVisualRotaryDelta(exitCircle.Center, visualDeltaX, visualDeltaY);

        var inferredDepth = exitCircle.Source is null
            ? SelectedOperation.Feature.Depth
            : alignedExitCenter.HasValue
                ? Math.Abs(alignedEntryCenter.Z - alignedExitCenter.Value.Z)
                : Math.Abs(SignedDistanceAlongNormal(entryCircle, exitCircle.Center));

        var feature = SelectedOperation.Feature;
        feature.Shape = FeatureShape.Circle;
        feature.Name = SelectedOperation.Type == OperationType.Boring
            ? exitCircle.Source is null ? "Selected Model Bore" : "Model Bore"
            : exitCircle.Source is null ? "Selected Model Hole" : "Model Hole";
        feature.CenterX = alignedEntryCenter.X - CurrentSetup.WorkOffset.X - CurrentSetup.AlignmentOffsetX;
        feature.CenterY = alignedEntryCenter.Y - CurrentSetup.WorkOffset.Y - CurrentSetup.AlignmentOffsetY;
        feature.StartZ = SnapNearZero(alignedEntryCenter.Z - _previewSceneData.StockTopZ);
        feature.Diameter = entryCircle.Radius * 2d;
        feature.Depth = inferredDepth;
        feature.Rows = 1;
        feature.Columns = 1;

        NotifySelectedFeatureChanged();
        RefreshPreview();
        var operationName = SelectedOperation.Type == OperationType.Boring ? "boring" : "drill";
        StatusMessage = exitCircle.Source is null
            ? $"Centered {operationName} on model circle at X{feature.CenterX:0.###}, Y{feature.CenterY:0.###}. {rotaryMessage} No matching lower edge was found, so depth remains {feature.Depth:0.###}."
            : $"Centered {operationName} on model hole at X{feature.CenterX:0.###}, Y{feature.CenterY:0.###}. {rotaryMessage} Diameter {feature.Diameter:0.###}, depth {feature.Depth:0.###}.";
        return true;
    }

    private static Vector3D ResolveDrillAxis(DrillCircleEdge topCircle, DrillCircleEdge bottomCircle)
    {
        if (bottomCircle.Source is not null)
        {
            var axis = topCircle.Center - bottomCircle.Center;
            if (axis.LengthSquared > 0.000001)
            {
                axis.Normalize();
                if (Vector3D.DotProduct(axis, topCircle.Normal) < 0)
                {
                    axis = -axis;
                }

                return axis;
            }
        }

        return topCircle.Normal;
    }

    private string ApplyDrillRotaryAutoIndex(Vector3D drillAxis, out double visualDeltaX, out double visualDeltaY)
    {
        visualDeltaX = 0;
        visualDeltaY = 0;
        if (SelectedOperation is null)
        {
            return string.Empty;
        }

        var normal = drillAxis;
        if (normal.LengthSquared < 0.000001)
        {
            return "No reliable circle normal was available for rotary auto-index.";
        }

        normal.Normalize();
        if (Vector3D.DotProduct(normal, new Vector3D(0, 0, 1)) >= 0.999)
        {
            return "No rotary index needed.";
        }

        var machine = SelectedMachineProfile ?? ResolveSelectedMachine();
        if (machine is null || !SupportsIndexedRotary(machine))
        {
            return "No indexed A/B machine is selected, so rotary index was not changed.";
        }

        if (!TryResolveBestDrillRotaryAlignment(machine, normal, out var alignment))
        {
            return "No configured A/B rotary axis can align this circle normal with the spindle.";
        }

        var previousA = SelectedOperation.RotaryIndexA;
        var previousB = SelectedOperation.RotaryIndexB;
        SelectedOperation.RotaryIndexA = NormalizeAngleDegrees(previousA + alignment.DeltaA);
        SelectedOperation.RotaryIndexB = NormalizeAngleDegrees(previousB + alignment.DeltaB);
        visualDeltaX = alignment.VisualDeltaX;
        visualDeltaY = alignment.VisualDeltaY;
        OnPropertyChanged($"{nameof(SelectedOperation)}.{nameof(ToolpathOperationDefinition.RotaryIndexA)}");
        OnPropertyChanged($"{nameof(SelectedOperation)}.{nameof(ToolpathOperationDefinition.RotaryIndexB)}");

        return Math.Abs(alignment.DeltaA) < 0.0001 && Math.Abs(alignment.DeltaB) < 0.0001
            ? "No rotary index needed."
            : $"Auto-indexed {FormatRotaryTargetDescription(previousA, previousB, SelectedOperation.RotaryIndexA, SelectedOperation.RotaryIndexB)}.";
    }

    private static string FormatRotaryTargetDescription(double previousA, double previousB, double targetA, double targetB)
    {
        var parts = new List<string>();
        if (Math.Abs(targetA - previousA) > 0.0001)
        {
            parts.Add($"A{previousA:0.###}->{targetA:0.###}");
        }

        if (Math.Abs(targetB - previousB) > 0.0001)
        {
            parts.Add($"B{previousB:0.###}->{targetB:0.###}");
        }

        return parts.Count == 0 ? "A/B unchanged" : string.Join(", ", parts);
    }

    private static bool TryResolveBestDrillRotaryAlignment(
        MachineProfile machine,
        Vector3D normal,
        out RotaryAlignmentSolution alignment)
    {
        alignment = default;
        var candidates = new List<RotaryAlignmentSolution>();
        if (TryResolveDrillRotaryAlignment(machine, normal, out var directAlignment))
        {
            candidates.Add(directAlignment);
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        alignment = candidates
            .OrderBy(candidate => candidate.Residual)
            .ThenBy(candidate => Math.Abs(candidate.DeltaA) + Math.Abs(candidate.DeltaB))
            .First();
        return true;
    }

    private static bool TryResolveDrillRotaryAlignment(
        MachineProfile machine,
        Vector3D normal,
        out RotaryAlignmentSolution alignment)
    {
        alignment = default;
        var exposedAxes = GetExposedRotaryAxisNames(machine).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rotaryAxes = machine.Axes
            .Where(axis => axis.Type == AxisType.Rotary && IsSupportedRotaryAxisName(axis.Name))
            .Select(axis => new
            {
                Axis = axis,
                Name = NormalizeRotaryAxisName(axis.Name)
            })
            .Where(axis => !string.IsNullOrWhiteSpace(axis.Name) && exposedAxes.Contains(axis.Name))
            .ToList();
        if (rotaryAxes.Count == 0)
        {
            return false;
        }

        var singleAxisCandidates = rotaryAxes
            .Select(axis => BuildSingleAxisAlignmentCandidate(axis.Name, axis.Axis.RotatesAround, normal))
            .OrderBy(candidate => candidate.Residual)
            .ThenBy(candidate => Math.Abs(candidate.DeltaA) + Math.Abs(candidate.DeltaB))
            .ToList();
        var bestSingleAxis = singleAxisCandidates.FirstOrDefault();
        if (bestSingleAxis.IsValid && bestSingleAxis.Residual <= 0.035)
        {
            alignment = bestSingleAxis;
            return true;
        }

        if (machine.Kinematics is not (KinematicsMode.ThreePlusTwoMill or KinematicsMode.FiveAxisSimultaneousMill))
        {
            return false;
        }

        var axisForX = rotaryAxes.FirstOrDefault(axis => axis.Axis.RotatesAround == RotaryAxisDirection.X);
        var axisForY = rotaryAxes.FirstOrDefault(axis => axis.Axis.RotatesAround == RotaryAxisDirection.Y);
        if (axisForX is null || axisForY is null)
        {
            return false;
        }

        var visualX = NormalizeAngleDegrees(RadiansToDegrees(Math.Atan2(normal.Y, normal.Z)));
        var afterX = RotateVectorByVisualDelta(normal, visualX, 0);
        var visualY = NormalizeAngleDegrees(RadiansToDegrees(Math.Atan2(-afterX.X, afterX.Z)));
        var alignedNormal = RotateVectorByVisualDelta(normal, visualX, visualY);
        alignedNormal.Normalize();
        var residual = Math.Sqrt((alignedNormal.X * alignedNormal.X) + (alignedNormal.Y * alignedNormal.Y));
        if (residual > 0.035 || alignedNormal.Z < 0.9)
        {
            return false;
        }

        var deltaA = 0d;
        var deltaB = 0d;
        AssignAxisDelta(axisForX.Name, visualX, ref deltaA, ref deltaB);
        AssignAxisDelta(axisForY.Name, visualY, ref deltaA, ref deltaB);
        alignment = new RotaryAlignmentSolution(
            true,
            deltaA,
            deltaB,
            visualX,
            visualY,
            residual,
            FormatRotaryAlignmentDescription(deltaA, deltaB));
        return true;
    }

    private static RotaryAlignmentSolution BuildSingleAxisAlignmentCandidate(string axisName, RotaryAxisDirection direction, Vector3D normal)
    {
        var angle = direction == RotaryAxisDirection.Y
            ? NormalizeAngleDegrees(RadiansToDegrees(Math.Atan2(-normal.X, normal.Z)))
            : NormalizeAngleDegrees(RadiansToDegrees(Math.Atan2(normal.Y, normal.Z)));
        var visualX = direction == RotaryAxisDirection.X ? angle : 0;
        var visualY = direction == RotaryAxisDirection.Y ? angle : 0;
        var alignedNormal = RotateVectorByVisualDelta(normal, visualX, visualY);
        if (alignedNormal.LengthSquared > 0.000001)
        {
            alignedNormal.Normalize();
        }

        var residual = Math.Sqrt((alignedNormal.X * alignedNormal.X) + (alignedNormal.Y * alignedNormal.Y));
        var deltaA = 0d;
        var deltaB = 0d;
        AssignAxisDelta(axisName, angle, ref deltaA, ref deltaB);
        return new RotaryAlignmentSolution(
            true,
            deltaA,
            deltaB,
            visualX,
            visualY,
            residual,
            FormatRotaryAlignmentDescription(deltaA, deltaB));
    }

    private static void AssignAxisDelta(string axisName, double angle, ref double deltaA, ref double deltaB)
    {
        if (axisName == "A")
        {
            deltaA = angle;
        }
        else if (axisName == "B")
        {
            deltaB = angle;
        }
    }

    private static string FormatRotaryAlignmentDescription(double deltaA, double deltaB)
    {
        var parts = new List<string>();
        if (Math.Abs(deltaA) > 0.0001)
        {
            parts.Add($"A{deltaA:0.###}");
        }

        if (Math.Abs(deltaB) > 0.0001)
        {
            parts.Add($"B{deltaB:0.###}");
        }

        return parts.Count == 0 ? "A/B unchanged" : string.Join(", ", parts);
    }

    private static Point3D TransformByVisualRotaryDelta(Point3D point, double visualDeltaX, double visualDeltaY)
    {
        var rotated = RotateVectorByVisualDelta(new Vector3D(point.X, point.Y, point.Z), visualDeltaX, visualDeltaY);
        return new Point3D(rotated.X, rotated.Y, rotated.Z);
    }

    private static Vector3D RotateVectorByVisualDelta(Vector3D vector, double visualDeltaX, double visualDeltaY)
    {
        var xRadians = DegreesToRadians(visualDeltaX);
        var xCos = Math.Cos(xRadians);
        var xSin = Math.Sin(xRadians);
        var afterX = new Vector3D(
            vector.X,
            (vector.Y * xCos) - (vector.Z * xSin),
            (vector.Y * xSin) + (vector.Z * xCos));

        var yRadians = DegreesToRadians(visualDeltaY);
        var yCos = Math.Cos(yRadians);
        var ySin = Math.Sin(yRadians);
        return new Vector3D(
            (afterX.X * yCos) + (afterX.Z * ySin),
            afterX.Y,
            (-afterX.X * ySin) + (afterX.Z * yCos));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

    private static double RadiansToDegrees(double radians) => radians * 180d / Math.PI;

    private static double NormalizeAngleDegrees(double angle)
    {
        while (angle <= -180)
        {
            angle += 360;
        }

        while (angle > 180)
        {
            angle -= 360;
        }

        return Math.Abs(angle) < 0.000001 ? 0 : angle;
    }

    private bool TryApplyBulkRemovalPlaneSelection(Point3D hitPoint)
    {
        if (SelectedOperation is null || _previewSceneData is null)
        {
            return false;
        }

        var feature = SelectedOperation.Feature;
        var roughingFloorZ = hitPoint.Z;
        var keepoutLoops = BuildBulkRemovalKeepoutLoops(roughingFloorZ, _previewSceneData.EdgeGeometries);
        feature.Shape = FeatureShape.ModelPlane;
        feature.Name = SelectedOperation.Type switch
        {
            OperationType.ZLevelFinishing => "Selected Z-Level Range",
            OperationType.Parallel3DFinishing => "Selected 3D Parallel Range",
            OperationType.ScallopFinishing => "Selected Scallop Range",
            OperationType.PencilCleanup => "Selected Pencil Range",
            _ => "Selected Roughing Plane"
        };
        feature.CenterX = hitPoint.X - CurrentSetup.WorkOffset.X - CurrentSetup.AlignmentOffsetX;
        feature.CenterY = hitPoint.Y - CurrentSetup.WorkOffset.Y - CurrentSetup.AlignmentOffsetY;
        feature.StartZ = SnapNearZero(roughingFloorZ - _previewSceneData.StockTopZ);
        feature.Depth = Math.Max(0, _previewSceneData.StockTopZ - roughingFloorZ);
        feature.KeepoutLoops = keepoutLoops;
        if (SelectedOperation.Type == OperationType.ScallopFinishing && !_previewSceneData.PartBounds.IsEmpty)
        {
            feature.Length = Math.Max(_previewSceneData.PartBounds.SizeX, feature.Length);
            feature.Width = Math.Max(_previewSceneData.PartBounds.SizeY, feature.Width);
            feature.Diameter = Math.Max(feature.Length, feature.Width);
        }

        NotifySelectedFeatureChanged();
        RefreshPreview();
        var strategyName = SelectedOperation.Type == OperationType.Raster
            ? SelectedOperation.UseRestMachining ? "REST raster roughing floor" : "raster roughing floor"
            : SelectedOperation.Type == OperationType.AdaptiveClearing
                ? SelectedOperation.UseRestMachining ? "REST adaptive roughing floor" : "adaptive roughing floor"
                : SelectedOperation.Type == OperationType.ZLevelFinishing
                    ? "Z-level finishing lower limit"
                : SelectedOperation.Type == OperationType.Parallel3DFinishing
                    ? "3D parallel finishing lower limit"
                : SelectedOperation.Type == OperationType.ScallopFinishing
                    ? "scallop finishing lower limit"
                : SelectedOperation.Type == OperationType.PencilCleanup
                    ? "pencil cleanup lower limit"
                : "bulk roughing floor";
        var selectionDetail = SelectedOperation.Type is OperationType.ZLevelFinishing or OperationType.Parallel3DFinishing or OperationType.ScallopFinishing or OperationType.PencilCleanup
            ? "The selected face sets the finishing lower limit."
            : $"{keepoutLoops.Count} protected model loop(s) above the plane.";
        StatusMessage =
            $"Selected {strategyName} for {SelectedOperation.Name}: Z{feature.StartZ:0.###} from stock top. {selectionDetail}";
        return true;
    }

    private bool TryApplyProfileEdgeSelection(PreviewEdgeGeometry edgeGeometry, Point3D hitPoint)
    {
        if (SelectedOperation is null || _previewSceneData is null || edgeGeometry.Points.Count < 2)
        {
            return false;
        }

        var feature = SelectedOperation.Feature;
        var resolvedEdge = ResolveProfileSelection(edgeGeometry, hitPoint, _previewSceneData.EdgeGeometries);
        var pathPoints = BuildRelativePathPoints(resolvedEdge, CurrentSetup, _previewSceneData.StockTopZ);
        if (pathPoints.Count < 2)
        {
            StatusMessage = "The selected edge was too short to create a profile path.";
            return false;
        }

        feature.Shape = FeatureShape.EdgePath;
        feature.PathPoints = pathPoints;
        feature.Name = SelectedOperation.Type switch
        {
            OperationType.Chamfer => "Selected Chamfer Edge",
            OperationType.Contour2D => "Selected Contour Edge",
            OperationType.PencilCleanup => "Selected Pencil Edge",
            _ => "Selected Model Edge"
        };
        feature.CenterX = pathPoints.Average(point => point.X);
        feature.CenterY = pathPoints.Average(point => point.Y);
        feature.StartZ = SnapNearZero(pathPoints.Max(point => point.Z));

        NotifySelectedFeatureChanged();
        RefreshPreview();
        var operationKind = SelectedOperation.Type switch
        {
            OperationType.Chamfer => "chamfer",
            OperationType.Contour2D => "2D contour",
            OperationType.PencilCleanup => "pencil cleanup",
            _ => "profile"
        };
        StatusMessage =
            $"Selected {operationKind} path for {SelectedOperation.Name}: {pathPoints.Count} points, length {CalculatePathLength(pathPoints):0.###}, Start Z{feature.StartZ:0.###}.";
        return true;
    }

    private static PreviewEdgeGeometry ResolveProfileSelection(
        PreviewEdgeGeometry clickedEdge,
        Point3D hitPoint,
        IReadOnlyList<PreviewEdgeGeometry> allEdges)
    {
        var tolerance = GetEdgeTolerance(allEdges);
        if (IsClosedInXy(clickedEdge, tolerance) && !IsMostlyVertical(clickedEdge, tolerance))
        {
            return clickedEdge;
        }

        var horizontalEdges = allEdges
            .Where(edge => edge.Points.Count >= 2 && !IsMostlyVertical(edge, tolerance))
            .ToList();
        if (horizontalEdges.Count == 0)
        {
            return clickedEdge;
        }

        if (IsMostlyVertical(clickedEdge, tolerance))
        {
            return ResolveVerticalSeamProfile(clickedEdge, hitPoint, horizontalEdges, tolerance);
        }

        var chain = BuildConnectedProfileChain(clickedEdge, horizontalEdges, tolerance);
        return chain.Points.Count >= 2 ? chain : clickedEdge;
    }

    private static PreviewEdgeGeometry ResolveVerticalSeamProfile(
        PreviewEdgeGeometry clickedEdge,
        Point3D hitPoint,
        IReadOnlyList<PreviewEdgeGeometry> horizontalEdges,
        double tolerance)
    {
        var endpoints = new[] { clickedEdge.Points[0], clickedEdge.Points[^1] };
        var candidates = horizontalEdges
            .Select(edge => new
            {
                Edge = edge,
                ConnectedDistance = endpoints.Min(endpoint => MinPointDistance(endpoint, edge.Points)),
                HitDistance = MinPointDistance(hitPoint, edge.Points),
                ZDistance = Math.Abs(AverageZ(edge) - hitPoint.Z),
                Closed = IsClosedInXy(edge, tolerance)
            })
            .Where(candidate => candidate.ConnectedDistance <= tolerance * 8 || candidate.HitDistance <= tolerance * 12)
            .OrderByDescending(candidate => candidate.Closed)
            .ThenBy(candidate => candidate.ConnectedDistance)
            .ThenBy(candidate => candidate.ZDistance)
            .ThenBy(candidate => candidate.HitDistance)
            .FirstOrDefault();

        if (candidates is null)
        {
            return horizontalEdges
                .OrderBy(edge => MinPointDistance(hitPoint, edge.Points))
                .First();
        }

        if (IsClosedInXy(candidates.Edge, tolerance))
        {
            return candidates.Edge;
        }

        var chain = BuildConnectedProfileChain(candidates.Edge, horizontalEdges, tolerance);
        return chain.Points.Count >= 2 ? chain : candidates.Edge;
    }

    private static PreviewEdgeGeometry BuildConnectedProfileChain(
        PreviewEdgeGeometry startEdge,
        IReadOnlyList<PreviewEdgeGeometry> candidateEdges,
        double tolerance)
    {
        var remaining = candidateEdges
            .Where(edge => !ReferenceEquals(edge, startEdge))
            .ToList();
        var points = startEdge.Points.ToList();
        var start = points[0];

        while (remaining.Count > 0)
        {
            var end = points[^1];
            var next = remaining
                .Select(edge => new
                {
                    Edge = edge,
                    ForwardDistance = DistanceSquared3D(end, edge.Points[0]),
                    ReverseDistance = DistanceSquared3D(end, edge.Points[^1])
                })
                .OrderBy(candidate => Math.Min(candidate.ForwardDistance, candidate.ReverseDistance))
                .First();

            var bestDistance = Math.Min(next.ForwardDistance, next.ReverseDistance);
            if (bestDistance > tolerance * tolerance)
            {
                break;
            }

            remaining.Remove(next.Edge);
            var nextPoints = next.ForwardDistance <= next.ReverseDistance
                ? next.Edge.Points
                : next.Edge.Points.Reverse().ToList();

            points.AddRange(nextPoints.Skip(1));
            if (DistanceSquared3D(points[^1], start) <= tolerance * tolerance)
            {
                points[^1] = start;
                break;
            }
        }

        return new PreviewEdgeGeometry(points, "Connected STEP profile loop");
    }

    private static List<FeaturePathPoint> BuildRelativePathPoints(
        PreviewEdgeGeometry edgeGeometry,
        JobSetup setup,
        double stockTopZ)
    {
        var points = new List<FeaturePathPoint>();
        foreach (var point in edgeGeometry.Points)
        {
            var relativePoint = new FeaturePathPoint
            {
                X = point.X - setup.WorkOffset.X - setup.AlignmentOffsetX,
                Y = point.Y - setup.WorkOffset.Y - setup.AlignmentOffsetY,
                Z = SnapNearZero(point.Z - stockTopZ)
            };

            if (points.Count == 0 || DistanceSquared(points[^1], relativePoint) > 0.000001)
            {
                points.Add(relativePoint);
            }
        }

        return points;
    }

    private void NotifySelectedFeatureChanged()
    {
        OnPropertyChanged(nameof(SelectedOperation));
        OnPropertyChanged($"{nameof(SelectedOperation)}.{nameof(ToolpathOperationDefinition.Feature)}.{nameof(FeatureDefinition.CenterX)}");
        OnPropertyChanged($"{nameof(SelectedOperation)}.{nameof(ToolpathOperationDefinition.Feature)}.{nameof(FeatureDefinition.CenterY)}");
        OnPropertyChanged($"{nameof(SelectedOperation)}.{nameof(ToolpathOperationDefinition.Feature)}.{nameof(FeatureDefinition.StartZ)}");
        OnPropertyChanged($"{nameof(SelectedOperation)}.{nameof(ToolpathOperationDefinition.Feature)}.{nameof(FeatureDefinition.Depth)}");
        OnPropertyChanged($"{nameof(SelectedOperation)}.{nameof(ToolpathOperationDefinition.Feature)}.{nameof(FeatureDefinition.Diameter)}");
        OnPropertyChanged($"{nameof(SelectedOperation)}.{nameof(ToolpathOperationDefinition.Feature)}.{nameof(FeatureDefinition.Rows)}");
        OnPropertyChanged($"{nameof(SelectedOperation)}.{nameof(ToolpathOperationDefinition.Feature)}.{nameof(FeatureDefinition.Columns)}");
        OnPropertyChanged($"{nameof(SelectedOperation)}.{nameof(ToolpathOperationDefinition.Feature)}.{nameof(FeatureDefinition.Shape)}");
        OnPropertyChanged($"{nameof(SelectedOperation)}.{nameof(ToolpathOperationDefinition.Feature)}.{nameof(FeatureDefinition.PathPoints)}");
        OnPropertyChanged($"{nameof(SelectedOperation)}.{nameof(ToolpathOperationDefinition.Feature)}.{nameof(FeatureDefinition.KeepoutLoops)}");
        OnPropertyChanged($"{nameof(SelectedOperation)}.{nameof(ToolpathOperationDefinition.RotaryIndexA)}");
        OnPropertyChanged($"{nameof(SelectedOperation)}.{nameof(ToolpathOperationDefinition.RotaryIndexB)}");
    }

    private static double SnapNearZero(double value)
    {
        return Math.Abs(value) < 0.001 ? 0d : value;
    }

    private static double DistanceSquared(FeaturePathPoint first, FeaturePathPoint second)
    {
        var x = second.X - first.X;
        var y = second.Y - first.Y;
        var z = second.Z - first.Z;
        return (x * x) + (y * y) + (z * z);
    }

    private static double CalculatePathLength(IReadOnlyList<FeaturePathPoint> pathPoints)
    {
        double length = 0;
        for (var index = 1; index < pathPoints.Count; index++)
        {
            length += Math.Sqrt(DistanceSquared(pathPoints[index - 1], pathPoints[index]));
        }

        return length;
    }

    private static bool IsMostlyVertical(PreviewEdgeGeometry edge, double tolerance)
    {
        var xyLength = 0d;
        var zLength = 0d;
        for (var index = 1; index < edge.Points.Count; index++)
        {
            var previous = edge.Points[index - 1];
            var current = edge.Points[index];
            var dx = current.X - previous.X;
            var dy = current.Y - previous.Y;
            var dz = current.Z - previous.Z;
            xyLength += Math.Sqrt((dx * dx) + (dy * dy));
            zLength += Math.Abs(dz);
        }

        return zLength > tolerance && xyLength < Math.Max(tolerance * 2, zLength * 0.12);
    }

    private List<FeaturePath> BuildBulkRemovalKeepoutLoops(double roughingFloorZ, IReadOnlyList<PreviewEdgeGeometry> edges)
    {
        if (edges.Count == 0 || _previewSceneData is null)
        {
            return new List<FeaturePath>();
        }

        var tolerance = GetEdgeTolerance(edges);
        return edges
            .Where(edge => edge.Points.Count >= 4)
            .Where(edge => IsClosedInXy(edge, tolerance))
            .Where(edge => IsMostlyHorizontal(edge, tolerance))
            .Where(edge => AverageZ(edge) > roughingFloorZ + Math.Max(tolerance * 0.35, 0.01))
            .GroupBy(edge => BuildLoopKey(edge, tolerance))
            .Select(group => group.OrderByDescending(AverageZ).First())
            .Select(edge => new FeaturePath
            {
                Points = BuildRelativePathPoints(edge, CurrentSetup, _previewSceneData.StockTopZ)
            })
            .Where(path => path.Points.Count >= 4)
            .ToList();
    }

    private List<DrillCircleEdge> BuildDrillCircleEdges(IReadOnlyList<PreviewEdgeGeometry> edges, Rect3D partBounds)
    {
        var tolerance = GetEdgeTolerance(edges);
        var circles = new List<DrillCircleEdge>();
        var circleKeys = new HashSet<string>();
        foreach (var edge in edges)
        {
            if (TryGetDrillCircle(edge, tolerance, partBounds, out var circle))
            {
                AddUniqueDrillCircle(circles, circleKeys, circle, tolerance);
            }
        }

        foreach (var loop in BuildClosedEdgeLoops(edges, tolerance))
        {
            if (TryGetDrillCircle(loop, tolerance, partBounds, out var circle))
            {
                AddUniqueDrillCircle(circles, circleKeys, circle, tolerance);
            }
        }

        return circles;
    }

    private static void AddUniqueDrillCircle(
        List<DrillCircleEdge> circles,
        ISet<string> circleKeys,
        DrillCircleEdge circle,
        double tolerance)
    {
        var keyScale = Math.Max(tolerance * 2d, 0.05);
        var key = string.Join(
            ":",
            RoundForKey(circle.Center.X, keyScale),
            RoundForKey(circle.Center.Y, keyScale),
            RoundForKey(circle.Center.Z, keyScale),
            RoundForKey(circle.Radius, keyScale),
            RoundForKey(Math.Abs(circle.Normal.X), 0.02),
            RoundForKey(Math.Abs(circle.Normal.Y), 0.02),
            RoundForKey(Math.Abs(circle.Normal.Z), 0.02));
        if (circleKeys.Add(key))
        {
            circles.Add(circle);
        }
    }

    private static IReadOnlyList<PreviewEdgeGeometry> BuildClosedEdgeLoops(IReadOnlyList<PreviewEdgeGeometry> edges, double tolerance)
    {
        var openChains = edges
            .Select(edge => TrimDuplicateClosingPoint(edge.Points, tolerance))
            .Where(points => points.Count >= 2)
            .Where(points => DistanceSquared3D(points[0], points[^1]) > tolerance * tolerance)
            .Select(points => new EdgeChain(points))
            .ToList();
        if (openChains.Count == 0)
        {
            return Array.Empty<PreviewEdgeGeometry>();
        }

        var joinTolerance = Math.Max(tolerance * 2d, 0.04);
        var joinToleranceSquared = joinTolerance * joinTolerance;
        var globallyUsed = new bool[openChains.Count];
        var loops = new List<PreviewEdgeGeometry>();

        for (var startIndex = 0; startIndex < openChains.Count; startIndex++)
        {
            if (globallyUsed[startIndex])
            {
                continue;
            }

            var usedThisLoop = new HashSet<int> { startIndex };
            var loopPoints = new List<Point3D>(openChains[startIndex].Points);
            while (usedThisLoop.Count < openChains.Count
                && DistanceSquared3D(loopPoints[0], loopPoints[^1]) > joinToleranceSquared)
            {
                var last = loopPoints[^1];
                var bestIndex = -1;
                var reverse = false;
                var bestDistance = double.MaxValue;
                for (var candidateIndex = 0; candidateIndex < openChains.Count; candidateIndex++)
                {
                    if (globallyUsed[candidateIndex] || usedThisLoop.Contains(candidateIndex))
                    {
                        continue;
                    }

                    var candidate = openChains[candidateIndex];
                    var startDistance = DistanceSquared3D(last, candidate.Points[0]);
                    if (startDistance < bestDistance)
                    {
                        bestDistance = startDistance;
                        bestIndex = candidateIndex;
                        reverse = false;
                    }

                    var endDistance = DistanceSquared3D(last, candidate.Points[^1]);
                    if (endDistance < bestDistance)
                    {
                        bestDistance = endDistance;
                        bestIndex = candidateIndex;
                        reverse = true;
                    }
                }

                if (bestIndex < 0 || bestDistance > joinToleranceSquared)
                {
                    break;
                }

                usedThisLoop.Add(bestIndex);
                var pointsToAppend = reverse
                    ? openChains[bestIndex].Points.AsEnumerable().Reverse().ToList()
                    : openChains[bestIndex].Points;
                AppendConnectedPoints(loopPoints, pointsToAppend);
            }

            if (loopPoints.Count >= 8 && DistanceSquared3D(loopPoints[0], loopPoints[^1]) <= joinToleranceSquared)
            {
                loopPoints[^1] = loopPoints[0];
                loops.Add(new PreviewEdgeGeometry(RemoveConsecutiveDuplicatePoints(loopPoints, tolerance), "stitched STEP circular edge"));
                foreach (var usedIndex in usedThisLoop)
                {
                    globallyUsed[usedIndex] = true;
                }
            }
        }

        return loops;
    }

    private static IReadOnlyList<Point3D> TrimDuplicateClosingPoint(IReadOnlyList<Point3D> points, double tolerance)
    {
        if (points.Count <= 1 || DistanceSquared3D(points[0], points[^1]) > tolerance * tolerance)
        {
            return points;
        }

        return points.Take(points.Count - 1).ToList();
    }

    private static void AppendConnectedPoints(List<Point3D> target, IReadOnlyList<Point3D> points)
    {
        if (points.Count == 0)
        {
            return;
        }

        var startIndex = target.Count > 0 && DistanceSquared3D(target[^1], points[0]) < 0.0000001
            ? 1
            : 0;
        for (var index = startIndex; index < points.Count; index++)
        {
            target.Add(points[index]);
        }
    }

    private static IReadOnlyList<Point3D> RemoveConsecutiveDuplicatePoints(IReadOnlyList<Point3D> points, double tolerance)
    {
        var cleaned = new List<Point3D>(points.Count);
        var toleranceSquared = tolerance * tolerance * 0.04;
        foreach (var point in points)
        {
            if (cleaned.Count == 0 || DistanceSquared3D(cleaned[^1], point) > toleranceSquared)
            {
                cleaned.Add(point);
            }
        }

        return cleaned;
    }

    private static bool TryGetDrillCircle(PreviewEdgeGeometry edge, double tolerance, Rect3D partBounds, out DrillCircleEdge circle)
    {
        circle = default;
        if (edge.Points.Count < 8 || !IsClosed3D(edge, tolerance))
        {
            return false;
        }

        var points = edge.Points
            .Take(edge.Points.Count > 1 && DistanceSquared3D(edge.Points[0], edge.Points[^1]) <= tolerance * tolerance
                ? edge.Points.Count - 1
                : edge.Points.Count)
            .ToList();
        if (points.Count < 8)
        {
            return false;
        }

        if (!TryGetLoopNormal(points, out var normal))
        {
            return false;
        }

        var average = AveragePoint(points);
        OrientNormalOutward(ref normal, average, partBounds);
        var basisX = GetPerpendicular(normal);
        var basisY = Vector3D.CrossProduct(normal, basisX);
        if (basisY.LengthSquared < 0.0000001)
        {
            return false;
        }

        basisY.Normalize();
        var projected = points
            .Select(point =>
            {
                var vector = point - average;
                return new Point(
                    Vector3D.DotProduct(vector, basisX),
                    Vector3D.DotProduct(vector, basisY));
            })
            .ToList();
        var centerOffsetX = (projected.Min(point => point.X) + projected.Max(point => point.X)) / 2d;
        var centerOffsetY = (projected.Min(point => point.Y) + projected.Max(point => point.Y)) / 2d;
        var center = average + (basisX * centerOffsetX) + (basisY * centerOffsetY);
        var planarSpread = points
            .Select(point => Math.Abs(Vector3D.DotProduct(point - center, normal)))
            .DefaultIfEmpty(0)
            .Max();
        var radii = points
            .Select(point =>
            {
                var radial = point - center;
                radial -= normal * Vector3D.DotProduct(radial, normal);
                return radial.Length;
            })
            .ToList();
        var radius = radii.Average();
        if (radius <= tolerance)
        {
            return false;
        }

        var radialSpread = radii.Max() - radii.Min();
        if (planarSpread > Math.Max(radius * 0.08, tolerance * 4)
            || radialSpread > Math.Max(radius * 0.20, tolerance * 4))
        {
            return false;
        }

        circle = new DrillCircleEdge(center, radius, normal, edge);
        return true;
    }

    private static bool IsMatchingDrillCircle(DrillCircleEdge selected, DrillCircleEdge candidate)
    {
        var centerTolerance = Math.Max(selected.Radius * 0.18, 0.08);
        var radiusTolerance = Math.Max(selected.Radius * 0.18, 0.08);
        var normalDot = Math.Abs(Vector3D.DotProduct(selected.Normal, candidate.Normal));
        var offset = candidate.Center - selected.Center;
        var axisDistance = Vector3D.DotProduct(offset, selected.Normal);
        var radialOffset = offset - (selected.Normal * axisDistance);
        return normalDot >= 0.94
            && radialOffset.Length <= centerTolerance
            && Math.Abs(selected.Radius - candidate.Radius) <= radiusTolerance;
    }

    private static double DistanceToDrillCircle(Point3D point, DrillCircleEdge circle)
    {
        var offset = point - circle.Center;
        var planeDistance = Math.Abs(Vector3D.DotProduct(offset, circle.Normal));
        var radial = offset - (circle.Normal * Vector3D.DotProduct(offset, circle.Normal));
        var radialDistance = Math.Abs(radial.Length - circle.Radius);
        return (planeDistance * planeDistance) + (radialDistance * radialDistance);
    }

    private static double SignedDistanceAlongNormal(DrillCircleEdge reference, Point3D point)
    {
        return Vector3D.DotProduct(point - reference.Center, reference.Normal);
    }

    private static bool IsClosedInXy(PreviewEdgeGeometry edge, double tolerance)
    {
        if (edge.Points.Count < 3)
        {
            return false;
        }

        var first = edge.Points[0];
        var last = edge.Points[^1];
        var dx = last.X - first.X;
        var dy = last.Y - first.Y;
        return (dx * dx) + (dy * dy) <= tolerance * tolerance;
    }

    private static bool IsClosed3D(PreviewEdgeGeometry edge, double tolerance)
    {
        return edge.Points.Count >= 3
            && DistanceSquared3D(edge.Points[0], edge.Points[^1]) <= tolerance * tolerance;
    }

    private static bool IsMostlyHorizontal(PreviewEdgeGeometry edge, double tolerance)
    {
        if (edge.Points.Count < 2)
        {
            return false;
        }

        var minZ = edge.Points.Min(point => point.Z);
        var maxZ = edge.Points.Max(point => point.Z);
        return maxZ - minZ <= Math.Max(tolerance * 1.5, 0.02);
    }

    private static string BuildLoopKey(PreviewEdgeGeometry edge, double tolerance)
    {
        var points = edge.Points;
        var minX = points.Min(point => point.X);
        var maxX = points.Max(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxY = points.Max(point => point.Y);
        var scale = Math.Max(tolerance * 4, 0.1);
        return string.Join(
            ":",
            RoundForKey((minX + maxX) / 2d, scale),
            RoundForKey((minY + maxY) / 2d, scale),
            RoundForKey(maxX - minX, scale),
            RoundForKey(maxY - minY, scale));
    }

    private static long RoundForKey(double value, double scale)
    {
        return (long)Math.Round(value / scale);
    }

    private readonly record struct DrillCircleEdge(Point3D Center, double Radius, Vector3D Normal, PreviewEdgeGeometry? Source);

    private readonly record struct EdgeChain(IReadOnlyList<Point3D> Points);

    private readonly record struct RotaryAlignmentSolution(
        bool IsValid,
        double DeltaA,
        double DeltaB,
        double VisualDeltaX,
        double VisualDeltaY,
        double Residual,
        string Description);

    private static bool TryGetLoopNormal(IReadOnlyList<Point3D> points, out Vector3D normal)
    {
        normal = default;
        for (var index = 0; index < points.Count; index++)
        {
            var current = points[index];
            var next = points[(index + 1) % points.Count];
            normal.X += (current.Y - next.Y) * (current.Z + next.Z);
            normal.Y += (current.Z - next.Z) * (current.X + next.X);
            normal.Z += (current.X - next.X) * (current.Y + next.Y);
        }

        if (normal.LengthSquared < 0.000001)
        {
            var origin = points[0];
            for (var first = 1; first < points.Count - 1; first++)
            {
                for (var second = first + 1; second < points.Count; second++)
                {
                    normal = Vector3D.CrossProduct(points[first] - origin, points[second] - origin);
                    if (normal.LengthSquared >= 0.000001)
                    {
                        normal.Normalize();
                        return true;
                    }
                }
            }

            return false;
        }

        normal.Normalize();
        return true;
    }

    private static Point3D AveragePoint(IReadOnlyList<Point3D> points)
    {
        return new Point3D(
            points.Average(point => point.X),
            points.Average(point => point.Y),
            points.Average(point => point.Z));
    }

    private static void OrientNormalOutward(ref Vector3D normal, Point3D center, Rect3D partBounds)
    {
        if (!partBounds.IsEmpty)
        {
            var partCenter = new Point3D(
                partBounds.X + (partBounds.SizeX / 2d),
                partBounds.Y + (partBounds.SizeY / 2d),
                partBounds.Z + (partBounds.SizeZ / 2d));
            var outward = center - partCenter;
            if (outward.LengthSquared > 0.000001)
            {
                outward.Normalize();
                if (Vector3D.DotProduct(normal, outward) < 0)
                {
                    normal = -normal;
                }

                return;
            }
        }

        if (normal.Z < 0)
        {
            normal = -normal;
        }
    }

    private static Vector3D GetPerpendicular(Vector3D normal)
    {
        var reference = Math.Abs(Vector3D.DotProduct(normal, new Vector3D(0, 0, 1))) > 0.92
            ? new Vector3D(1, 0, 0)
            : new Vector3D(0, 0, 1);
        var perpendicular = Vector3D.CrossProduct(normal, reference);
        if (perpendicular.LengthSquared < 0.000001)
        {
            perpendicular = new Vector3D(1, 0, 0);
        }

        perpendicular.Normalize();
        return perpendicular;
    }

    private static double AverageZ(PreviewEdgeGeometry edge)
    {
        return edge.Points.Count == 0 ? 0 : edge.Points.Average(point => point.Z);
    }

    private static double MinPointDistance(Point3D point, IReadOnlyList<Point3D> points)
    {
        return Math.Sqrt(points.Min(candidate => DistanceSquared3D(point, candidate)));
    }

    private static double DistanceSquared3D(Point3D first, Point3D second)
    {
        var dx = second.X - first.X;
        var dy = second.Y - first.Y;
        var dz = second.Z - first.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private static double GetEdgeTolerance(IReadOnlyList<PreviewEdgeGeometry> edges)
    {
        if (edges.Count == 0)
        {
            return 0.05;
        }

        var minX = edges.SelectMany(edge => edge.Points).Min(point => point.X);
        var maxX = edges.SelectMany(edge => edge.Points).Max(point => point.X);
        var minY = edges.SelectMany(edge => edge.Points).Min(point => point.Y);
        var maxY = edges.SelectMany(edge => edge.Points).Max(point => point.Y);
        var minZ = edges.SelectMany(edge => edge.Points).Min(point => point.Z);
        var maxZ = edges.SelectMany(edge => edge.Points).Max(point => point.Z);
        var largest = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
        return Math.Clamp(largest * 0.003, 0.02, 1.5);
    }


    private void AddMachineProfile()
    {
        var profile = new MachineProfile
        {
            Name = $"Machine {MachineProfiles.Count + 1}",
            Family = MachineFamily.Mill,
            Kinematics = KinematicsMode.ThreeAxisMill,
            Controller = "GRBL",
            SpindleMinRpm = 500,
            SpindleMaxRpm = 18000,
            MaxRapidRate = 3000,
            HomePoint = new Pose6D(),
            Notes = "Add axes and machine limits here."
        };

        profile.Axes.Add(new MachineAxis { Name = "X", Type = AxisType.Linear, TravelMax = 300, MaxFeedRate = 3000 });
        profile.Axes.Add(new MachineAxis { Name = "Y", Type = AxisType.Linear, TravelMax = 200, MaxFeedRate = 3000 });
        profile.Axes.Add(new MachineAxis { Name = "Z", Type = AxisType.Linear, TravelMin = -150, TravelMax = 0, MaxFeedRate = 1500 });

        MachineProfiles.Add(profile);
        SelectedMachineProfile = profile;
        StatusMessage = $"Added machine profile {profile.Name}.";
        RefreshProjectSummary();
    }

    private void RemoveMachineProfile()
    {
        if (SelectedMachineProfile is null)
        {
            return;
        }

        var profileToRemove = SelectedMachineProfile;
        MachineProfiles.Remove(profileToRemove);
        SelectedMachineProfile = ResolveSelectedMachine();
        StatusMessage = $"Removed machine profile {profileToRemove.Name}.";
        RefreshProjectSummary();
    }

    private void ConfigureIndexedFourthAxis()
    {
        var machine = SelectedMachineProfile;
        if (machine is null)
        {
            StatusMessage = "Select a machine profile before adding indexed rotary axes.";
            return;
        }

        if (!SupportsIndexedRotary(machine))
        {
            machine.Kinematics = KinematicsMode.ThreePlusTwoMill;
        }

        AddOrRepairRotaryAxis(machine, "A", RotaryAxisDirection.X);
        if (AllowsMultipleIndexedRotaryAxes(machine))
        {
            AddOrRepairRotaryAxis(machine, "B", RotaryAxisDirection.Y);
        }

        NormalizeMachineRotaryAxesForKinematics(machine);
        ClearUnsupportedRotaryIndexes();

        StatusMessage = AllowsMultipleIndexedRotaryAxes(machine)
            ? $"{machine.Name} is configured for A/B indexed rotary positioning. Use the Around column to swap A and B machine directions."
            : $"{machine.Name} is configured for single indexed rotary positioning. Use the Around column to choose the X or Y machine direction.";
        OnPropertyChanged(nameof(SelectedMachineProfile));
        NotifyRotaryUiProperties();
        RefreshProjectSummary();
        ToolpathDiagnostics = BuildToolpathDiagnostics(_previewToolpaths);
        RaiseCommandStates();
    }

    private static void AddOrRepairRotaryAxis(MachineProfile machine, string axisName, RotaryAxisDirection defaultDirection)
    {
        var rotaryAxis = machine.Axes.FirstOrDefault(axis =>
            string.Equals(NormalizeRotaryAxisName(axis.Name), axisName, StringComparison.OrdinalIgnoreCase));
        if (rotaryAxis is null)
        {
            machine.Axes.Add(new MachineAxis
            {
                Name = axisName,
                Type = AxisType.Rotary,
                TravelMin = -360,
                TravelMax = 360,
                MaxFeedRate = 3600,
                HomePosition = 0,
                Mount = RotaryAxisMount.Table,
                RotatesAround = defaultDirection
            });
            return;
        }

        rotaryAxis.Name = axisName;
        rotaryAxis.Type = AxisType.Rotary;
        if (Math.Abs(rotaryAxis.TravelMax - rotaryAxis.TravelMin) < 0.0001)
        {
            rotaryAxis.TravelMin = -360;
            rotaryAxis.TravelMax = 360;
        }

        if (rotaryAxis.MaxFeedRate <= 0)
        {
            rotaryAxis.MaxFeedRate = 3600;
        }
    }

    private void AddTool()
    {
        var tool = new ToolDefinition
        {
            Number = ToolLibrary.Count == 0 ? 1 : ToolLibrary.Max(existing => existing.Number) + 1,
            Name = $"Tool {ToolLibrary.Count + 1}",
            Style = ToolStyle.Square,
            CuttingDiameter = 6,
            CuttingLength = 20,
            FluteLength = 20,
            ShankDiameter = 6,
            StickOut = 35,
            OverallLength = 60,
            FluteCount = 2,
            MaxStepDown = 2,
            MaxStepOver = 3
        };

        ToolLibrary.Add(tool);
        SelectedTool = tool;
        StatusMessage = $"Added tool T{tool.Number}.";
        RefreshProjectSummary();
    }

    private void RemoveTool()
    {
        if (SelectedTool is null)
        {
            return;
        }

        var toolToRemove = SelectedTool;
        ToolLibrary.Remove(toolToRemove);
        SelectedTool = ToolLibrary.FirstOrDefault();
        StatusMessage = $"Removed tool T{toolToRemove.Number}.";
        RefreshProjectSummary();
    }

    private void StartPickingSetupOrigin()
    {
        IsPickingSetupOrigin = true;
        StatusMessage = "Pick a point on the visible stock or model to set the active setup origin.";
    }

    private void ApplySetupOriginPreset()
    {
        if (CurrentSetup.OriginAnchor == SetupOriginAnchor.PickedPoint)
        {
            StartPickingSetupOrigin();
            return;
        }

        if (_previewSceneData is null)
        {
            RefreshPreview();
        }

        if (_previewSceneData is null)
        {
            StatusMessage = "Preview geometry is not ready yet, so the setup origin could not be applied.";
            return;
        }

        var bounds = CurrentSetup.OriginSource == SetupOriginSource.Stock
            ? _previewSceneData.StockBounds
            : _previewSceneData.PartBounds;
        if (bounds.IsEmpty || bounds.SizeX <= 0 || bounds.SizeY <= 0 || bounds.SizeZ <= 0)
        {
            StatusMessage = $"No usable {CurrentSetup.OriginSource.ToString().ToLowerInvariant()} bounds are available for origin selection.";
            return;
        }

        var origin = GetAnchorPoint(bounds, CurrentSetup.OriginAnchor);
        SetCurrentSetupOrigin(origin);
        IsPickingSetupOrigin = false;
        RefreshPreview();
        StatusMessage =
            $"Set {CurrentSetup.Name} origin to {CurrentSetup.OriginSource} {CurrentSetup.OriginAnchor}: X{origin.X:0.###}, Y{origin.Y:0.###}, Z{origin.Z:0.###}.";
    }

    private void SetCurrentSetupOrigin(Point3D point)
    {
        CurrentSetup.WorkOrigin.X = point.X;
        CurrentSetup.WorkOrigin.Y = point.Y;
        CurrentSetup.WorkOrigin.Z = point.Z;
        OnPropertyChanged(nameof(CurrentSetup));
        OnPropertyChanged($"{nameof(CurrentSetup)}.{nameof(JobSetup.WorkOrigin)}.{nameof(Pose6D.X)}");
        OnPropertyChanged($"{nameof(CurrentSetup)}.{nameof(JobSetup.WorkOrigin)}.{nameof(Pose6D.Y)}");
        OnPropertyChanged($"{nameof(CurrentSetup)}.{nameof(JobSetup.WorkOrigin)}.{nameof(Pose6D.Z)}");
        OnPropertyChanged($"{nameof(CurrentSetup)}.{nameof(JobSetup.OriginSource)}");
        OnPropertyChanged($"{nameof(CurrentSetup)}.{nameof(JobSetup.OriginAnchor)}");
    }

    private static Point3D GetAnchorPoint(Rect3D bounds, SetupOriginAnchor anchor)
    {
        var minX = bounds.X;
        var midX = bounds.X + (bounds.SizeX / 2d);
        var maxX = bounds.X + bounds.SizeX;
        var minY = bounds.Y;
        var midY = bounds.Y + (bounds.SizeY / 2d);
        var maxY = bounds.Y + bounds.SizeY;
        var minZ = bounds.Z;
        var midZ = bounds.Z + (bounds.SizeZ / 2d);
        var maxZ = bounds.Z + bounds.SizeZ;

        return anchor switch
        {
            SetupOriginAnchor.Center => new Point3D(midX, midY, midZ),
            SetupOriginAnchor.BottomCenter => new Point3D(midX, midY, minZ),
            SetupOriginAnchor.TopFrontLeft => new Point3D(minX, minY, maxZ),
            SetupOriginAnchor.TopFrontRight => new Point3D(maxX, minY, maxZ),
            SetupOriginAnchor.TopBackLeft => new Point3D(minX, maxY, maxZ),
            SetupOriginAnchor.TopBackRight => new Point3D(maxX, maxY, maxZ),
            SetupOriginAnchor.TopFrontMidpoint => new Point3D(midX, minY, maxZ),
            SetupOriginAnchor.TopBackMidpoint => new Point3D(midX, maxY, maxZ),
            SetupOriginAnchor.TopLeftMidpoint => new Point3D(minX, midY, maxZ),
            SetupOriginAnchor.TopRightMidpoint => new Point3D(maxX, midY, maxZ),
            SetupOriginAnchor.BottomFrontLeft => new Point3D(minX, minY, minZ),
            SetupOriginAnchor.BottomFrontRight => new Point3D(maxX, minY, minZ),
            SetupOriginAnchor.BottomBackLeft => new Point3D(minX, maxY, minZ),
            SetupOriginAnchor.BottomBackRight => new Point3D(maxX, maxY, minZ),
            _ => new Point3D(midX, midY, maxZ)
        };
    }

    private void AddSetup()
    {
        var setupNumber = Setups.Count + 1;
        var setup = CloneSetup(CurrentSetup);
        setup.Name = $"Setup {setupNumber}";
        setup.WorkOffsetCode = $"G{Math.Min(54 + Setups.Count, 59)}";
        setup.TransferRestFromPreviousSetup = Setups.Count > 0;
        setup.Operations.Clear();

        Setups.Add(setup);
        SelectedSetup = setup;
        StatusMessage = $"Added {setup.Name}. Rotate or offset this setup, then add its operations.";
        RefreshProjectSummary();
    }

    private void RemoveSetup()
    {
        if (SelectedSetup is null || Setups.Count <= 1)
        {
            return;
        }

        var removedIndex = Math.Max(0, Setups.IndexOf(SelectedSetup));
        var setupToRemove = SelectedSetup;
        Setups.Remove(setupToRemove);
        SelectedSetup = Setups[Math.Min(removedIndex, Setups.Count - 1)];
        StatusMessage = $"Removed {setupToRemove.Name}.";
        RefreshProjectSummary();
    }

    private static JobSetup CloneSetup(JobSetup source)
    {
        return new JobSetup
        {
            Name = source.Name,
            TransferRestFromPreviousSetup = source.TransferRestFromPreviousSetup,
            Part = ClonePart(source.Part),
            Stock = CloneStock(source.Stock),
            WorkOrigin = ClonePose(source.WorkOrigin),
            OriginSource = source.OriginSource,
            OriginAnchor = source.OriginAnchor,
            FlipAxisX = source.FlipAxisX,
            FlipAxisY = source.FlipAxisY,
            FlipAxisZ = source.FlipAxisZ,
            WorkOffset = ClonePose(source.WorkOffset),
            AlignmentOffsetX = source.AlignmentOffsetX,
            AlignmentOffsetY = source.AlignmentOffsetY,
            AlignmentOffsetZ = source.AlignmentOffsetZ,
            SafeZ = source.SafeZ,
            ClearanceZ = source.ClearanceZ,
            WorkOffsetCode = source.WorkOffsetCode,
            Notes = source.Notes
        };
    }

    private static PartDefinition ClonePart(PartDefinition source)
    {
        return new PartDefinition
        {
            Name = source.Name,
            SourceType = source.SourceType,
            SourcePath = source.SourcePath,
            LengthX = source.LengthX,
            WidthY = source.WidthY,
            HeightZ = source.HeightZ,
            Diameter = source.Diameter,
            RotationA = source.RotationA,
            RotationB = source.RotationB,
            RotationC = source.RotationC
        };
    }

    private static StockDefinition CloneStock(StockDefinition source)
    {
        return new StockDefinition
        {
            Shape = source.Shape,
            ShowInPreview = source.ShowInPreview,
            ImportedSolidPath = source.ImportedSolidPath,
            LengthX = source.LengthX,
            WidthY = source.WidthY,
            HeightZ = source.HeightZ,
            Diameter = source.Diameter,
            OffsetX = source.OffsetX,
            OffsetY = source.OffsetY,
            OffsetZ = source.OffsetZ,
            RotationA = source.RotationA,
            RotationB = source.RotationB,
            RotationC = source.RotationC,
            RadialAllowance = source.RadialAllowance,
            AxialAllowance = source.AxialAllowance
        };
    }

    private static Pose6D ClonePose(Pose6D source)
    {
        return new Pose6D
        {
            X = source.X,
            Y = source.Y,
            Z = source.Z,
            A = source.A,
            B = source.B,
            C = source.C
        };
    }

    private void AddOperation()
    {
        var defaultTool = ToolLibrary.FirstOrDefault();
        var operation = new ToolpathOperationDefinition
        {
            Name = $"Operation {ActiveOperations.Count + 1}",
            Type = OperationType.Pocket,
            ToolNumber = defaultTool?.Number ?? 1,
            SpindleSpeed = 12000,
            FeedRate = 900,
            PlungeRate = 250,
            StepDown = defaultTool?.MaxStepDown ?? 2,
            StepOver = defaultTool?.MaxStepOver ?? 2.5,
            SafeRetractZ = 6,
            Feature = new FeatureDefinition
            {
                Name = "Feature",
                Shape = FeatureShape.Rectangle,
                Length = 30,
                Width = 20,
                Depth = 4
            }
        };

        ActiveOperations.Add(operation);
        SelectedOperation = operation;
        StatusMessage = $"Added operation {operation.Name} to {CurrentSetup.Name}.";
        RefreshProjectSummary();
    }

    private void RemoveOperation()
    {
        if (SelectedOperation is null)
        {
            return;
        }

        var operationToRemove = SelectedOperation;
        ActiveOperations.Remove(operationToRemove);
        SelectedOperation = ActiveOperations.FirstOrDefault();
        StatusMessage = $"Removed operation {operationToRemove.Name}.";
        RefreshProjectSummary();
    }

    private void PlayToolpath()
    {
        PreparePlayback(rebuildToolpaths: true);
        if (_playbackSegmentCount == 0)
        {
            PlaybackStatus = "No toolpath moves are available for playback.";
            StatusMessage = "Generate or define an enabled operation before starting playback.";
            return;
        }

        if (_playbackPosition >= _playbackSegmentCount)
        {
            _playbackPosition = 0;
        }

        IsPlaybackActive = true;
        IsPlaybackRunning = true;
        UpdatePlaybackProgress();
        _playbackTimer.Start();
        RefreshPreview(rebuildToolpaths: false);
        StatusMessage = "Playing toolpath preview with opaque green stock removal.";
    }

    private void PauseToolpath()
    {
        _playbackTimer.Stop();
        IsPlaybackRunning = false;
        UpdatePlaybackStatus();
        RefreshPreview(rebuildToolpaths: false);
    }

    private void StepToolpath()
    {
        PreparePlayback(rebuildToolpaths: false);
        if (_playbackSegmentCount == 0)
        {
            PlaybackStatus = "No toolpath moves are available for playback.";
            return;
        }

        _playbackTimer.Stop();
        IsPlaybackActive = true;
        IsPlaybackRunning = false;
        AdvancePlayback(1d);
    }

    private void StopToolpath()
    {
        _playbackTimer.Stop();
        _playbackPosition = 0;
        IsPlaybackActive = false;
        IsPlaybackRunning = false;
        UpdatePlaybackProgress();
        PlaybackStatus = "Toolpath playback stopped.";
        RefreshPreview(rebuildToolpaths: false);
    }

    private void AdvancePlayback(double deltaSegments)
    {
        if (_playbackSegmentCount == 0)
        {
            _playbackTimer.Stop();
            IsPlaybackRunning = false;
            UpdatePlaybackStatus();
            return;
        }

        _playbackPosition = Math.Min(_playbackSegmentCount, _playbackPosition + Math.Max(deltaSegments, 0.1));
        if (_playbackPosition >= _playbackSegmentCount)
        {
            _playbackTimer.Stop();
            IsPlaybackRunning = false;
        }

        UpdatePlaybackProgress();
        RefreshPreview(rebuildToolpaths: false);
    }

    private void SeekPlayback(double progress)
    {
        PreparePlayback(rebuildToolpaths: false);
        if (_playbackSegmentCount == 0)
        {
            return;
        }

        _playbackTimer.Stop();
        IsPlaybackActive = true;
        IsPlaybackRunning = false;
        _playbackPosition = (Math.Clamp(progress, 0, 100) / 100d) * _playbackSegmentCount;
        UpdatePlaybackStatus();
        RefreshPreview(rebuildToolpaths: false);
    }

    private void PreparePlayback(bool rebuildToolpaths)
    {
        var machine = SelectedMachineProfile ?? ResolveSelectedMachine();
        if (machine is null)
        {
            _playbackSegmentCount = 0;
            return;
        }

        SyncActiveSetupAliases();
        CurrentJob.MachineProfileName = machine.Name;
        if (rebuildToolpaths || _previewToolpaths.Count == 0)
        {
            _previewToolpaths = _toolpathPlanner.Plan(machine, CurrentJob, ToolLibrary.ToList());
        }

        SyncPlaybackSegmentCount();
    }

    private void SyncPlaybackSegmentCount()
    {
        _playbackSegmentCount = GetActivePlaybackToolpaths()
            .Sum(CountPlaybackSegments);
        _playbackPosition = Math.Clamp(_playbackPosition, 0, _playbackSegmentCount);
        UpdatePlaybackProgress();
    }

    private IEnumerable<OperationToolpath> GetActivePlaybackToolpaths()
    {
        return _previewToolpaths
            .Where(toolpath => ReferenceEquals(toolpath.Setup, CurrentSetup) || CurrentSetup.Operations.Contains(toolpath.Operation));
    }

    private static int CountPlaybackSegments(OperationToolpath toolpath)
    {
        var count = 0;
        for (var index = 1; index < toolpath.Moves.Count; index++)
        {
            var start = toolpath.Moves[index - 1];
            var end = toolpath.Moves[index];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var dz = end.Z - start.Z;
            if ((dx * dx) + (dy * dy) + (dz * dz) >= 0.000001)
            {
                count++;
            }
        }

        return count;
    }

    private void UpdatePlaybackProgress()
    {
        _isUpdatingPlaybackProgress = true;
        ToolpathPlaybackProgress = _playbackSegmentCount <= 0
            ? 0
            : (_playbackPosition / _playbackSegmentCount) * 100d;
        _isUpdatingPlaybackProgress = false;
        UpdatePlaybackStatus();
    }

    private void UpdatePlaybackStatus()
    {
        if (_playbackSegmentCount <= 0)
        {
            PlaybackStatus = "No toolpath playback moves available.";
            return;
        }

        var state = IsPlaybackRunning
            ? "Playing"
            : IsPlaybackActive ? "Paused" : "Ready";
        PlaybackStatus = $"{state} move {_playbackPosition:0}/{_playbackSegmentCount} ({ToolpathPlaybackProgress:0.#}%).";
    }

    private void GenerateProgram()
    {
        var machine = SelectedMachineProfile ?? ResolveSelectedMachine();
        if (machine is null)
        {
            StatusMessage = "Add a machine profile before generating a program.";
            ToolpathDiagnostics = BuildToolpathDiagnostics(_previewToolpaths);
            return;
        }

        SyncActiveSetupAliases();
        CurrentJob.MachineProfileName = machine.Name;

        var toolpaths = _toolpathPlanner.Plan(machine, CurrentJob, ToolLibrary.ToList());
        var gcodeLines = _postProcessor.BuildProgram(machine, CurrentJob, toolpaths);
        var simulation = _simulationEngine.Simulate(CurrentJob, toolpaths);

        _previewToolpaths = toolpaths;
        GeneratedGCode = string.Join(Environment.NewLine, gcodeLines);
        SimulationSummary = simulation.Summary;
        SimulationBitmap = CreateBitmap(simulation);
        RefreshPreview(rebuildToolpaths: false);
        ToolpathDiagnostics = BuildToolpathDiagnostics(toolpaths);
        StatusMessage = BuildStatusMessage(machine, toolpaths, simulation);
        RefreshProjectSummary(toolpaths);
    }

    private IReadOnlyList<OperationToolpath> BuildPreviewToolpaths()
    {
        var machine = SelectedMachineProfile ?? ResolveSelectedMachine();
        if (machine is null)
        {
            return Array.Empty<OperationToolpath>();
        }

        SyncActiveSetupAliases();
        CurrentJob.MachineProfileName = machine.Name;
        return _toolpathPlanner.Plan(machine, CurrentJob, ToolLibrary.ToList());
    }

    private void RefreshProjectSummary(IReadOnlyList<OperationToolpath>? toolpaths = null)
    {
        var machine = SelectedMachineProfile ?? ResolveSelectedMachine();
        var allOperations = Setups.Sum(setup => setup.Operations.Count);
        var enabledOperations = Setups.Sum(setup => setup.Operations.Count(operation => operation.Enabled));
        var generatedMoves = toolpaths?.Sum(operation => operation.Moves.Count) ?? 0;
        var modelName = string.IsNullOrWhiteSpace(CurrentSetup.Part.SourcePath)
            ? CurrentSetup.Part.SourceType.ToString()
            : Path.GetFileName(CurrentSetup.Part.SourcePath);
        var stockDescription = CurrentSetup.Stock.Shape switch
        {
            StockShape.Cylinder => $"Cylinder D{CurrentSetup.Stock.Diameter:0.##} x {CurrentSetup.Stock.HeightZ:0.##}",
            StockShape.ImportedSolid => string.IsNullOrWhiteSpace(CurrentSetup.Stock.ImportedSolidPath)
                ? "Imported solid"
                : Path.GetFileName(CurrentSetup.Stock.ImportedSolidPath),
            _ => $"{CurrentSetup.Stock.LengthX:0.##} x {CurrentSetup.Stock.WidthY:0.##} x {CurrentSetup.Stock.HeightZ:0.##}"
        };

        ProjectSummary =
            $"{CurrentJob.Name}{Environment.NewLine}" +
            $"Machine: {(machine is null ? "None" : $"{machine.Name} ({machine.Kinematics})")}{Environment.NewLine}" +
            $"Setups: {Setups.Count} | Active: {CurrentSetup.Name}{Environment.NewLine}" +
            $"Model Source: {modelName}{Environment.NewLine}" +
            $"Model Orientation: X{CurrentSetup.Part.RotationA:0.###}, Y{CurrentSetup.Part.RotationB:0.###}, Z{CurrentSetup.Part.RotationC:0.###}{Environment.NewLine}" +
            $"Origin: X{CurrentSetup.WorkOrigin.X:0.###}, Y{CurrentSetup.WorkOrigin.Y:0.###}, Z{CurrentSetup.WorkOrigin.Z:0.###} | Axis: X{FormatAxisSign(CurrentSetup.FlipAxisX)} Y{FormatAxisSign(CurrentSetup.FlipAxisY)} Z{FormatAxisSign(CurrentSetup.FlipAxisZ)}{Environment.NewLine}" +
            $"Stock: {stockDescription}{Environment.NewLine}" +
            $"Tools: {ToolLibrary.Count} | Active Setup Ops: {ActiveOperations.Count} | Total Ops: {enabledOperations}/{allOperations} | Generated Moves: {generatedMoves}";
    }

    private string BuildToolpathDiagnostics(IReadOnlyList<OperationToolpath> toolpaths)
    {
        var machine = SelectedMachineProfile ?? ResolveSelectedMachine();
        var toolGroups = ToolLibrary.GroupBy(tool => tool.Number).ToList();
        var toolLookup = toolGroups.ToDictionary(group => group.Key, group => group.First());
        var duplicateToolNumbers = toolGroups
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(number => number)
            .ToList();
        var enabledOperationCount = Setups.Sum(setup => setup.Operations.Count(operation => operation.Enabled));
        var totalMoves = toolpaths.Sum(toolpath => toolpath.Moves.Count);
        var cuttingMoves = toolpaths.Sum(toolpath => toolpath.Moves.Count(move => move.IsCutting));
        var headerIssues = new List<ToolpathDiagnosticIssue>();
        var globalIssues = new List<ToolpathDiagnosticIssue>();
        var details = new StringBuilder();

        if (machine is null)
        {
            headerIssues.Add(new ToolpathDiagnosticIssue("ERROR", "No machine profile is selected, so the planner cannot produce a valid program."));
        }

        foreach (var duplicateToolNumber in duplicateToolNumbers)
        {
            headerIssues.Add(new ToolpathDiagnosticIssue("WARN", $"Tool number T{duplicateToolNumber} is duplicated. The first matching tool is used for diagnostics."));
        }

        if (enabledOperationCount == 0)
        {
            headerIssues.Add(new ToolpathDiagnosticIssue("WARN", "No enabled operations are available to plan."));
        }

        if (toolpaths.Count == 0 && enabledOperationCount > 0)
        {
            headerIssues.Add(new ToolpathDiagnosticIssue("WARN", "No planner results are available yet. Click Refresh 3D Preview or Generate Toolpaths after editing settings."));
        }

        AddMachineTravelDiagnostics(machine, toolpaths, headerIssues);
        foreach (var setup in Setups)
        {
            AddSetupDefinitionDiagnostics(setup, headerIssues);
            AddIndexedRotaryDiagnostics(machine, setup, headerIssues);
        }

        foreach (var setup in Setups)
        {
            var enabledOperations = setup.Operations.Where(operation => operation.Enabled).ToList();
            if (enabledOperations.Count == 0)
            {
                details.AppendLine($"{setup.Name}: no enabled operations.");
                details.AppendLine();
                continue;
            }

            var hasPriorEnabledOperation = false;
            foreach (var operation in enabledOperations)
            {
                var operationIssues = new List<ToolpathDiagnosticIssue>();
                toolLookup.TryGetValue(operation.ToolNumber, out var tool);
                AddOperationInputDiagnostics(setup, operation, tool, machine, hasPriorEnabledOperation, operationIssues);

                var plannedPaths = toolpaths
                    .Where(toolpath => ReferenceEquals(toolpath.Operation, operation))
                    .ToList();
                if (plannedPaths.Count == 0)
                {
                    operationIssues.Add(new ToolpathDiagnosticIssue("WARN", "No planner result is attached to this operation in the last preview/program run."));
                }

                details.AppendLine($"{setup.Name} / {operation.Name} ({operation.Type})");
                details.AppendLine($"  Tool: {FormatDiagnosticTool(tool, operation.ToolNumber)}");
                details.AppendLine($"  Feature: {FormatDiagnosticFeature(operation.Feature)}");

                foreach (var plannedPath in plannedPaths)
                {
                    AppendMoveDiagnostics(details, plannedPath, operationIssues);
                }

                if (operationIssues.Count == 0)
                {
                    details.AppendLine("  OK: inputs and generated moves look consistent.");
                }
                else
                {
                    foreach (var issue in operationIssues)
                    {
                        details.AppendLine($"  {issue.Severity}: {issue.Message}");
                    }
                }

                details.AppendLine();
                globalIssues.AddRange(operationIssues);
                hasPriorEnabledOperation = true;
            }
        }

        globalIssues.AddRange(headerIssues);
        var errorCount = globalIssues.Count(issue => issue.Severity == "ERROR");
        var warningCount = globalIssues.Count(issue => issue.Severity == "WARN");
        var builder = new StringBuilder();
        builder.AppendLine($"Diagnostics: {errorCount} error(s), {warningCount} warning(s)");
        builder.AppendLine($"Plans: {toolpaths.Count} | Moves: {totalMoves} | Cutting moves: {cuttingMoves}");
        builder.AppendLine($"Machine: {(machine is null ? "None" : machine.Name)} | Enabled ops: {enabledOperationCount}");
        builder.AppendLine("Refresh preview or generate toolpaths after changing operation values to update this report.");
        builder.AppendLine();
        if (headerIssues.Count > 0)
        {
            builder.AppendLine("Global checks:");
            foreach (var issue in headerIssues)
            {
                builder.AppendLine($"  {issue.Severity}: {issue.Message}");
            }

            builder.AppendLine();
        }

        builder.Append(details);
        return builder.ToString().TrimEnd();
    }

    private static void AddSetupDefinitionDiagnostics(JobSetup setup, List<ToolpathDiagnosticIssue> issues)
    {
        if (!double.IsFinite(setup.SafeZ) || !double.IsFinite(setup.ClearanceZ))
        {
            issues.Add(new ToolpathDiagnosticIssue("ERROR", $"{setup.Name}: setup safe/clearance Z contains an invalid number."));
        }

        if (setup.SafeZ <= setup.ClearanceZ + 0.0001)
        {
            issues.Add(new ToolpathDiagnosticIssue("WARN", $"{setup.Name}: setup Safe Z should be above Clearance Z."));
        }

        switch (setup.Stock.Shape)
        {
            case StockShape.Cylinder:
                if (setup.Stock.Diameter <= 0 || setup.Stock.HeightZ <= 0)
                {
                    issues.Add(new ToolpathDiagnosticIssue("WARN", $"{setup.Name}: cylinder stock needs positive diameter and height."));
                }

                break;
            case StockShape.Box:
                if (setup.Stock.LengthX <= 0 || setup.Stock.WidthY <= 0 || setup.Stock.HeightZ <= 0)
                {
                    issues.Add(new ToolpathDiagnosticIssue("WARN", $"{setup.Name}: box stock needs positive length, width, and height."));
                }

                break;
            case StockShape.ImportedSolid:
                if (string.IsNullOrWhiteSpace(setup.Stock.ImportedSolidPath))
                {
                    issues.Add(new ToolpathDiagnosticIssue("WARN", $"{setup.Name}: imported stock is selected but no stock file path is set."));
                }

                break;
        }
    }

    private static void AddMachineTravelDiagnostics(
        MachineProfile? machine,
        IReadOnlyList<OperationToolpath> toolpaths,
        List<ToolpathDiagnosticIssue> issues)
    {
        if (machine is null)
        {
            return;
        }

        if (machine.SpindleMinRpm > 0 && machine.SpindleMaxRpm > 0 && machine.SpindleMinRpm > machine.SpindleMaxRpm)
        {
            issues.Add(new ToolpathDiagnosticIssue("ERROR", $"{machine.Name}: spindle min RPM is greater than max RPM."));
        }

        var validMoves = toolpaths.SelectMany(toolpath => toolpath.Moves).Where(IsFiniteMove).ToList();
        if (validMoves.Count == 0)
        {
            return;
        }

        AddAxisSpanDiagnostic(machine, "X", validMoves.Min(move => move.X), validMoves.Max(move => move.X), issues);
        AddAxisSpanDiagnostic(machine, "Y", validMoves.Min(move => move.Y), validMoves.Max(move => move.Y), issues);
        AddAxisSpanDiagnostic(machine, "Z", validMoves.Min(move => move.Z), validMoves.Max(move => move.Z), issues);
    }

    private static void AddIndexedRotaryDiagnostics(
        MachineProfile? machine,
        JobSetup setup,
        List<ToolpathDiagnosticIssue> issues)
    {
        var indexedOperations = setup.Operations.Where(HasOperationRotaryIndex).ToList();
        if (indexedOperations.Count == 0 || machine is null)
        {
            return;
        }

        if (!SupportsIndexedRotary(machine))
        {
            issues.Add(new ToolpathDiagnosticIssue("WARN", $"{setup.Name}: operation rotary indexing is preview-only because {machine.Name} is not indexed rotary capable."));
            return;
        }

        var rotaryAxes = machine.Axes
            .Where(axis => axis.Type == AxisType.Rotary && IsSupportedRotaryAxisName(axis.Name))
            .ToList();
        if (rotaryAxes.Count == 0)
        {
            issues.Add(new ToolpathDiagnosticIssue("ERROR", $"{setup.Name}: indexed rotary motion is requested, but {machine.Name} has no A/B rotary axis."));
            return;
        }

        if (machine.Kinematics is KinematicsMode.FourAxisIndexedMill or KinematicsMode.ThreePlusOneMill && rotaryAxes.Count > 1)
        {
            issues.Add(new ToolpathDiagnosticIssue("WARN", $"{machine.Name}: {machine.Kinematics} posts only the first configured rotary axis for setup indexing."));
        }

        foreach (var axis in rotaryAxes)
        {
            foreach (var operation in indexedOperations)
            {
                AddRotaryTravelDiagnostic($"{setup.Name}/{operation.Name}", axis, GetOperationRotaryAngle(operation, axis.Name), issues);
            }
        }

        foreach (var operation in indexedOperations)
        {
            WarnForUnpostableOperationRotation(machine, setup, operation, rotaryAxes, issues);
        }
    }

    private static void AddRotaryTravelDiagnostic(string label, MachineAxis axis, double angle, List<ToolpathDiagnosticIssue> issues)
    {
        var travelSpan = Math.Abs(axis.TravelMax - axis.TravelMin);
        if (Math.Abs(angle) > 0.0001
            && travelSpan > 0.0001
            && (angle < Math.Min(axis.TravelMin, axis.TravelMax) - 0.0001 || angle > Math.Max(axis.TravelMin, axis.TravelMax) + 0.0001))
        {
            issues.Add(new ToolpathDiagnosticIssue("WARN", $"{label}: {NormalizeRotaryAxisName(axis.Name)} index {angle:0.###} is outside configured rotary travel {axis.TravelMin:0.###}..{axis.TravelMax:0.###}."));
        }
    }

    private static void WarnForUnpostableOperationRotation(
        MachineProfile machine,
        JobSetup setup,
        ToolpathOperationDefinition operation,
        IReadOnlyList<MachineAxis> rotaryAxes,
        List<ToolpathDiagnosticIssue> issues)
    {
        var configuredAxisNames = rotaryAxes.Select(axis => NormalizeRotaryAxisName(axis.Name)).ToHashSet();
        if (Math.Abs(operation.RotaryIndexA) > 0.0001 && !configuredAxisNames.Contains("A"))
        {
            issues.Add(new ToolpathDiagnosticIssue("WARN", $"{setup.Name}/{operation.Name}: A operation index is set, but no A rotary axis is configured for posting."));
        }

        if (Math.Abs(operation.RotaryIndexB) > 0.0001 && !configuredAxisNames.Contains("B"))
        {
            issues.Add(new ToolpathDiagnosticIssue("WARN", $"{setup.Name}/{operation.Name}: B operation index is set, but no B rotary axis is configured for posting."));
        }

        if (machine.Kinematics is KinematicsMode.FourAxisIndexedMill or KinematicsMode.ThreePlusOneMill && rotaryAxes.Count > 0)
        {
            var postedAxis = NormalizeRotaryAxisName(rotaryAxes[0].Name);
            var unpostedAngles = new[]
            {
                ("A", operation.RotaryIndexA),
                ("B", operation.RotaryIndexB)
            }
                .Where(axis => axis.Item1 != postedAxis && Math.Abs(axis.Item2) > 0.0001)
                .Select(axis => $"{axis.Item1}{axis.Item2:0.###}")
                .ToList();
            if (unpostedAngles.Count > 0)
            {
                issues.Add(new ToolpathDiagnosticIssue("WARN", $"{setup.Name}/{operation.Name}: {machine.Kinematics} will post only {postedAxis}; unposted operation index offsets: {string.Join(", ", unpostedAngles)}."));
            }
        }
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

    private bool IsRotaryAxisExposed(string axisName)
    {
        var normalizedName = NormalizeRotaryAxisName(axisName);
        return GetExposedRotaryAxisNames(SelectedMachineProfile)
            .Any(name => string.Equals(name, normalizedName, StringComparison.OrdinalIgnoreCase));
    }

    private string FormatRotaryAxisLabel(string axisName)
    {
        var normalizedName = NormalizeRotaryAxisName(axisName);
        var axis = SelectedMachineProfile?.Axes.FirstOrDefault(candidate =>
            candidate.Type == AxisType.Rotary
            && string.Equals(NormalizeRotaryAxisName(candidate.Name), normalizedName, StringComparison.OrdinalIgnoreCase));
        return axis is null
            ? normalizedName
            : $"{normalizedName} (around {axis.RotatesAround})";
    }

    private static IEnumerable<string> GetExposedRotaryAxisNames(MachineProfile? machine)
    {
        if (machine is null || !SupportsIndexedRotary(machine))
        {
            yield break;
        }

        var configuredNames = machine.Axes
            .Where(axis => axis.Type == AxisType.Rotary && IsSupportedRotaryAxisName(axis.Name))
            .Select(axis => NormalizeRotaryAxisName(axis.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (configuredNames.Count == 0)
        {
            yield break;
        }

        if (!AllowsMultipleIndexedRotaryAxes(machine))
        {
            yield return configuredNames.Contains("A", StringComparer.OrdinalIgnoreCase)
                ? "A"
                : configuredNames[0];
            yield break;
        }

        if (configuredNames.Contains("A", StringComparer.OrdinalIgnoreCase))
        {
            yield return "A";
        }

        if (configuredNames.Contains("B", StringComparer.OrdinalIgnoreCase))
        {
            yield return "B";
        }
    }

    private static bool NormalizeMachineRotaryAxesForKinematics(MachineProfile machine)
    {
        var changed = false;
        for (var index = machine.Axes.Count - 1; index >= 0; index--)
        {
            var axis = machine.Axes[index];
            if (axis.Type == AxisType.Rotary && NormalizeRotaryAxisName(axis.Name) == "C")
            {
                machine.Axes.RemoveAt(index);
                changed = true;
            }
        }

        if (!SupportsIndexedRotary(machine) || AllowsMultipleIndexedRotaryAxes(machine))
        {
            return changed;
        }

        var configuredNames = machine.Axes
            .Where(axis => axis.Type == AxisType.Rotary && IsSupportedRotaryAxisName(axis.Name))
            .Select(axis => NormalizeRotaryAxisName(axis.Name))
            .ToList();
        if (configuredNames.Count <= 1)
        {
            return changed;
        }

        var keepName = configuredNames.Contains("A", StringComparer.OrdinalIgnoreCase)
            ? "A"
            : configuredNames[0];
        var keptAxis = false;
        for (var index = 0; index < machine.Axes.Count; index++)
        {
            var axis = machine.Axes[index];
            if (axis.Type != AxisType.Rotary || !IsSupportedRotaryAxisName(axis.Name))
            {
                continue;
            }

            var axisName = NormalizeRotaryAxisName(axis.Name);
            if (!keptAxis && string.Equals(axisName, keepName, StringComparison.OrdinalIgnoreCase))
            {
                keptAxis = true;
                continue;
            }

            machine.Axes.RemoveAt(index);
            index--;
            changed = true;
        }

        return changed;
    }

    private void ClearUnsupportedRotaryIndexes()
    {
        var exposedAxes = GetExposedRotaryAxisNames(SelectedMachineProfile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var setup in Setups)
        {
            if (!exposedAxes.Contains("A"))
            {
                foreach (var operation in setup.Operations)
                {
                    operation.RotaryIndexA = 0;
                }
            }

            if (!exposedAxes.Contains("B"))
            {
                foreach (var operation in setup.Operations)
                {
                    operation.RotaryIndexB = 0;
                }
            }

            foreach (var operation in setup.Operations)
            {
                operation.RotaryIndexC = 0;
            }
        }
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

    private static double GetOperationRotaryAngle(ToolpathOperationDefinition operation, string? axisName)
    {
        return NormalizeRotaryAxisName(axisName) switch
        {
            "A" => operation.RotaryIndexA,
            "B" => operation.RotaryIndexB,
            _ => 0
        };
    }

    private static void AddAxisSpanDiagnostic(
        MachineProfile machine,
        string axisName,
        double minProgramValue,
        double maxProgramValue,
        List<ToolpathDiagnosticIssue> issues)
    {
        var axis = machine.Axes.FirstOrDefault(candidate =>
            candidate.Type == AxisType.Linear && string.Equals(candidate.Name, axisName, StringComparison.OrdinalIgnoreCase));
        if (axis is null)
        {
            issues.Add(new ToolpathDiagnosticIssue("WARN", $"{machine.Name}: no configured {axisName} linear axis to compare against the planned program envelope."));
            return;
        }

        var programSpan = Math.Abs(maxProgramValue - minProgramValue);
        var travelSpan = Math.Abs(axis.TravelMax - axis.TravelMin);
        if (travelSpan <= 0)
        {
            issues.Add(new ToolpathDiagnosticIssue("WARN", $"{machine.Name}: {axisName} travel span is zero or negative."));
            return;
        }

        if (programSpan > travelSpan + 0.001)
        {
            issues.Add(new ToolpathDiagnosticIssue("WARN", $"{axisName} program span {programSpan:0.###} exceeds configured travel span {travelSpan:0.###}."));
        }
        else
        {
            issues.Add(new ToolpathDiagnosticIssue("INFO", $"{axisName} program range {minProgramValue:0.###}..{maxProgramValue:0.###}, span {programSpan:0.###} of {travelSpan:0.###} configured travel."));
        }
    }

    private static void AddOperationInputDiagnostics(
        JobSetup setup,
        ToolpathOperationDefinition operation,
        ToolDefinition? tool,
        MachineProfile? machine,
        bool hasPriorEnabledOperation,
        List<ToolpathDiagnosticIssue> issues)
    {
        if (tool is null)
        {
            issues.Add(new ToolpathDiagnosticIssue("ERROR", $"Tool T{operation.ToolNumber} does not exist in the tool library."));
        }
        else
        {
            if (tool.CuttingDiameter <= 0)
            {
                issues.Add(new ToolpathDiagnosticIssue("ERROR", $"Tool T{tool.Number} has a non-positive cutting diameter."));
            }

            if (tool.FluteLength <= 0)
            {
                issues.Add(new ToolpathDiagnosticIssue("WARN", $"Tool T{tool.Number} has no flute length, so cutting engagement cannot be checked yet."));
            }

            if (tool.FluteCount <= 0)
            {
                issues.Add(new ToolpathDiagnosticIssue("WARN", $"Tool T{tool.Number} has no flute count."));
            }

            if (tool.CuttingLength > 0 && operation.Feature.Depth > tool.CuttingLength + 0.001 && operation.Type is not OperationType.Facing)
            {
                issues.Add(new ToolpathDiagnosticIssue("WARN", $"Feature depth {operation.Feature.Depth:0.###} exceeds tool cutting length {tool.CuttingLength:0.###}."));
            }
        }

        if (operation.SpindleSpeed <= 0)
        {
            issues.Add(new ToolpathDiagnosticIssue("WARN", "Spindle speed is zero or negative."));
        }

        if (operation.FeedRate <= 0)
        {
            issues.Add(new ToolpathDiagnosticIssue("WARN", "Feed rate is zero or negative."));
        }

        if (operation.PlungeRate <= 0)
        {
            issues.Add(new ToolpathDiagnosticIssue("WARN", "Plunge rate is zero or negative."));
        }

        if (machine is not null)
        {
            if (machine.SpindleMinRpm > 0 && operation.SpindleSpeed > 0 && operation.SpindleSpeed < machine.SpindleMinRpm)
            {
                issues.Add(new ToolpathDiagnosticIssue("WARN", $"Spindle speed {operation.SpindleSpeed:0.###} is below machine minimum {machine.SpindleMinRpm:0.###}."));
            }

            if (machine.SpindleMaxRpm > 0 && operation.SpindleSpeed > machine.SpindleMaxRpm)
            {
                issues.Add(new ToolpathDiagnosticIssue("WARN", $"Spindle speed {operation.SpindleSpeed:0.###} is above machine maximum {machine.SpindleMaxRpm:0.###}."));
            }

            var slowestLinearFeed = machine.Axes
                .Where(axis => axis.Type == AxisType.Linear && axis.MaxFeedRate > 0)
                .Select(axis => axis.MaxFeedRate)
                .DefaultIfEmpty(0)
                .Min();
            if (slowestLinearFeed > 0 && operation.FeedRate > slowestLinearFeed)
            {
                issues.Add(new ToolpathDiagnosticIssue("WARN", $"Feed rate {operation.FeedRate:0.###} exceeds the slowest configured linear axis feed {slowestLinearFeed:0.###}."));
            }

            var zAxis = machine.Axes.FirstOrDefault(axis => axis.Type == AxisType.Linear && string.Equals(axis.Name, "Z", StringComparison.OrdinalIgnoreCase));
            if (zAxis?.MaxFeedRate > 0 && operation.PlungeRate > zAxis.MaxFeedRate)
            {
                issues.Add(new ToolpathDiagnosticIssue("WARN", $"Plunge rate {operation.PlungeRate:0.###} exceeds configured Z max feed {zAxis.MaxFeedRate:0.###}."));
            }
        }

        var effectiveSafeZ = Math.Max(setup.SafeZ, operation.SafeRetractZ);
        if (effectiveSafeZ <= setup.ClearanceZ + 0.0001)
        {
            issues.Add(new ToolpathDiagnosticIssue("WARN", "Effective operation Safe Z should be above setup Clearance Z."));
        }

        if (RequiresStepDown(operation.Type) && operation.StepDown <= 0)
        {
            issues.Add(new ToolpathDiagnosticIssue("ERROR", "StepDown must be greater than zero for this operation."));
        }

        if (RequiresStepOver(operation.Type) && operation.StepOver <= 0)
        {
            issues.Add(new ToolpathDiagnosticIssue("ERROR", "StepOver must be greater than zero for this operation."));
        }

        if (tool is not null && RequiresStepOver(operation.Type) && operation.StepOver > tool.CuttingDiameter)
        {
            issues.Add(new ToolpathDiagnosticIssue("WARN", $"StepOver {operation.StepOver:0.###} is larger than tool diameter {tool.CuttingDiameter:0.###}; this can leave uncut stock."));
        }

        if (operation.SafeRetractZ <= operation.Feature.StartZ && operation.Type != OperationType.Drill)
        {
            issues.Add(new ToolpathDiagnosticIssue("WARN", "Safe retract is at or below the feature start Z."));
        }

        if (operation.UseRestMachining && !hasPriorEnabledOperation)
        {
            issues.Add(new ToolpathDiagnosticIssue("WARN", "REST is enabled but there is no prior enabled operation in this setup."));
        }

        if (RequiresPositiveDepth(operation.Type) && operation.Feature.Depth <= 0)
        {
            issues.Add(new ToolpathDiagnosticIssue("ERROR", "Feature depth must be greater than zero for this operation."));
        }

        if (operation.Type == OperationType.Chamfer && operation.Feature.Shape != FeatureShape.EdgePath)
        {
            issues.Add(new ToolpathDiagnosticIssue("ERROR", "Chamfer requires a selected model edge/path."));
        }

        if (RequiresEdgePath(operation.Type, operation.Feature.Shape) && operation.Feature.PathPoints.Count < 2)
        {
            issues.Add(new ToolpathDiagnosticIssue("WARN", "This operation needs a selected model edge/path with at least two points."));
        }

        if (operation.Feature.Shape == FeatureShape.Rectangle
            && (operation.Type is OperationType.Pocket or OperationType.Profile or OperationType.Contour2D)
            && (operation.Feature.Length <= 0 || operation.Feature.Width <= 0))
        {
            issues.Add(new ToolpathDiagnosticIssue("ERROR", "Rectangle feature length and width must be greater than zero."));
        }

        if (tool is not null
            && operation.Feature.Shape == FeatureShape.Circle
            && operation.Feature.InsideProfile
            && (operation.Type is OperationType.Pocket or OperationType.Profile or OperationType.Contour2D)
            && operation.Feature.Diameter <= tool.CuttingDiameter + (Math.Max(0, operation.FinishStockRadial) * 2d))
        {
            issues.Add(new ToolpathDiagnosticIssue("ERROR", $"{operation.Type} circle diameter is too small for the selected tool and radial stock allowance."));
        }

        if (operation.Type is OperationType.Drill or OperationType.Boring)
        {
            if (operation.Feature.Diameter <= 0)
            {
                issues.Add(new ToolpathDiagnosticIssue("WARN", $"{operation.Type} feature diameter is zero or negative; select a circular model edge or enter a diameter."));
            }

            if (tool is not null && operation.Feature.Diameter > 0 && operation.Feature.Diameter <= tool.CuttingDiameter + (Math.Max(0, operation.FinishStockRadial) * 2d))
            {
                issues.Add(new ToolpathDiagnosticIssue("WARN", $"{operation.Type} diameter {operation.Feature.Diameter:0.###} is not larger than tool diameter plus radial stock allowance."));
            }
        }

        if (operation.Type == OperationType.Boring && operation.Feature.Shape is not (FeatureShape.Circle or FeatureShape.HolePattern))
        {
            issues.Add(new ToolpathDiagnosticIssue("ERROR", "Boring requires a circular feature or hole pattern."));
        }

        if (operation.Type == OperationType.Drill)
        {
            if (operation.DrillPeckDepth < 0)
            {
                issues.Add(new ToolpathDiagnosticIssue("ERROR", "Peck depth cannot be negative."));
            }

            if (operation.DrillRetractDistance < 0)
            {
                issues.Add(new ToolpathDiagnosticIssue("ERROR", "Peck retract distance cannot be negative."));
            }
        }
    }

    private static void AppendMoveDiagnostics(
        StringBuilder builder,
        OperationToolpath plannedPath,
        List<ToolpathDiagnosticIssue> issues)
    {
        if (plannedPath.Moves.Count == 0)
        {
            var severity = plannedPath.Summary.StartsWith("Skipped ", StringComparison.OrdinalIgnoreCase)
                ? "ERROR"
                : "WARN";
            issues.Add(new ToolpathDiagnosticIssue(severity, string.IsNullOrWhiteSpace(plannedPath.Summary)
                ? "Planner returned zero moves."
                : plannedPath.Summary));
            return;
        }

        var invalidMoves = plannedPath.Moves.Count(move => !IsFiniteMove(move));
        if (invalidMoves > 0)
        {
            issues.Add(new ToolpathDiagnosticIssue("ERROR", $"{invalidMoves} move(s) contain NaN or infinite coordinates/feed values."));
        }

        var cutMoves = plannedPath.Moves.Where(move => move.IsCutting && IsFiniteMove(move)).ToList();
        if (cutMoves.Count == 0)
        {
            issues.Add(new ToolpathDiagnosticIssue("WARN", "Planner produced moves, but none are marked as cutting moves."));
        }

        var cutDistance = CalculateCuttingDistance(plannedPath.Moves);
        var rangeText = FormatMoveRange(plannedPath.Moves);
        builder.AppendLine($"  Moves: {plannedPath.Moves.Count} total, {cutMoves.Count} cutting, {cutDistance:0.###} mm cut distance");
        builder.AppendLine($"  Range: {rangeText}");
        if (!string.IsNullOrWhiteSpace(plannedPath.Summary))
        {
            builder.AppendLine($"  Summary: {plannedPath.Summary}");
        }

        if (cutMoves.Any(move => move.FeedRate <= 0))
        {
            issues.Add(new ToolpathDiagnosticIssue("WARN", "One or more cutting moves have a zero or negative feed rate."));
        }

        if (cutMoves.Count > 0 && plannedPath.Tool.FluteLength > 0)
        {
            var minCutZ = cutMoves.Min(move => move.Z);
            var maxCutZ = cutMoves.Max(move => move.Z);
            var axialEngagement = Math.Max(0, maxCutZ - minCutZ);
            if (axialEngagement > plannedPath.Tool.FluteLength + 0.001)
            {
                issues.Add(new ToolpathDiagnosticIssue("WARN", $"Cutting Z span {axialEngagement:0.###} exceeds flute length {plannedPath.Tool.FluteLength:0.###}."));
            }
        }
    }

    private static bool RequiresStepDown(OperationType type)
    {
        return type is not OperationType.Drill;
    }

    private static bool RequiresStepOver(OperationType type)
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

    private static bool RequiresPositiveDepth(OperationType type)
    {
        return type is OperationType.Facing
            or OperationType.BulkRemoval
            or OperationType.Raster
            or OperationType.AdaptiveClearing
            or OperationType.Pocket
            or OperationType.Profile
            or OperationType.Contour2D
            or OperationType.Chamfer
            or OperationType.Boring
            or OperationType.Drill;
    }

    private static bool RequiresEdgePath(OperationType type, FeatureShape shape)
    {
        return type == OperationType.Chamfer
            || (shape == FeatureShape.EdgePath && type is OperationType.Profile or OperationType.Contour2D);
    }

    private static string FormatDiagnosticTool(ToolDefinition? tool, int requestedToolNumber)
    {
        return tool is null
            ? $"T{requestedToolNumber} missing"
            : $"T{tool.Number} {tool.Name} ({tool.Style}, D{tool.CuttingDiameter:0.###}, flutes {tool.FluteCount}, flute length {tool.FluteLength:0.###})";
    }

    private static string FormatDiagnosticFeature(FeatureDefinition feature)
    {
        return $"{feature.Shape}, center X{feature.CenterX:0.###} Y{feature.CenterY:0.###}, start Z{feature.StartZ:0.###}, depth {feature.Depth:0.###}, size {feature.Length:0.###} x {feature.Width:0.###}, dia {feature.Diameter:0.###}";
    }

    private static string FormatMoveRange(IReadOnlyList<ToolpathMove> moves)
    {
        var validMoves = moves.Where(IsFiniteMove).ToList();
        if (validMoves.Count == 0)
        {
            return "no finite coordinates";
        }

        return
            $"X{validMoves.Min(move => move.X):0.###}..{validMoves.Max(move => move.X):0.###}, " +
            $"Y{validMoves.Min(move => move.Y):0.###}..{validMoves.Max(move => move.Y):0.###}, " +
            $"Z{validMoves.Min(move => move.Z):0.###}..{validMoves.Max(move => move.Z):0.###}";
    }

    private static double CalculateCuttingDistance(IReadOnlyList<ToolpathMove> moves)
    {
        double total = 0;
        for (var index = 1; index < moves.Count; index++)
        {
            if (!moves[index].IsCutting || !IsFiniteMove(moves[index]) || !IsFiniteMove(moves[index - 1]))
            {
                continue;
            }

            total += Math.Sqrt(
                Math.Pow(moves[index].X - moves[index - 1].X, 2) +
                Math.Pow(moves[index].Y - moves[index - 1].Y, 2) +
                Math.Pow(moves[index].Z - moves[index - 1].Z, 2));
        }

        return total;
    }

    private static bool IsFiniteMove(ToolpathMove move)
    {
        return double.IsFinite(move.X)
            && double.IsFinite(move.Y)
            && double.IsFinite(move.Z)
            && double.IsFinite(move.FeedRate);
    }

    private void HookCollections()
    {
        MachineProfiles.CollectionChanged += OnCollectionChanged;
        ToolLibrary.CollectionChanged += OnCollectionChanged;
        Setups.CollectionChanged += OnSetupsChanged;
        HookActiveOperationsCollection();
    }

    private void HookActiveOperationsCollection()
    {
        if (_hookedOperations is not null)
        {
            _hookedOperations.CollectionChanged -= OnCollectionChanged;
        }

        _hookedOperations = ActiveOperations;
        _hookedOperations.CollectionChanged += OnCollectionChanged;
    }

    private void HookSelectedOperationNotifications(ToolpathOperationDefinition? operation)
    {
        if (_hookedSelectedOperation is not null)
        {
            _hookedSelectedOperation.PropertyChanged -= OnSelectedOperationPropertyChanged;
        }

        _hookedSelectedOperation = operation;
        if (_hookedSelectedOperation is not null)
        {
            _hookedSelectedOperation.PropertyChanged += OnSelectedOperationPropertyChanged;
        }
    }

    private void OnSelectedOperationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName == nameof(ToolpathOperationDefinition.Type))
        {
            NotifyOperationSpecificUiProperties();
        }
    }

    private void HookSelectedMachineProfileNotifications(MachineProfile? machine)
    {
        if (_hookedSelectedMachineProfile is not null)
        {
            _hookedSelectedMachineProfile.Axes.CollectionChanged -= OnSelectedMachineAxesChanged;
            foreach (var axis in _hookedSelectedMachineProfile.Axes)
            {
                axis.PropertyChanged -= OnSelectedMachineAxisPropertyChanged;
            }
        }

        _hookedSelectedMachineProfile = machine;
        if (_hookedSelectedMachineProfile is null)
        {
            return;
        }

        _hookedSelectedMachineProfile.Axes.CollectionChanged += OnSelectedMachineAxesChanged;
        foreach (var axis in _hookedSelectedMachineProfile.Axes)
        {
            axis.PropertyChanged += OnSelectedMachineAxisPropertyChanged;
        }
    }

    private void OnSelectedMachineAxesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (MachineAxis axis in e.OldItems)
            {
                axis.PropertyChanged -= OnSelectedMachineAxisPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (MachineAxis axis in e.NewItems)
            {
                axis.PropertyChanged += OnSelectedMachineAxisPropertyChanged;
            }
        }

        RefreshRotaryAxisConfiguration();
    }

    private void OnSelectedMachineAxisPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName is nameof(MachineAxis.Name)
            or nameof(MachineAxis.Type)
            or nameof(MachineAxis.RotatesAround)
            or nameof(MachineAxis.Mount)
            or nameof(MachineAxis.PivotOffsetX)
            or nameof(MachineAxis.PivotOffsetY)
            or nameof(MachineAxis.PivotOffsetZ)
            or nameof(MachineAxis.ZeroOffset))
        {
            RefreshRotaryAxisConfiguration();
        }
    }

    private void RefreshRotaryAxisConfiguration()
    {
        if (SelectedMachineProfile is not null)
        {
            NormalizeMachineRotaryAxesForKinematics(SelectedMachineProfile);
        }

        ClearUnsupportedRotaryIndexes();
        NotifyRotaryUiProperties();
        RefreshProjectSummary();
        ToolpathDiagnostics = BuildToolpathDiagnostics(_previewToolpaths);
        if (!_suppressPreviewRefresh)
        {
            RefreshPreview();
        }
    }

    private void NotifyOperationSpecificUiProperties()
    {
        OnPropertyChanged(nameof(OperationStepSettingsVisibility));
        OnPropertyChanged(nameof(FinishStockSettingsVisibility));
        OnPropertyChanged(nameof(LeadSettingsVisibility));
        OnPropertyChanged(nameof(TabSettingsVisibility));
        OnPropertyChanged(nameof(ClimbToggleVisibility));
        OnPropertyChanged(nameof(RestToggleVisibility));
        OnPropertyChanged(nameof(FeatureSizeVisibility));
        OnPropertyChanged(nameof(FeatureDiameterVisibility));
        OnPropertyChanged(nameof(InsideProfileVisibility));
        OnPropertyChanged(nameof(FeatureDiameterSideVisibility));
        OnPropertyChanged(nameof(DrillSettingsVisibility));
        OnPropertyChanged(nameof(PatternSettingsVisibility));
        OnPropertyChanged(nameof(RotationSettingsVisibility));
        NotifyRotaryUiProperties();
    }

    private void NotifyRotaryUiProperties()
    {
        OnPropertyChanged(nameof(IndexedRotarySettingsVisibility));
        OnPropertyChanged(nameof(RotaryAIndexVisibility));
        OnPropertyChanged(nameof(RotaryBIndexVisibility));
        OnPropertyChanged(nameof(RotaryIndexLabel));
    }

    private bool IsSelectedOperationType(params OperationType[] operationTypes)
    {
        return SelectedOperation is not null && operationTypes.Contains(SelectedOperation.Type);
    }

    private void OnSetupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!Setups.Contains(CurrentSetup))
        {
            SelectedSetup = Setups.FirstOrDefault();
        }

        OnPropertyChanged(nameof(Setups));
        OnPropertyChanged(nameof(CurrentSetup));
        OnPropertyChanged(nameof(ActiveOperations));
        RaiseCommandStates();
        RefreshProjectSummary();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        RaiseCommandStates();
        RefreshProjectSummary();
    }

    private MachineProfile? ResolveSelectedMachine()
    {
        return MachineProfiles.FirstOrDefault(machine => machine.Name == CurrentJob.MachineProfileName)
            ?? MachineProfiles.FirstOrDefault();
    }

    private void SyncActiveSetupAliases()
    {
        if (CurrentJob.Setups.Count == 0)
        {
            CurrentJob.Setup.Name = "Setup 1";
            CurrentJob.Setup.TransferRestFromPreviousSetup = false;
            CurrentJob.Setups.Add(CurrentJob.Setup);
        }

        if (_selectedSetup is null || !CurrentJob.Setups.Contains(_selectedSetup))
        {
            _selectedSetup = CurrentJob.Setups[0];
        }

        CurrentJob.Setup = _selectedSetup;
        CurrentJob.Operations = _selectedSetup.Operations;
    }

    private void RaiseCommandStates()
    {
        (RemoveMachineProfileCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (ConfigureIndexedFourthAxisCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (RemoveToolCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (RemoveSetupCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (RemoveOperationCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private static string BuildStatusMessage(
        MachineProfile machine,
        IReadOnlyList<OperationToolpath> toolpaths,
        SimulationReport simulation)
    {
        var generatedOperations = toolpaths.Count(path => path.Moves.Count > 0);
        var totalMoves = toolpaths.Sum(path => path.Moves.Count);
        var setupCount = toolpaths.Select(path => path.Setup).Distinct().Count();
        return $"Generated {generatedOperations} operations across {setupCount} setup(s) for {machine.Name} with {totalMoves} motion blocks. {simulation.Summary}";
    }

    private static string FormatAxisSign(bool flipped) => flipped ? "-" : "+";

    private static BitmapSource? CreateBitmap(SimulationReport simulation)
    {
        if (simulation.Width <= 0 || simulation.Height <= 0 || simulation.PixelData.Length == 0)
        {
            return null;
        }

        var bitmap = BitmapSource.Create(
            simulation.Width,
            simulation.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            simulation.PixelData,
            simulation.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static PartSourceType ResolvePartSourceType(string? path, PartSourceType fallback = PartSourceType.StepFile)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return fallback;
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".stl" or ".obj" => PartSourceType.ImportedSolid,
            ".step" or ".stp" => PartSourceType.StepFile,
            _ => fallback
        };
    }

    private readonly record struct ToolpathDiagnosticIssue(string Severity, string Message);
}
