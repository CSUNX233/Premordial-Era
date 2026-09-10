using Godot;
using NativeEpoch.Simulation;
using NumericsVector2 = System.Numerics.Vector2;

namespace NativeEpoch.Godot;

public sealed partial class Stage3Main : Node3D
{
    private const ulong DefaultSeed = 20260908;
    private ulong _worldSeed = DefaultSeed;
    private const double FixedDelta = 0.1;
    private const int MaximumStepsPerFrame = 512;
    private SimulationWorld _world = null!;
    private Task<int>? _simulationBatch;
    private bool _synchronousDiagnostic;
    private bool _simulationFaulted;
    private ObservationTerrain _observationTerrain = null!;
    private WorldPresentationSnapshot _snapshot = null!;
    private LowPolyWorldRenderer _worldRenderer = null!;
    private Stage3Hud _hud = null!;
    private Camera3D _camera = null!;
    private FunctionCatalogue _catalogue = null!;
    private FunctionCataloguePanel _cataloguePanel = null!;
    private bool _pausedBeforeCatalogue;
    private double _catalogueObserveElapsed, _catalogueSaveElapsed;
    private Vector3 _cameraTarget = Vector3.Zero;
    private Basis _cameraSurfaceFrame = Basis.Identity;
    private float _cameraYaw = -0.55f;
    private float _cameraPitch = -0.82f;
    private float _cameraDistance = 410f;
    private bool _rightDragging;
    private bool _paused;
    private double _timeScale = 1.0;
    private double _stepAccumulator;
    private double _renderAccumulator;
    private double _resourceRenderAccumulator = 1.0;
    private double _poseInterpolationElapsed;
    private double _statisticsAccumulator;
    private double _inspectorAccumulator;
    private double _rateWindowSeconds;
    private long _rateWindowSteps;
    private double _achievedScale;
    private int _renderedRegions;
    private ulong? _selectedId;
    private ulong? _trackedId;
    private Vector3 _trackingTarget;
    private ToolMode _toolMode = ToolMode.Select;
    private HeatmapMode _heatmapMode = HeatmapMode.Natural;
    private float _brushRadius = 28f;

    public override void _Ready()
    {
        _synchronousDiagnostic = OS.GetCmdlineUserArgs().Contains("--stage3-profile-sync");
        GetWindow().Title = "原生纪 · 微缩星球观察台";
        BuildSceneLighting();

        _worldRenderer = new LowPolyWorldRenderer { Name = "LowPolyWorldRenderer" };
        AddChild(_worldRenderer);

        _camera = new Camera3D
        {
            Name = "ObservationCamera",
            Current = true,
            Far = 2200f,
            Fov = 55f,
            Near = 0.03f
        };
        AddChild(_camera);

        _hud = new Stage3Hud { Name = "Stage3Hud" };
        AddChild(_hud);
        bool diagnosticRun = OS.GetCmdlineUserArgs().Any(argument =>
            argument.StartsWith("--stage3-", StringComparison.Ordinal) ||
            argument.StartsWith("--capture-", StringComparison.Ordinal));
        _catalogue = new FunctionCatalogue(!diagnosticRun, !diagnosticRun);
        _cataloguePanel = new FunctionCataloguePanel { Name = "FunctionCatalogue" };
        _cataloguePanel.Initialize(_catalogue);
        _cataloguePanel.CanFocus = CanFocusCatalogueEntry;
        _cataloguePanel.FocusRequested += entry => SelectAndFocus(entry.RepresentativeId);
        _hud.AddChild(_cataloguePanel);
        _cataloguePanel.VisibilityChanged += () =>
        {
            if (_cataloguePanel.Visible)
            {
                CompleteSimulationBatch(wait: true);
                _pausedBeforeCatalogue = _paused;
                _paused = true;
                _rightDragging = false;
            }
            else _paused = _pausedBeforeCatalogue || _simulationFaulted;
            _hud.SetPaused(_paused);
            UpdateModeLabel();
        };
        ConnectHudCommands();
        ResetWorld(24, preRunSteps: 0, "少量祖先 24");
        UpdateCameraTransform();
        if (OS.GetCmdlineUserArgs().Contains("--stage3-profile-small"))
            RunStage2PerformanceProfile(24, 0);
        else if (OS.GetCmdlineUserArgs().Contains("--stage3-profile-1000"))
            RunStage2PerformanceProfile(1000, 80);
        else if (OS.GetCmdlineUserArgs().Contains("--stage3-profile"))
            RunStage2PerformanceProfile(300, 80);
        else if (OS.GetCmdlineUserArgs().Contains("--stage3-smoke"))
            RunHeadlessInteractionSmoke();
        else if (OS.GetCmdlineUserArgs().Contains("--stage3-tracking-smoke"))
            RunTrackingSmoke();
        else if (OS.GetCmdlineUserArgs().Contains("--stage3-worker-smoke"))
            RunWorkerSmoke();
        else if (OS.GetCmdlineUserArgs().Contains("--capture-stage2-world"))
            CaptureSelectedWorldFrame();
        else if (OS.GetCmdlineUserArgs().Contains("--capture-function-catalogue"))
            CaptureFunctionCatalogue();
        else if (OS.GetCmdlineUserArgs().Contains("--capture-food-web"))
            CaptureFoodWeb();
        else if (OS.GetCmdlineUserArgs().Contains("--capture-planet"))
            CapturePlanet();
    }

    public override void _Process(double delta)
    {
        CompleteSimulationBatch(wait: false);
        if (_rightDragging && !Input.IsMouseButtonPressed(MouseButton.Right))
            _rightDragging = false;
        if (!_cataloguePanel.Visible) UpdateCameraMovement(delta);
        if (!_paused && !_simulationFaulted)
        {
            _stepAccumulator += delta * _timeScale;
            // Accumulate at most one second of requested work instead of an unbounded backlog.
            _stepAccumulator = Math.Min(_stepAccumulator, Math.Max(FixedDelta, _timeScale));
        }

        _rateWindowSeconds += delta;
        if (_rateWindowSeconds >= 1.0)
        {
            _achievedScale = (_rateWindowSteps * FixedDelta) / _rateWindowSeconds;
            _rateWindowSeconds = 0.0;
            _rateWindowSteps = 0;
        }

        _renderAccumulator += delta;
        _resourceRenderAccumulator += delta;
        _poseInterpolationElapsed += delta;
        double renderInterval = _timeScale >= 100.0 ? 0.20 : 0.10;
        if (_renderAccumulator >= renderInterval)
        {
            if (RefreshSnapshotAndWorld())
            {
                _renderAccumulator = 0.0;
                _poseInterpolationElapsed = 0.0;
            }
        }
        UpdateTrackingCamera(delta);
        _worldRenderer.InterpolateContinuousSkins(
            (float)Math.Clamp(_poseInterpolationElapsed / renderInterval, 0.0, 1.0));

        _statisticsAccumulator += delta;
        if (_statisticsAccumulator >= 0.50)
        {
            RefreshStatistics();
            _statisticsAccumulator = 0.0;
        }

        _inspectorAccumulator += delta;
        _catalogueObserveElapsed += delta;
        _catalogueSaveElapsed += delta;
        if (_catalogueObserveElapsed >= 1.0)
        {
            ObserveFunctions();
            _catalogueObserveElapsed = 0;
        }
        if (_catalogueSaveElapsed >= 30.0)
        {
            _catalogue.Save();
            _catalogueSaveElapsed = 0;
        }
        if (_inspectorAccumulator >= 0.20)
        {
            RefreshInspector();
            _inspectorAccumulator = 0.0;
        }
        if (!_paused && !_simulationFaulted && _simulationBatch is null && _stepAccumulator >= FixedDelta)
        {
            SimulationWorld batchWorld = _world;
            int requested = (int)Math.Min(MaximumStepsPerFrame, _stepAccumulator / FixedDelta);
            Func<int> batch = () =>
            {
                int executed = 0;
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                do
                {
                    batchWorld.Step();
                    executed++;
                } while (executed < requested &&
                    System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < 8.0);
                return executed;
            };
            _simulationBatch = _synchronousDiagnostic ? Task.FromResult(batch()) : Task.Run(batch);
        }
    }

    private bool CompleteSimulationBatch(bool wait)
    {
        if (_simulationFaulted) return false;
        if (_simulationBatch is null) return true;
        if (!wait && !_simulationBatch.IsCompleted) return false;
        Task<int> completed = _simulationBatch;
        _simulationBatch = null;
        int executed;
        try { executed = completed.GetAwaiter().GetResult(); }
        catch (Exception error)
        {
            _simulationFaulted = true;
            _paused = true;
            _stepAccumulator = 0;
            _hud.SetPaused(true);
            _hud.UpdateStatus("模拟异常已暂停；可重置世界。" + error.Message);
            GD.PushError(error.ToString());
            return false;
        }
        _stepAccumulator = Math.Max(0, _stepAccumulator - executed * FixedDelta);
        _rateWindowSteps += executed;
        return true;
    }

    public override void _Input(InputEvent inputEvent)
    {
        if (inputEvent is InputEventKey shortcut && shortcut.Pressed && !shortcut.Echo &&
            (shortcut.Keycode == Key.G || (shortcut.Keycode == Key.Escape && _cataloguePanel.Visible)))
        {
            _hud.Visible = true;
            _cataloguePanel.Toggle();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (_cataloguePanel.Visible) return;
        if (inputEvent is InputEventMouseButton button && button.ButtonIndex == MouseButton.Right)
        {
            if (button.Pressed)
            {
                if (GetViewport().GuiGetHoveredControl() is null)
                {
                    _rightDragging = true;
                    GetViewport().SetInputAsHandled();
                }
            }
            else
            {
                _rightDragging = false;
            }
        }
        else if (inputEvent is InputEventMouseMotion motion && _rightDragging)
        {
            if (CameraOverviewWeight > 0.5f)
                OrbitGlobe(motion.Relative);
            else
            {
                _cameraYaw -= motion.Relative.X * 0.006f;
                _cameraPitch = Math.Clamp(_cameraPitch - (motion.Relative.Y * 0.006f), -1.42f, -0.12f);
            }
            UpdateCameraTransform();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (_cataloguePanel.Visible) return;
        if (inputEvent is InputEventMouseButton mouseButton)
        {
            if (mouseButton.Pressed && mouseButton.ButtonIndex == MouseButton.WheelUp)
            {
                _cameraDistance = Math.Max(GetMinimumCameraDistance(), _cameraDistance * 0.82f);
                UpdateCameraTransform();
            }
            else if (mouseButton.Pressed && mouseButton.ButtonIndex == MouseButton.WheelDown)
            {
                _cameraDistance = Math.Min(PlanetProjection.Radius(_world.Config.WorldSize) * 6f, _cameraDistance * 1.18f);
                UpdateCameraTransform();
            }
            else if (mouseButton.Pressed && mouseButton.ButtonIndex == MouseButton.Left)
            {
                if (_toolMode == ToolMode.Select)
                    SelectNearest(mouseButton.Position);
                else if (TryGetWorldPoint(mouseButton.Position, out NumericsVector2 worldPoint))
                    ApplyBrush(worldPoint, mouseButton.ShiftPressed ? -1.0 : 1.0);
            }
        }
        else if (inputEvent is InputEventKey key && key.Pressed && !key.Echo)
        {
            switch (key.Keycode)
            {
                case Key.Space:
                    TogglePause();
                    break;
                case Key.Key1:
                    SetTool(ToolMode.Select);
                    break;
                case Key.Key2:
                    SetTool(ToolMode.Minerals);
                    break;
                case Key.Key3:
                    SetTool(ToolMode.Temperature);
                    break;
                case Key.H:
                    CycleHeatmap();
                    break;
                case Key.Bracketleft:
                    _brushRadius = Math.Max(4f, _brushRadius - 4f);
                    UpdateToolStatus();
                    break;
                case Key.Bracketright:
                    _brushRadius = Math.Min(120f, _brushRadius + 4f);
                    UpdateToolStatus();
                    break;
                case Key.Tab:
                    _hud.Visible = !_hud.Visible;
                    break;
                case Key.F11:
                    ToggleFullscreen();
                    break;
                case Key.F2:
                    OpenMorphologyLab();
                    break;
            }
        }
    }

    private void ConnectHudCommands()
    {
        _hud.RandomIndividualRequested += SelectRandomIndividual;
        _hud.PauseRequested += TogglePause;
        _hud.StepRequested += SingleStep;
        _hud.SpeedRequested += SetSpeed;
        _hud.SmallWorldRequested += () => ResetWorld(24, 0, "少量祖先 24");
        _hud.ObservationWorldRequested += () => ResetWorld(300, 80, "观察负载 300（真实预演 8 秒）");
        _hud.SeedWorldRequested += seed =>
        {
            _worldSeed = seed;
            _cameraTarget = Vector3.Zero;
            _cameraDistance = 440f;
            ResetWorld(24, 0, $"随机地形 · 种子 {seed}");
        };
        _hud.SelectToolRequested += () => SetTool(ToolMode.Select);
        _hud.MineralToolRequested += () => SetTool(ToolMode.Minerals);
        _hud.TemperatureToolRequested += () => SetTool(ToolMode.Temperature);
        _hud.HeatmapRequested += CycleHeatmap;
        _hud.MediumDiagnosticRequested += MoveSelectedToLandDiagnostic;
        _hud.MorphologyLabRequested += OpenMorphologyLab;
        _hud.FunctionCatalogueRequested += _cataloguePanel.Toggle;
    }

    private void ResetWorld(int ancestors, int preRunSteps, string label)
    {
        StopTracking();
        CompleteSimulationBatch(wait: true);
        _simulationFaulted = false;
        _catalogue.Save();
        _catalogue.BeginWorld();
        SimulationConfig config = new()
        {
            WorldSize = 512f,
            EnvironmentGridSize = 128,
            MaxPopulation = 5_000,
            // One finite material budget shared by the default and observation starts.
            InitialMineralScale = 1.0,
            ResourceBudgetReferenceAncestors = 24
        };
        _world = new SimulationWorld(config, _worldSeed, ancestors);
        _hud.SetSeed(_worldSeed);
        _observationTerrain = new ObservationTerrain(_world.Environment, config);
        if (preRunSteps > 0)
            _world.Run(preRunSteps);
        _selectedId = null;
        _stepAccumulator = 0.0;
        _heatmapMode = HeatmapMode.Natural;
        _resourceRenderAccumulator = 1;
        _worldRenderer.BuildEnvironment(_world.Environment, config.WorldSize, _heatmapMode);
        if (_cameraTarget.LengthSquared() < 1f)
            SetCameraAnchor(new NumericsVector2(config.WorldSize * 0.5f, config.WorldSize * 0.5f), 0f);
        UpdateCameraTransform();
        RefreshSnapshotAndWorld();
        RefreshStatistics();
        RefreshInspector();
        _hud.UpdateStatus($"已载入 {label}：绿植与水藻可被摄食，青色显示矿物，琥珀色为有机食物，棕色为残骸。水面薄色块表示该水柱资源；基因决定生物颜色。");
        UpdateModeLabel();
    }

    public override void _ExitTree()
    {
        CompleteSimulationBatch(wait: true);
        _catalogue?.Save();
    }

    private bool CanFocusCatalogueEntry(ObservedFunction entry)
    {
        if (entry.WorldId != _catalogue.WorldId || _snapshot is null) return false;
        foreach (var organism in _snapshot.Organisms)
            if (organism.Id == entry.RepresentativeId &&
                organism.GenomeFingerprint.ToString("X16") == entry.RepresentativeGenome) return true;
        return false;
    }

    private void ObserveFunctions()
    {
        if (_snapshot is null) return;
        foreach (var organism in _snapshot.Organisms)
        {
            bool chemical = organism.ActiveSensorCount > 0 && organism.SensingEnergyLastStep > 1e-10 && Math.Abs(organism.ChemicalSensorSignal) > 1e-6;
            bool vision = organism.ActiveVisualSensorCount > 0 && organism.SensingEnergyLastStep > 1e-10 && Math.Abs(organism.VisionSignal) > 1e-6;
            bool ground = organism.AppendageContactCount > 0 && organism.AppendageSupport > 1e-5 &&
                organism.AppendageGroundVelocity.LengthSquared() > 1e-8 && organism.AppendageEnergyLastStep > 1e-10;
            bool cavity = organism.CavityTissueOxygenLastStep > 1e-9;
            if (organism.PrimaryProductionLastStep > 1e-9)
                RecordFunction(organism, "primary-production", "光合有机物生产",
                    $"本步把无机养分转成有机储备 {organism.PrimaryProductionLastStep:E2}。",
                    "需要光、光合表达和矿物同时可用。观察贫养分或遮光环境中的增长变化。");
            if (organism.OrganicFeedingLastStep > 1e-9)
                RecordFunction(organism, "organic-feeding", "有机物摄食",
                    $"本步从植被、藻类或可食有机物同化 {organism.OrganicFeedingLastStep:E2}。",
                    "摄食与消化都需要实际表达和处理能量；食物被吃后对应环境库存减少。");
            if (organism.DecompositionLastStep > 1e-9)
                RecordFunction(organism, "detritus-decomposition", "残骸分解",
                    $"本步处理残骸 {organism.DecompositionLastStep:E2}。",
                    "分解者从有机物获得有限收益，并让部分养分回到无机池。");
            if (organism.SocialResponse != SocialResponse.None && organism.Velocity.LengthSquared() > 1e-6 &&
                organism.SensingEnergyLastStep > 1e-10)
                RecordFunction(organism, "social-" + organism.SocialResponse,
                    organism.SocialResponse == SocialResponse.Approach ? "个体趋近反应" : "个体回避反应",
                    $"付费感知与运动同时发生，响应目标 #{organism.SocialTargetId}。",
                    "响应经过遗传控制连接与身体力学；记录反应不代表必定追上或逃脱。");
            if (organism.AttackDamageLastStep > 0)
                RecordFunction(organism, "contact-attack", "接触攻击",
                    $"本步付费攻击造成损伤 {organism.AttackDamageLastStep:E2}。",
                    "攻击倾向与食性独立；植食者也能演化出攻击能力。");
            if (organism.RetaliationDamageLastStep > 0)
                RecordFunction(organism, "contact-retaliation", "受伤反击",
                    $"本步遭实际伤害后反击造成损伤 {organism.RetaliationDamageLastStep:E2}。",
                    "需要遗传反击倾向、真实接触和能量；每步最多一次，不递归触发。");
            if (organism.PredationLastStep > 1e-9)
                RecordFunction(organism, "contact-predation", "接触捕食",
                    $"本步从接触个体同化有机储备 {organism.PredationLastStep:E2}。",
                    "需要食肉遗传倾向、真实接触、摄食和消化能力；最多同化20%，攻击与消化另耗能。");
            if (organism.LightEnergyLastStep > 1e-9)
                RecordFunction(organism, "pigment-photosynthesis", "色素光能利用",
                    $"本步光能转化为化学能 {organism.LightEnergyLastStep:E2}。",
                    "只有付费表达光合色素的外露区域才能利用光能。光合生产还受矿物供应限制，方向光感本身不产能。");
            if (organism.AppendageContactCount > 0 && organism.AppendageSupport > 1e-5)
                RecordFunction(organism, "ground-support", "附肢接地支撑",
                    $"植地接触 {organism.AppendageContactCount}，支撑比例 {organism.AppendageSupport:P1}。",
                    "在岸边观察支撑能否减少行动限制。能接地支撑不代表已经能行走，推进作用单独记录。");
            if (ground)
                RecordFunction(organism, "ground-propulsion", "附肢接地推进",
                    $"接地推进 {organism.AppendageGroundVelocity.Length():F4}，本步附肢耗能 {organism.AppendageEnergyLastStep:E2}。",
                    "观察浅滩到陆地的实际位移、失水和能量消耗。具有附肢外观不等于可用的腿，只有实际接地推进才会记录。");
            if (cavity)
                RecordFunction(organism, "cavity-supply", "气腔向组织供氧",
                    $"本步组织供氧 {organism.CavityTissueOxygenLastStep:E2}，腔氧 {organism.CavityOxygen:F5}/{organism.CavityOxygenCapacity:F5}。",
                    "观察储气耗尽后是否能补气，以及离水后的保水成本。供氧和通气共同工作才有持续呼吸的可能。");
            if (organism.CavityVentilationLastStep > 1e-9)
                RecordFunction(organism, "cavity-ventilation", "气腔换气",
                    $"本步外界通气补氧 {organism.CavityVentilationLastStep:E2}，腔体耗能 {organism.CavityEnergyLastStep:E2}。",
                    "观察开口在空气、水线和水下的变化。开口能补气不等于氧能运输到身体，组织供氧单独记录。");
            int combination = (chemical ? 1 : 0) | (vision ? 2 : 0) | (ground ? 4 : 0) | (cavity ? 8 : 0);
            if (System.Numerics.BitOperations.PopCount((uint)combination) >= 2)
            {
                string name = string.Join(" + ", new[] { chemical ? "化学感知" : null,
                    vision ? "方向光感" : null, ground ? "接地推进" : null, cavity ? "气腔供氧" : null }.Where(value => value is not null));
                RecordFunction(organism, $"combination-{combination}", "组合：" + name,
                    "同一个体在本次观察中同时发挥了这些作用：" + name + "。",
                    "可收藏并定位这个组合。功能同时存在不代表产生协同优势；比较能耗、存活和成熟后代后再判断。");
            }
            if (organism.ActiveVisualSensorCount > 0 && organism.SensingEnergyLastStep > 1e-10 &&
                Math.Abs(organism.VisionSignal) > 1e-6)
                RecordFunction(organism, "directional-light", "简单视觉：方向光感",
                    $"活跃方向受体 {organism.ActiveVisualSensorCount}，光信号 {organism.VisionSignal:F3}。",
                    "观察不同方向、深度与遮挡条件下的行动变化。这是有方向的光信号，不是图像识别；是否有生存收益需要跟踪后代。");
            if (organism.OxygenUptakeLastStep > 1e-9 && organism.WaterExposedArea > 1e-6 && organism.Immersion > 0.95)
                RecordFunction(organism, "aquatic-exchange", "水中气体交换",
                    $"水下外露面积 {organism.WaterExposedArea:F3}，本步摄氧 {organism.OxygenUptakeLastStep:E2}。",
                    "比较不同水层中的摄氧与生存。这是水中交换的观察记录，不等于已经形成鳃。");
            if (organism.OxygenUptakeLastStep > 1e-9 && organism.AirExposedArea > 1e-6 && organism.Immersion < 0.05)
                RecordFunction(organism, "air-exchange", "空气气体交换",
                    $"空气外露面积 {organism.AirExposedArea:F3}，本步摄氧 {organism.OxygenUptakeLastStep:E2}。",
                    "比较岸边个体的水分、供氧和存活。皮肤交换也会出现这条记录，不能据此认定已经形成肺。");
            if (organism.Immersion > 0.5 && organism.Velocity.LengthSquared() > 1e-6 &&
                organism.LocalActuationForce.LengthSquared() > 1e-9)
                RecordFunction(organism, "aquatic-propulsion", "水中主动运动",
                    $"游动速度 {organism.Velocity.Length():F3}，局部驱动力 {organism.LocalActuationForce.Length():F3}。",
                    "通过稀疏资源斑块观察移动距离与能量成本。速度更高不一定使后代更成功。");
            if (organism.ActiveSensorCount > 0 && organism.SensingEnergyLastStep > 1e-10 &&
                Math.Abs(organism.ChemicalSensorSignal) > 1e-6)
                RecordFunction(organism, "chemical-sensing", "化学资源感知",
                    $"付费感知已工作，化学信号 {organism.ChemicalSensorSignal:F3}。",
                    "用资源笔刷形成稀疏食物斑块，观察个体是否更有效地寻找资源，并追踪后代。资源减少也可能使谱系灭绝。");
            if (organism.ActiveSensorCount > 0 && organism.SensingEnergyLastStep > 1e-10 &&
                organism.ContactSensorSignal > 1e-6)
                RecordFunction(organism, "contact-sensing", "接触感知",
                    $"付费接触信号 {organism.ContactSensorSignal:F3}，邻居 {organism.ContactNeighborCount}。",
                    "观察拥挤区域中个体的避让与争夺。收藏后可定位代表个体，对比它与后代的行为。");
            if (organism.InteractionIntensity > 1e-6 && organism.ContactNeighborCount > 0)
                RecordFunction(organism, "local-interaction", "局部个体互动",
                    $"互动状态 {organism.InteractionState}，强度 {organism.InteractionIntensity:F3}。",
                    "通过资源分布改变局部密度，观察争夺、避让及其生存代价。");
        }
    }

    private void RecordFunction(OrganismPresentationState organism, string key, string name,
        string evidence, string guidance) => _catalogue.Observe(key, name, evidence, guidance,
            _worldSeed, _snapshot.Statistics.SimulatedSeconds, organism.Generation,
            organism.Id, organism.GenomeFingerprint);

    private void TogglePause()
    {
        if (!CompleteSimulationBatch(wait: true)) return;
        _paused = !_paused;
        _hud.SetPaused(_paused);
        UpdateModeLabel();
    }

    private void SingleStep()
    {
        if (!CompleteSimulationBatch(wait: true)) return;
        _paused = true;
        _hud.SetPaused(true);
        _world.Step();
        RefreshSnapshotAndWorld();
        RefreshStatistics();
        RefreshInspector();
        UpdateModeLabel();
    }

    private void SetSpeed(double speed)
    {
        if (_simulationFaulted) return;
        _timeScale = speed;
        _paused = false;
        _hud.SetPaused(false);
        UpdateModeLabel();
    }

    private void SetTool(ToolMode mode)
    {
        _toolMode = mode;
        UpdateToolStatus();
    }

    private void CycleHeatmap()
    {
        if (!CompleteSimulationBatch(wait: true)) return;
        int count = Enum.GetValues<HeatmapMode>().Length;
        _heatmapMode = (HeatmapMode)(((int)_heatmapMode + 1) % count);
        _worldRenderer.BuildEnvironment(_world.Environment, _world.Config.WorldSize, _heatmapMode);
        _resourceRenderAccumulator = 1;
        RefreshSnapshotAndWorld();
        _hud.UpdateStatus($"环境图层：{HeatmapName(_heatmapMode)}。");
    }

    private void ApplyBrush(NumericsVector2 position, double direction)
    {
        if (!CompleteSimulationBatch(wait: true)) return;
        EnvironmentBrushChannel channel = _toolMode == ToolMode.Minerals
            ? EnvironmentBrushChannel.Minerals
            : EnvironmentBrushChannel.Temperature;
        double amount = channel == EnvironmentBrushChannel.Minerals ? 0.65 : 0.06;
        EnvironmentBrushCommand command = new(position, _brushRadius, amount * direction, channel);
        _world.QueueEnvironmentBrush(command);
        _world.ApplyQueuedCommands();
        _worldRenderer.BuildEnvironment(_world.Environment, _world.Config.WorldSize, _heatmapMode);
        RefreshSnapshotAndWorld();
        EnvironmentInterventionRecord applied = _world.RecentInterventions[^1];
        string effect = channel == EnvironmentBrushChannel.Minerals
            ? $"物质账本外部变化 {applied.AppliedMatterDelta:+0.000;-0.000}"
            : "温度场已修改";
        _hud.UpdateStatus(
            $"{(channel == EnvironmentBrushChannel.Minerals ? "矿物" : "温度")}笔刷：" +
            $"({position.X:F1}, {position.Y:F1})，半径 {_brushRadius:F0}，{effect}。");
    }

    private void MoveSelectedToLandDiagnostic()
    {
        if (!CompleteSimulationBatch(wait: true)) return;
        if (_selectedId is null && _snapshot.Organisms.Count > 0)
            _selectedId = _snapshot.Organisms[0].Id;
        if (_selectedId is null)
        {
            _hud.UpdateStatus("人工水陆对照无法开始：当前没有存活个体。");
            return;
        }
        NumericsVector2? land = null;
        // This is a one-off diagnostic query, never part of the simulation step.
        // Prefer interior land, not the first shoreline pixel in scan order.
        var waterSamples = new List<NumericsVector2>();
        var landSamples = new List<NumericsVector2>();
        for (int y = 0; y <= 48; y++) for (int x = 0; x <= 48; x++)
        {
            NumericsVector2 candidate = new(
                _world.Config.WorldSize * x / 48f, _world.Config.WorldSize * y / 48f);
            if (_world.Environment.Sample(candidate).WaterDepth <= 0.0)
                landSamples.Add(candidate);
            else waterSamples.Add(candidate);
        }
        double bestClearance = -1;
        System.Numerics.Vector3[] waterDirections = waterSamples.Select(position =>
            SphericalWorld.ToUnit(position, _world.Config.WorldSize)).ToArray();
        foreach (NumericsVector2 candidate in landSamples)
        {
            System.Numerics.Vector3 direction = SphericalWorld.ToUnit(candidate, _world.Config.WorldSize);
            double clearance = double.MaxValue;
            foreach (System.Numerics.Vector3 water in waterDirections)
                clearance = Math.Min(clearance, System.Numerics.Vector3.DistanceSquared(direction, water));
            if (clearance > bestClearance) { bestClearance = clearance; land = candidate; }
        }
        if (land is null || !_world.RelocateForMediumDiagnostic(_selectedId.Value, land.Value, 0))
        {
            _hud.UpdateStatus("人工水陆对照无法开始：地图没有陆地点或个体已死亡。");
            return;
        }
        _paused = false;
        _timeScale = 1;
        _hud.SetPaused(false);
        RefreshSnapshotAndWorld();
        SelectAndFocus(_selectedId.Value);
        _hud.UpdateStatus($"人工诊断：已移至内陆 ({land.Value.X:F0}, {land.Value.Y:F0})。观察含水、摄氧和移动；资源丰富仍需具备陆地生存能力。");
    }

    private void SelectRandomIndividual()
    {
        if (_snapshot.Organisms.Count == 0)
        {
            StopTracking();
            _hud.UpdateStatus("目前没有存活个体可定位。");
            return;
        }
        // Presentation RNG must never consume the simulation's random streams.
        SelectAndFocus(_snapshot.Organisms[Random.Shared.Next(_snapshot.Organisms.Count)].Id);
    }

    private void StartTracking(OrganismPresentationState organism)
    {
        _trackedId = organism.Id;
        _trackingTarget = PlanetProjection.MapToWorld(organism.Position,
            LowPolyWorldRenderer.OrganismElevation(organism), _world.Config.WorldSize);
        _hud.SetTracking(_trackedId);
    }

    private void StopTracking()
    {
        if (_trackedId is null) return;
        _trackedId = null;
        _hud.SetTracking(null);
    }

    private void RefreshTrackingTarget()
    {
        if (_trackedId is null) return;
        foreach (OrganismPresentationState organism in _snapshot.Organisms)
        {
            if (organism.Id != _trackedId.Value) continue;
            _trackingTarget = PlanetProjection.MapToWorld(organism.Position,
                LowPolyWorldRenderer.OrganismElevation(organism), _world.Config.WorldSize);
            return;
        }
        StopTracking();
        _hud.UpdateStatus("追踪个体已死亡，已恢复自由视角。");
    }

    private void UpdateTrackingCamera(double delta)
    {
        if (_trackedId is null) return;
        if (_cameraDistance >= PlanetProjection.Radius(_world.Config.WorldSize) * 1.6f)
        {
            StopTracking();
            return;
        }
        float blend = (float)(1.0 - Math.Exp(-12.0 * delta));
        Vector3 normal = _cameraTarget.Normalized().Lerp(_trackingTarget.Normalized(), blend).Normalized();
        _cameraTarget = normal * Mathf.Lerp(_cameraTarget.Length(), _trackingTarget.Length(), blend);
        TransportCameraFrame(normal);
        UpdateCameraTransform();
    }

    private void SelectAndFocus(ulong organismId)
    {
        foreach (OrganismPresentationState organism in _snapshot.Organisms)
        {
            if (organism.Id != organismId) continue;
            _selectedId = organismId;
            StartTracking(organism);
            _hud.RevealInspector();
            SetCameraAnchor(organism.Position, LowPolyWorldRenderer.OrganismElevation(organism));
            _cameraDistance = Math.Min(_cameraDistance, 18f);
            UpdateCameraTransform();
            RefreshInspector();
            return;
        }
    }

    private void SelectNearest(Vector2 screenPosition)
    {
        const float maximumScreenDistance = 28f;
        OrganismPresentationState? nearest = null;
        float nearestDistance = maximumScreenDistance;
        foreach (OrganismPresentationState organism in _snapshot.Organisms)
        {
            Vector3 worldPosition = PlanetProjection.MapToWorld(organism.Position,
                LowPolyWorldRenderer.OrganismElevation(organism), _world.Config.WorldSize);
            if (_camera.IsPositionBehind(worldPosition))
                continue;
            Vector2 organismScreen = _camera.UnprojectPosition(worldPosition);
            float distance = organismScreen.DistanceTo(screenPosition);
            if (distance < nearestDistance)
            {
                Vector3 toOrganism = worldPosition - _camera.GlobalPosition;
                if (TrySurfaceRay(_camera.GlobalPosition, toOrganism.Normalized(), false, out Vector3 surfaceHit) &&
                    surfaceHit.DistanceTo(_camera.GlobalPosition) + (float)organism.Body.BoundingRadius * 2f < toOrganism.Length())
                    continue;
                nearest = organism;
                nearestDistance = distance;
            }
        }

        if (nearest is not null)
            SelectAndFocus(nearest.Value.Id);
        else
        {
            _selectedId = null;
            StopTracking();
        }
        RefreshSnapshotAndWorld();
        RefreshInspector();
        _hud.UpdateStatus(nearest is null
            ? "点击位置附近没有生命；可滚轮拉近后重试。"
            : $"已选择并聚焦个体 {nearest.Value.Id}，屏幕命中距离 {nearestDistance:F1}px。 ");
    }

    private bool RefreshSnapshotAndWorld()
    {
        if (!CompleteSimulationBatch(wait: false)) return false;
        _snapshot = _world.CapturePresentationSnapshot();
        RefreshTrackingTarget();
        if (_snapshot.Resources is not null && _resourceRenderAccumulator >= 0.5)
        {
            _worldRenderer.UpdateResources(_snapshot.Resources,
                CameraMapPosition, _heatmapMode == HeatmapMode.Natural);
            _resourceRenderAccumulator = 0;
        }
        _renderedRegions = _worldRenderer.UpdateOrganisms(_snapshot, _selectedId,
            CameraMapPosition);
        if (_selectedId is not null && !_snapshot.Organisms.Any(organism => organism.Id == _selectedId.Value))
            _selectedId = null;
        return true;
    }

    private void RefreshStatistics()
    {
        SimulationSnapshot stats = _snapshot.Statistics;
        int livingGeneration = 0;
        int crowdedPopulation = 0;
        foreach (OrganismPresentationState organism in _snapshot.Organisms)
        {
            livingGeneration = Math.Max(livingGeneration, organism.Generation);
            if (organism.ContactPressure > 0.05) crowdedPopulation++;
        }
        _hud.UpdateStatistics(
            $"步 {stats.StepIndex:N0} · {stats.SimulatedSeconds:F1}s · 存活 {stats.Population:N0} · " +
            $"出生/死亡 {stats.CumulativeBirths:N0}/{stats.CumulativeDeaths:N0} · 存活最高第 {livingGeneration} 代\n" +
            $"死亡 损伤/夭折/衰老 {stats.DamageDeaths:N0}/{stats.JuvenileDeaths:N0}/{stats.SenescenceDeaths:N0} · " +
            $"身体/储备 {stats.OrganismBodyMatter:F1}/{stats.OrganismStoredMatter:F1} · 矿物 {stats.EnvironmentMinerals:F1} · 残骸/废物 {stats.EnvironmentDetritus:F1}/{stats.EnvironmentMetabolicWaste:F1}\n" +
            $"植物/藻类 {stats.EnvironmentVegetation:F1} · 有机食物 {stats.EnvironmentEdibleOrganics:F1} · 累计生产/摄食/分解 {stats.CumulativePrimaryProduction:F1}/{stats.CumulativeOrganicFeeding:F1}/{stats.CumulativeDecomposition:F1}\n" +
            $"水中/岸边/陆地 {stats.AquaticPopulation}/{stats.ShorePopulation}/{stats.LandPopulation} · 累计光合输入 {stats.CumulativeLightEnergy:F1} · 拥挤 {crowdedPopulation:N0}\n" +
            $"基因组 {stats.GenomeCount:N0} · 身体区域 {stats.TotalBodyRegions:N0}（渲染实例 {_renderedRegions:N0}" +
            $"{(_worldRenderer.UsesSimplifiedProxies ? "，远景代理" : "，完整区域")}） · " +
            $"均速 {stats.AverageSpeed:F2} · 目标 {_timeScale:0}× / 实际 {_achievedScale:0.0}× · " +
            $"物质误差 {stats.MatterError:E2}",
            $"生命 {stats.Population:N0}     第 {livingGeneration} 代     {stats.SimulatedSeconds:F0} 秒     {_timeScale:0}×");
    }

    private void RefreshInspector()
    {
        if (_selectedId is null)
        {
            _hud.UpdateInspector("选择工具下点击一个生命。\n详情以约 5 Hz 读取只读快照，只有值变化时才更新文本。 ");
            return;
        }

        OrganismPresentationState? selected = null;
        foreach (OrganismPresentationState candidate in _snapshot.Organisms)
        {
            if (candidate.Id == _selectedId.Value)
            {
                selected = candidate;
                break;
            }
        }
        if (selected is null)
        {
            _selectedId = null;
            _hud.UpdateInspector("该个体已死亡，其实际身体和储存物质已回到环境残骸。 ");
            return;
        }

        OrganismPresentationState organism = selected.Value;
        string interaction = organism.InteractionState switch
        {
            InteractionState.Contesting => "争夺位置",
            InteractionState.Yielding => "接触退让",
            _ => "无接触互动"
        };
        string opponent = organism.InteractionOpponentId == 0 ? "无" : organism.InteractionOpponentId.ToString();
        string foodSatisfaction = organism.ResourceDemandLastStep > 1e-12
            ? $"{organism.ResourceSatisfaction:P0}（需求 {organism.ResourceDemandLastStep:F4}）"
            : "无摄取需求";
        string regionalInventory = string.Join("\n", organism.RegionInventories
            .OrderBy(region => region.RegionId)
            .Take(6)
            .Select(region =>
                $"  区 {region.RegionId}: 结构 {region.Matter:F3} / 底物 {region.Substrate:F3} / 氧 {region.Oxygen:F4} / 水 {region.Water:F3} / 能 {region.Energy:F3}"));
        _hud.UpdateInspector(
            $"ID {organism.Id} · 亲代 {organism.ParentId} · 第 {organism.Generation} 代\n" +
            $"基因组 {organism.GenomeId} · {organism.GenomeFingerprint:X16}\n" +
            $"{(organism.ParentId==0 ? "初始祖先基因" : $"出生时 {organism.BirthMutationCount} 次突变")} · 子代突变概率 {organism.OffspringMutationProbability:P1}\n" +
            $"基因区域 {organism.GenomeRegionCount} · 当前身体区域 {organism.Regions.Count}\n" +
            $"年龄 {organism.AgeSeconds:F1}s · 成熟 {organism.Maturity:P1} · 发育 {organism.DevelopmentCompletion:P1}\n" +
            $"能量 {organism.Energy:F3} · 储存物质 {organism.StoredMatter:F3} · 繁殖冷却 {organism.ReproductionCooldownSeconds:F1}s\n" +
            $"探索倾向 {organism.ExplorationDrive:P0} · 食物信号变化 {organism.ForagingTrend:+0.000;-0.000;0.000}\n" +
            $"生理压力 {organism.SurvivalStress:P0} · 避害反射 {(organism.SurvivalReflexActive ? organism.SurvivalReflexMode == SurvivalReflexMode.Retreat ? "沿近期经历回撤" : "试探缓解方向" : "未触发")}\n" +
            $"表达强度 {organism.MeanTissueExpression:F3} · 活跃受体 {organism.ActiveSensorCount}（方向光感 {organism.ActiveVisualSensorCount}）\n" +
            $"食肉/攻击/反击 {organism.AnimalFoodAffinity:P0}/{organism.AttackAffinity:P0}/{organism.RetaliationAffinity:P0} · 个体受体 {organism.ActiveIndividualSensors} · {(organism.SocialResponse == SocialResponse.Approach ? "趋近" : organism.SocialResponse == SocialResponse.Avoid ? "回避" : organism.SocialResponse == SocialResponse.InjuryAvoidance ? "受伤反应" : "无目标反应")} #{organism.SocialTargetId}\n" +
            $"化学感知有效度 {organism.ChemicalSenseAccess:P1} · 化学/接触/视觉信号 {organism.ChemicalSensorSignal:F3}/{organism.ContactSensorSignal:F3}/{organism.VisionSignal:F3} · 感知耗能 {organism.SensingEnergyLastStep:E2}\n" +
            $"附肢接地 {organism.AppendageContactCount} · 支撑 {organism.AppendageSupport:P0} · 推进 {organism.AppendageGroundVelocity.Length():F3} · 耗能 {organism.AppendageEnergyLastStep:E2}\n" +
            $"腔氧 {organism.CavityOxygen:F4}/{organism.CavityOxygenCapacity:F4} · 换气/供组织 {organism.CavityVentilationLastStep:E2}/{organism.CavityTissueOxygenLastStep:E2} · 耗能 {organism.CavityEnergyLastStep:E2}\n" +
            $"接触压力 {organism.ContactPressure:P1} · 接触邻居 {organism.ContactNeighborCount}\n" +
            $"互动 {interaction} · 对方 ID {opponent} · 争位强度 {organism.InteractionIntensity:P1}\n" +
            $"本步食物需求满足 {foodSatisfaction}\n" +
            $"身体物质 {organism.Body.TotalMatter:F3} · 质量 {organism.Body.PhysicalMass:F3} · 半径 {organism.Body.BoundingRadius:F3}\n" +
            $"有效光合面 {organism.Body.PhotosyntheticSurface:F3} · 摄取面 {organism.Body.MatterUptakeSurface:F3} · 维护 {organism.Body.MaintenanceEnergyPerSecond:F3}/s\n" +
            $"介质 {(organism.Immersion >= 0.8 ? "水中" : organism.Immersion > 0.05 ? "水线" : "陆地")} · 个体深度 {organism.Depth:F2}/{organism.Environment.WaterDepth:F2} · 浸没 {organism.Immersion:P0} · 升降速度 {organism.VerticalVelocity:+0.000;-0.000;0.000}（+上浮）\n" +
            $"含水 {organism.Hydration:P1} · 空气交换表达 {organism.RegionInventories.Average(region => region.AirExchangeExpression):F3} · 区域氧 {organism.InternalOxygen:F4}/{organism.OxygenCapacity:F4} · 本步摄氧/耗氧 {organism.OxygenUptakeLastStep:F5}/{organism.OxygenConsumedLastStep:F5} · 缺氧缺口 {organism.HypoxiaShortfallLastStep:E2}\n" +
            $"本步光合储能/代谢产能 {organism.LightEnergyLastStep:F5}/{organism.MetabolicEnergyLastStep:F5} · 失水/压力代价 {organism.DehydrationCostLastStep:F5}\n" +
            $"本步生产/摄食/分解/捕食 {organism.PrimaryProductionLastStep:E1}/{organism.OrganicFeedingLastStep:E1}/{organism.DecompositionLastStep:E1}/{organism.PredationLastStep:E1}\n" +
            $"表面样本 外露/遮蔽 {organism.ExposedSurfaceSamples}/{organism.OccludedSurfaceSamples} · 水/气暴露面 {organism.WaterExposedArea:F3}/{organism.AirExposedArea:F3}\n" +
            $"区域库存（最多显示 6 区）：\n{regionalInventory}\n" +
            $"环境：海底 {organism.Environment.TerrainHeight:F2} · 压力 {organism.Environment.Pressure:F3} · 光 {organism.Environment.Light:F3}\n" +
            $"溶解氧可用度 {organism.Environment.DissolvedOxygenAvailability:F3} · 空气氧可用度 {organism.Environment.AirOxygenAvailability:F3}（均为各介质内部无量纲势）\n" +
            $"温度 {organism.Environment.Temperature:F3} · 矿物 {organism.Environment.Minerals:F3} · 植被/有机物 {organism.Environment.ProducerBiomass:F3}/{organism.Environment.EdibleOrganics:F3}\n" +
            $"残骸/废物 {organism.Environment.Detritus:F3}/{organism.Environment.MetabolicWaste:F3}\n" +
            $"速度 ({organism.Velocity.X:F2}, {organism.Velocity.Y:F2}) · 局部反力 ({organism.LocalActuationForce.X:F3}, {organism.LocalActuationForce.Y:F3}) · 力矩 {organism.ActuationTorque:F3}\n" +
            $"控制输出 收缩 {organism.ControllerOutputs.ContractionActivation:F2} / 通透 {organism.ControllerOutputs.PermeabilityGate:F2} / 分泌 {organism.ControllerOutputs.SecretionActivation:F2}\n" +
            "身体形变与活性表面驱动均消耗局部能量；低能量时减少探索。",
            $"个体 #{organism.Id}    第 {organism.Generation} 代\n\n" +
            $"{(organism.Maturity >= 0.95 ? "已成年" : "正在成长")} · 年龄 {organism.AgeSeconds:F0} 秒\n" +
            $"{(organism.Immersion >= 0.8 ? "水生环境" : organism.Immersion > 0.05 ? "水陆交界" : "陆地环境")} · 深度 {organism.Depth:F1}\n\n" +
            $"能量  {(organism.Energy > 0 && organism.Energy < 0.01 ? organism.Energy.ToString("E2") : organism.Energy.ToString("F2"))}\n有机储备  {organism.StoredMatter:F3}\n组织损伤  {organism.RegionInventories.Average(r => r.Damage):P0}\n" +
            $"身体区域  {organism.Regions.Count}\n" +
            $"{(organism.ParentId == 0 ? "初始祖先" : $"出生突变  {organism.BirthMutationCount} 次")}\n\n" +
            $"{(organism.SurvivalReflexActive ? organism.SurvivalReflexMode == SurvivalReflexMode.Retreat ? "正在避害回撤" : "正在寻找缓解方向" : organism.PredationLastStep > 0 ? "正在捕食" : organism.OrganicFeedingLastStep > 0 ? "正在摄食" : organism.ExplorationDrive > 0.05 ? "探索环境" : "低活动状态")}",
            organism.Maturity, organism.Energy / _world.Config.MaximumEnergy);
    }

    private void UpdateModeLabel()
    {
        string state = _paused ? "已暂停" : $"运行 {_timeScale:0}×";
        _hud.UpdateMode($"{state} · {HeatmapName(_heatmapMode)}");
    }

    private void UpdateToolStatus()
    {
        string tool = _toolMode switch
        {
            ToolMode.Select => "选择工具",
            ToolMode.Minerals => "矿物笔刷（左键增加，Shift+左键移除）",
            _ => "温度笔刷（左键加热，Shift+左键降温）"
        };
        _hud.UpdateStatus($"{tool}；连续半径 {_brushRadius:F0}，边缘平滑衰减。 ");
    }

    private void UpdateCameraMovement(double delta)
    {
        if (GetViewport().GuiGetFocusOwner() is LineEdit) return;
        Vector3 movement = GetCameraMovement(
            Input.IsKeyPressed(Key.W),
            Input.IsKeyPressed(Key.S),
            Input.IsKeyPressed(Key.A),
            Input.IsKeyPressed(Key.D));
        ApplyCameraMovement(movement, delta);
    }

    private NumericsVector2 CameraMapPosition =>
        PlanetProjection.WorldToMap(_cameraTarget, _world.Config.WorldSize);

    private float CameraOverviewWeight => Math.Clamp(
        (_cameraDistance / PlanetProjection.Radius(_world.Config.WorldSize) - 0.4f) / 0.9f, 0f, 1f);

    private void SetCameraAnchor(NumericsVector2 position, float elevation)
    {
        _cameraTarget = PlanetProjection.MapToWorld(position, elevation, _world.Config.WorldSize);
        _cameraSurfaceFrame = PlanetProjection.BasisAt(position, _world.Config.WorldSize);
    }

    private Vector3 GetCameraMovement(bool w, bool s, bool a, bool d)
    {
        Vector3 normal = _cameraTarget.Normalized();
        Vector3 forward = -_camera.GlobalBasis.Z;
        forward -= normal * forward.Dot(normal);
        if (forward.LengthSquared() < 1e-5f) forward = -_cameraSurfaceFrame.Z;
        forward = forward.Normalized();
        Vector3 right = forward.Cross(normal).Normalized();
        Vector3 movement = Vector3.Zero;
        if (w) movement += forward;
        if (s) movement -= forward;
        if (d) movement += right;
        if (a) movement -= right;
        return movement;
    }

    private void TransportCameraFrame(Vector3 normal)
    {
        Vector3 east = _cameraSurfaceFrame.X - normal * _cameraSurfaceFrame.X.Dot(normal);
        if (east.LengthSquared() < 1e-6f)
            east = PlanetProjection.BasisAt(CameraMapPosition, _world.Config.WorldSize).X;
        east = east.Normalized();
        _cameraSurfaceFrame = new Basis(east, normal, east.Cross(normal).Normalized());
    }

    private void ApplyCameraMovement(Vector3 movement, double delta)
    {
        if (movement.LengthSquared() <= 0f) return;
        StopTracking();
        Vector3 normal = _cameraTarget.Normalized();
        float metres = (float)(delta * Math.Max(2.0, _cameraDistance * 0.32));
        float angle = metres / PlanetProjection.Radius(_world.Config.WorldSize);
        Vector3 nextNormal = (normal * MathF.Cos(angle) + movement.Normalized() * MathF.Sin(angle)).Normalized();
        _cameraTarget = nextNormal * _cameraTarget.Length();
        TransportCameraFrame(nextNormal);
        UpdateCameraTargetHeight();
        UpdateCameraTransform();
    }

    private void OrbitGlobe(Vector2 drag)
    {
        Basis rotation = new Basis(_camera.GlobalBasis.Y, -drag.X * 0.006f) *
            new Basis(_camera.GlobalBasis.X, -drag.Y * 0.006f);
        _cameraTarget = rotation * _cameraTarget;
        _cameraSurfaceFrame = rotation * _cameraSurfaceFrame;
        UpdateCameraTargetHeight();
    }

    private void UpdateCameraTransform()
    {
        if (_cameraTarget.LengthSquared() < 1f)
            SetCameraAnchor(new NumericsVector2(_world.Config.WorldSize * 0.5f), 0f);
        float horizontal = _cameraDistance * MathF.Cos(_cameraPitch);
        Vector3 offset = _cameraSurfaceFrame * new Vector3(
            horizontal * MathF.Sin(_cameraYaw),
            -_cameraDistance * MathF.Sin(_cameraPitch),
            horizontal * MathF.Cos(_cameraYaw));
        float overview = CameraOverviewWeight;
        Vector3 normal = _cameraTarget.Normalized();
        float radius = PlanetProjection.Radius(_world.Config.WorldSize);
        _camera.Position = (_cameraTarget + offset).Lerp(normal * (radius + _cameraDistance), overview);
        // Prevent the near camera from entering solid terrain; water is traversable.
        NumericsVector2 cameraMap = PlanetProjection.WorldToMap(_camera.Position, _world.Config.WorldSize);
        float minimumRadius = Math.Max(1f, radius + (float)_observationTerrain.Height(cameraMap) + 0.3f);
        if (_camera.Position.Length() < minimumRadius)
            _camera.Position = _camera.Position.Normalized() * minimumRadius;
        Vector3 target = _cameraTarget.Lerp(Vector3.Zero, overview);
        Vector3 up = _cameraSurfaceFrame.Y.Lerp(-_cameraSurfaceFrame.Z, overview).Normalized();
        // Leave room for the top HUD while keeping the whole globe visible.
        target += up * (radius * 0.15f * overview);
        if (Math.Abs((_camera.Position - target).Normalized().Dot(up)) > 0.999f)
            up = _cameraSurfaceFrame.X;
        _camera.LookAt(target, up);
    }

    private bool TryGetWorldPoint(Vector2 screenPosition, out NumericsVector2 worldPoint)
    {
        if (TrySurfaceRay(_camera.ProjectRayOrigin(screenPosition),
            _camera.ProjectRayNormal(screenPosition), includeWater: true, out Vector3 hit))
        {
            worldPoint = PlanetProjection.WorldToMap(hit, _world.Config.WorldSize);
            return true;
        }
        worldPoint = default;
        return false;
    }

    private bool TrySurfaceRay(Vector3 origin, Vector3 direction, bool includeWater, out Vector3 hit)
    {
        hit = default;
        float radius = PlanetProjection.Radius(_world.Config.WorldSize);
        float outerRadius = radius + Math.Max((float)_observationTerrain.MaximumHeight,
            (float)_observationTerrain.WaterSurface) + 1f;
        float b = origin.Dot(direction);
        float discriminant = b * b - origin.LengthSquared() + outerRadius * outerRadius;
        if (discriminant < 0f) return false;
        float root = MathF.Sqrt(discriminant);
        float start = Math.Max(0f, -b - root), end = -b + root;
        if (end <= start) return false;
        bool pickWater = includeWater && origin.Length() >= radius + _observationTerrain.WaterSurface;
        float SurfaceDistance(float distance)
        {
            Vector3 point = origin + direction * distance;
            NumericsVector2 map = PlanetProjection.WorldToMap(point, _world.Config.WorldSize);
            double height = _observationTerrain.Height(map);
            if (pickWater) height = Math.Max(height, _observationTerrain.WaterSurface);
            return point.Length() - radius - (float)height;
        }
        float previous = start;
        float previousDistance = SurfaceDistance(start);
        for (int step = 1; step <= 128; step++)
        {
            float current = Mathf.Lerp(start, end, step / 128f);
            float distance = SurfaceDistance(current);
            if (previousDistance >= 0f && distance <= 0f)
            {
                float lo = previous, hi = current;
                for (int iteration = 0; iteration < 12; iteration++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (SurfaceDistance(mid) > 0f) lo = mid; else hi = mid;
                }
                hit = origin + direction * ((lo + hi) * 0.5f);
                return true;
            }
            previous = current;
            previousDistance = distance;
        }
        return false;
    }

    private float GetMinimumCameraDistance()
    {
        if (_selectedId is not null)
        {
            foreach (OrganismPresentationState organism in _snapshot.Organisms)
            {
                if (organism.Id == _selectedId.Value)
                    return Math.Max(2.2f, (float)organism.Body.BoundingRadius * 3.1f);
            }
        }
        return 2.2f;
    }

    private void UpdateCameraTargetHeight()
    {
        NumericsVector2 map = CameraMapPosition;
        float height = (float)Math.Max(_observationTerrain.Height(map), _observationTerrain.WaterSurface);
        _cameraTarget = PlanetProjection.MapToWorld(map, height + 0.5f, _world.Config.WorldSize);
    }

    private void ToggleFullscreen()
    {
        Window window = GetWindow();
        window.Mode = window.Mode == Window.ModeEnum.Fullscreen
            ? Window.ModeEnum.Windowed
            : Window.ModeEnum.Fullscreen;
    }

    private void OpenMorphologyLab() =>
        GetTree().ChangeSceneToFile("res://scenes/MorphologyLab.tscn");

    private void BuildSceneLighting()
    {
        DirectionalLight3D sun = new()
        {
            RotationDegrees = new Vector3(-58f, -32f, 0f),
            LightColor = new Color("fff0d4"),
            LightEnergy = 1.15f,
            ShadowEnabled = true
        };
        AddChild(sun);
        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(28f, 140f, 0f),
            LightColor = new Color("b6d3e7"),
            LightEnergy = 0.70f,
            ShadowEnabled = false
        });

        WorldEnvironment worldEnvironment = new()
        {
            Environment = new global::Godot.Environment
            {
                BackgroundMode = global::Godot.Environment.BGMode.Color,
                BackgroundColor = new Color("07111e"),
                AmbientLightSource = global::Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color("c0d3df"),
                AmbientLightEnergy = 0.70f,
                TonemapMode = global::Godot.Environment.ToneMapper.Filmic
            }
        };
        AddChild(worldEnvironment);
    }

    private void RunTrackingSmoke()
    {
        CompleteSimulationBatch(wait: true);
        _paused = true;
        _world.Run(100);
        RefreshSnapshotAndWorld();
        ulong before = _world.CaptureSnapshot().StateFingerprint;
        SelectRandomIndividual();
        bool random = _trackedId.HasValue && _selectedId == _trackedId && _world.CaptureSnapshot().StateFingerprint == before;
        ulong id = _trackedId!.Value;
        Organism target = _world.Organisms.First(o => o.Id == id);
        NumericsVector2 next = SphericalWorld.OffsetPosition(target.Position, new NumericsVector2(1, 0), _world.Config.WorldSize);
        _world.RelocateForMediumDiagnostic(id, next, target.Depth);
        RefreshSnapshotAndWorld();
        UpdateTrackingCamera(1.0);
        bool follows = _cameraTarget.DistanceTo(_trackingTarget) < 0.01f;
        StopTracking();
        SelectNearest(_camera.UnprojectPosition(_trackingTarget));
        bool mapSelect = _trackedId.HasValue && _trackedId == _selectedId;
        _cameraDistance = PlanetProjection.Radius(_world.Config.WorldSize) * 1.61f;
        UpdateTrackingCamera(0.1);
        bool zoomStops = _trackedId is null;
        _cameraDistance = 18f;
        UpdateTrackingCamera(0.1);
        bool noRestart = _trackedId is null;
        SelectAndFocus(id);
        for (int step = 0; step < 10 && _world.Organisms.Any(o => o.Id == id); step++)
        {
            Organism victim = _world.Organisms.First(o => o.Id == id);
            foreach (BodyRegion region in victim.Body.Regions.ToArray()) victim.Body.AddDamage(region.RegionId, 1);
            _world.Step();
        }
        RefreshSnapshotAndWorld();
        bool deathStops = !_world.Organisms.Any(o => o.Id == id) && _trackedId is null;
        bool passed = random && follows && mapSelect && zoomStops && noRestart && deathStops;
        GD.Print($"TRACKING_SMOKE {(passed ? "PASS" : "FAIL")} random={random} follows={follows} map_select={mapSelect} zoom_stops={zoomStops} no_restart={noRestart} death_stops={deathStops}");
        GetTree().Quit(passed ? 0 : 1);
    }

    private async void RunWorkerSmoke()
    {
        SetSpeed(100);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (_snapshot.Statistics.StepIndex < 60 &&
            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds < 10)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        _paused = true;
        CompleteSimulationBatch(wait: true);
        RefreshSnapshotAndWorld();
        long steps = _world.StepIndex;
        SimulationWorld reference = new(_world.Config, _worldSeed, 24);
        reference.Run((int)steps);
        bool deterministic = steps >= 60 &&
            reference.CaptureSnapshot().StateFingerprint == _world.CaptureSnapshot().StateFingerprint;
        _cataloguePanel.Toggle();
        long pausedStep = _world.StepIndex;
        for (int frame = 0; frame < 3; frame++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        bool pauseStable = _world.StepIndex == pausedStep;
        _cataloguePanel.Toggle();
        SetSpeed(1);
        _Process(FixedDelta);
        SetTool(ToolMode.Minerals);
        ApplyBrush(new NumericsVector2(256, 256), 1);
        bool commandSerialized = _simulationBatch is null && _world.RecentInterventions.Count == 1;
        _Process(FixedDelta);
        ResetWorld(24, 0, "工作线程重置诊断");
        _paused = true;
        bool resetSafe = _simulationBatch is null && _world.StepIndex == 0;
        bool passed = deterministic && pauseStable && commandSerialized && resetSafe;
        GD.Print($"WORKER_SMOKE {(passed ? "PASS" : "FAIL")} steps={steps} deterministic={deterministic} " +
            $"pause={pauseStable} command_serialized={commandSerialized} reset={resetSafe}");
        GetTree().Quit(passed ? 0 : 1);
    }

    private void RunHeadlessInteractionSmoke()
    {
        ResetWorld(300, 80, "观察负载 300（冒烟测试）");
        SingleStep();
        SetSpeed(10);
        CycleHeatmap();
        SetTool(ToolMode.Minerals);
        ApplyBrush(new NumericsVector2(256f, 256f), 1.0);
        SetTool(ToolMode.Temperature);
        ApplyBrush(new NumericsVector2(276f, 256f), -1.0);
        OrganismPresentationState targetOrganism = _snapshot.Organisms[0];
        SetCameraAnchor(targetOrganism.Position, LowPolyWorldRenderer.OrganismElevation(targetOrganism));
        _cameraDistance = 48f;
        UpdateCameraTransform();
        SelectNearest(_camera.UnprojectPosition(_cameraTarget));
        RefreshSnapshotAndWorld();
        RefreshStatistics();
        RefreshInspector();

        SimulationSnapshot statistics = _snapshot.Statistics;
        double tolerance = Math.Max(1e-8, Math.Abs(statistics.InitialMatter) * 1e-10);
        Vector3 expectedForward = _cameraSurfaceFrame * new Vector3(-MathF.Sin(_cameraYaw), 0f, -MathF.Cos(_cameraYaw));
        Vector3 viewForward = _cameraTarget - _camera.Position;
        Vector3 cameraNormal = _cameraTarget.Normalized();
        viewForward -= cameraNormal * viewForward.Dot(cameraNormal);
        bool forwardAligned = viewForward.Normalized().Dot(expectedForward.Normalized()) > 0.999f;
        Vector3 beforeForward = _cameraTarget;
        ApplyCameraMovement(GetCameraMovement(w: true, s: false, a: false, d: false), 0.1);
        bool wMovesForward = (_cameraTarget - beforeForward).Dot(expectedForward) > 0f;
        Vector3 beforeBackward = _cameraTarget;
        ApplyCameraMovement(GetCameraMovement(w: false, s: true, a: false, d: false), 0.1);
        bool sMovesBackward = (_cameraTarget - beforeBackward).Dot(expectedForward) < 0f;

        _hud.Visible = false;
        float yawBeforeDrag = _cameraYaw;
        _Input(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true });
        _Input(new InputEventMouseMotion { Relative = new Vector2(24f, -8f) });
        _Input(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false });
        _hud.Visible = true;
        bool rightDragRotates = Math.Abs(_cameraYaw - yawBeforeDrag) > 0.01f && !_rightDragging;

        float distanceBeforeWheel = _cameraDistance;
        _UnhandledInput(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true });
        bool closeZoomAvailable = GetMinimumCameraDistance() < 12f && _cameraDistance < distanceBeforeWheel;

        _hud.ApplyResponsiveLayout(800f, force: true);
        (bool mediumTool, bool mediumInspector) = _hud.VisiblePanels;
        _hud.ApplyResponsiveLayout(500f, force: true);
        (bool narrowTool, bool narrowInspector) = _hud.VisiblePanels;
        _hud.ApplyResponsiveLayout(1280f, force: true);
        (bool wideTool, bool wideInspector) = _hud.VisiblePanels;
        bool responsivePanels = mediumTool && !mediumInspector &&
            !narrowTool && !narrowInspector && wideTool && wideInspector;
        bool movementPaid = statistics.AverageSpeed > 0.0 && statistics.CumulativeMovementEnergy > 0.0;
        bool aquaticDepthValid = _snapshot.Organisms.All(organism =>
            organism.Environment.WaterDepth > 0.0 && organism.Depth > 0f &&
            organism.Depth <= organism.Environment.WaterDepth + 1e-5);
        double oxygenTolerance = Math.Max(1e-8, Math.Abs(statistics.InitialOxygen) * 1e-10);
        bool oxygenBudget = Math.Abs(statistics.OxygenError) <= oxygenTolerance;
        ObserveFunctions();
        FunctionCatalogue catalogueCopy = new(false, false);
        catalogueCopy.RestoreJson(_catalogue.ExportJson());
        ObservedFunction? example = catalogueCopy.Entries.FirstOrDefault();
        bool catalogueRoundTrip = example is not null;
        if (example is not null)
        {
            catalogueCopy.ToggleFavorite(example.Key);
            FunctionCatalogue restored = new(false, false);
            restored.RestoreJson(catalogueCopy.ExportJson());
            catalogueRoundTrip = restored.Entries.Any(entry => entry.Key == example.Key && entry.Favorite) &&
                CanFocusCatalogueEntry(example);
            string previousWorld = example.WorldId;
            example.WorldId = "different-world";
            catalogueRoundTrip &= !CanFocusCatalogueEntry(example);
            example.WorldId = previousWorld;
        }
        bool wasPaused = _paused;
        _cataloguePanel.Toggle();
        bool cataloguePause = _paused && _cataloguePanel.Visible;
        _cataloguePanel.Toggle();
        cataloguePause &= _paused == wasPaused && !_cataloguePanel.Visible;
        string archiveTestPath = "user://catalogue-smoke-" + Guid.NewGuid().ToString("N") + ".json";
        bool catalogueDisk;
        try
        {
            FunctionCatalogue disk = new(false, true, archiveTestPath);
            disk.BeginWorld();
            disk.Observe("test", "诊断记录", "作用", "引导", DefaultSeed, 1, 0, 1, 123);
            disk.Save();
            disk.ToggleFavorite("test");
            disk.Save();
            FunctionCatalogue loaded = new(true, true, archiveTestPath);
            catalogueDisk = loaded.Entries.SingleOrDefault()?.Favorite == true;
        }
        finally
        {
            System.IO.File.Delete(ProjectSettings.GlobalizePath(archiveTestPath));
            System.IO.File.Delete(ProjectSettings.GlobalizePath(archiveTestPath + ".tmp"));
        }
        bool passed =
            statistics.StepIndex == 81 &&
            statistics.Population > 0 &&
            statistics.Population == 300 + statistics.CumulativeBirths - statistics.CumulativeDeaths &&
            statistics.TotalBodyRegions >= statistics.Population &&
            _world.RecentInterventions.Count == 2 &&
            Math.Abs(statistics.MatterError) <= tolerance &&
            _renderedRegions > 0 &&
            _selectedId is not null &&
            forwardAligned &&
            wMovesForward &&
            sMovesBackward &&
            rightDragRotates &&
            closeZoomAvailable &&
            responsivePanels &&
            movementPaid &&
            aquaticDepthValid &&
            oxygenBudget && catalogueRoundTrip && cataloguePause && catalogueDisk;
        GD.Print(
            $"STAGE3_SMOKE {(passed ? "PASS" : "FAIL")} " +
            $"step={statistics.StepIndex} population={statistics.Population} " +
            $"body_regions={statistics.TotalBodyRegions} render_instances={_renderedRegions} " +
            $"interventions={_world.RecentInterventions.Count} heatmap={_heatmapMode} " +
            $"forward_aligned={forwardAligned} w_forward={wMovesForward} s_backward={sMovesBackward} " +
            $"right_drag={rightDragRotates} close_zoom={closeZoomAvailable} responsive={responsivePanels} " +
            $"movement_paid={movementPaid} average_speed={statistics.AverageSpeed:F4} " +
            $"aquatic_depth={aquaticDepthValid} oxygen_budget={oxygenBudget} " +
            $"catalogue_roundtrip={catalogueRoundTrip} catalogue_pause={cataloguePause} catalogue_disk={catalogueDisk} " +
            $"matter_error={statistics.MatterError:E6} oxygen_error={statistics.OxygenError:E6}");
        GetTree().Quit(passed ? 0 : 1);
    }

    private async void CaptureFunctionCatalogue()
    {
        _world.Run(80);
        RefreshSnapshotAndWorld();
        ObserveFunctions();
        _cataloguePanel.Toggle();
        for (int frame = 0; frame < 6; frame++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        string directory = ProjectSettings.GlobalizePath("res://artifacts");
        DirAccess.MakeDirRecursiveAbsolute(directory);
        string path = System.IO.Path.Combine(directory, "function-catalogue.png");
        Error error = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"CAPTURE_FUNCTION_CATALOGUE {error} entries={_catalogue.Entries.Count} path={path}");
        GetTree().Quit(error == Error.Ok && _catalogue.Entries.Count > 0 ? 0 : 1);
    }

    private async void CapturePlanet()
    {
        CompleteSimulationBatch(wait: true);
        _paused = true;
        _hud.SetPaused(true);
        _world.Run(100);
        SetCameraAnchor(new NumericsVector2(_world.Config.WorldSize * 0.5f), 0f);
        float radius = PlanetProjection.Radius(_world.Config.WorldSize);
        _cameraDistance = radius * 2.7f;
        UpdateCameraTransform();
        _resourceRenderAccumulator = 1;
        RefreshSnapshotAndWorld();
        RefreshStatistics();
        string directory = ProjectSettings.GlobalizePath("res://artifacts");
        DirAccess.MakeDirRecursiveAbsolute(directory);
        async Task<Error> Save(string name)
        {
            for (int frame = 0; frame < 8; frame++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            return GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(directory, name));
        }
        Error overview = await Save("planet-overview.png");
        bool overviewClean = _worldRenderer.ResourceCounts.Total == 0 && _worldRenderer.VisibleOrganismCount == 0;
        bool picking = TryGetWorldPoint(_camera.UnprojectPosition(Vector3.Zero), out NumericsVector2 hitMap) &&
            SphericalWorld.Distance(hitMap, CameraMapPosition, _world.Config.WorldSize) < 1.0;
        Vector3 oldNormal = _cameraTarget.Normalized();
        Vector3 behind = -oldNormal * radius;
        Vector3 ray = (behind - _camera.Position).Normalized();
        bool backOccluded = TrySurfaceRay(_camera.Position, ray, false, out Vector3 solid) &&
            solid.DistanceTo(_camera.Position) + 1f < behind.DistanceTo(_camera.Position);
        OrbitGlobe(new Vector2(MathF.PI / 0.006f, 0f));
        UpdateCameraTransform();
        _resourceRenderAccumulator = 1;
        RefreshSnapshotAndWorld();
        bool otherHemisphere = oldNormal.Dot(_cameraTarget.Normalized()) < -0.9f;
        Error reverse = await Save("planet-farside.png");
        OrganismPresentationState selected = _snapshot.Organisms[0];
        _selectedId = selected.Id;
        SetCameraAnchor(selected.Position, LowPolyWorldRenderer.OrganismElevation(selected));
        _cameraDistance = Math.Max(5f, (float)selected.Body.BoundingRadius * 5f);
        UpdateCameraTransform();
        _resourceRenderAccumulator = 1;
        RefreshSnapshotAndWorld();
        RefreshInspector();
        _hud.RevealInspector();
        Error closeup = await Save("planet-closeup.png");
        bool cameraFinite = _camera.Position.IsFinite() && _camera.GlobalBasis.IsFinite();
        bool passed = overview == Error.Ok && reverse == Error.Ok && closeup == Error.Ok &&
            picking && backOccluded && otherHemisphere && cameraFinite && overviewClean;
        GD.Print($"PLANET_VIEW {(passed ? "PASS" : "FAIL")} picking={picking} back_occluded={backOccluded} " +
            $"orbit_other_hemisphere={otherHemisphere} camera_finite={cameraFinite} overview_clean={overviewClean} radius={radius:F2}");
        GetTree().Quit(passed ? 0 : 1);
    }

    private async void CaptureFoodWeb()
    {
        CompleteSimulationBatch(wait: true);
        _paused = true;
        _hud.SetPaused(true);
        _world.Run(100);
        SetCameraAnchor(new NumericsVector2(_world.Config.WorldSize * 0.5f), 0f);
        _cameraDistance = PlanetProjection.Radius(_world.Config.WorldSize) * 2.7f;
        UpdateCameraTransform();
        _resourceRenderAccumulator = 1;
        RefreshSnapshotAndWorld();
        RefreshStatistics();
        for (int frame = 0; frame < 8; frame++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        string directory = ProjectSettings.GlobalizePath("res://artifacts");
        DirAccess.MakeDirRecursiveAbsolute(directory);
        Error saved = GetViewport().GetTexture().GetImage().SavePng(
            System.IO.Path.Combine(directory, "food-web-map.png"));
        EnvironmentResourceSnapshot resources = _snapshot.Resources!;
        bool overviewClean = _worldRenderer.ResourceCounts.Total == 0;
        _cameraDistance = PlanetProjection.Radius(_world.Config.WorldSize) * 0.85f;
        UpdateCameraTransform();
        _resourceRenderAccumulator = 1;
        RefreshSnapshotAndWorld();
        ResourceRenderCounts populated = _worldRenderer.ResourceCounts;
        int cells = resources.Minerals.Length;
        EnvironmentResourceSnapshot empty = resources with
        {
            Minerals = new double[cells], LandPlants = new double[cells], Algae = new double[cells],
            EdibleOrganics = new double[cells], Detritus = new double[cells], MetabolicWaste = new double[cells]
        };
        ResourceRenderCounts exhausted = _worldRenderer.UpdateResources(empty);
        _worldRenderer.UpdateResources(resources);
        int geneticColors = _snapshot.Organisms.Select(o => o.Geometry.Regions[0].Color).Distinct().Count();
        bool passed = saved == Error.Ok && overviewClean && populated.Minerals > 0 && populated.LandPlants > 0 &&
            populated.Algae > 0 && exhausted.Total == 0 && geneticColors > 1;
        GD.Print($"FOOD_WEB_RENDER {(passed ? "PASS" : "FAIL")} resources={populated} exhausted={exhausted.Total} genetic_colors={geneticColors}");
        GetTree().Quit(passed ? 0 : 1);
    }

    private async void CaptureSelectedWorldFrame()
    {
        ResetWorld(300, 80, "观察负载 300（截图）");
        _paused = true;
        OrganismPresentationState selected = _snapshot.Organisms[0];
        _selectedId = selected.Id;
        SetCameraAnchor(selected.Position, LowPolyWorldRenderer.OrganismElevation(selected));
        _cameraPitch = -0.10f;
        _cameraDistance = Math.Max(2.8f, (float)selected.Body.BoundingRadius * 4.0f);
        UpdateCameraTransform();
        RefreshSnapshotAndWorld();
        RefreshStatistics();
        RefreshInspector();
        UpdateModeLabel();

        for (int frame = 0; frame < 6; frame++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        string directory = ProjectSettings.GlobalizePath("res://artifacts");
        DirAccess.MakeDirRecursiveAbsolute(directory);
        string path = System.IO.Path.Combine(directory, "phase2-selected-organism.png");
        Error error = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"CAPTURE_STAGE2_WORLD {(error == Error.Ok ? "PASS" : "FAIL")} path={path} error={error}");
        GetTree().Quit(error == Error.Ok ? 0 : 1);
    }

    private async void RunStage2PerformanceProfile(int ancestors, int preRunSteps)
    {
        ResetWorld(ancestors, preRunSteps, $"性能剖面 {ancestors}");
        _paused = true;
        for (int frame = 0; frame < 12; frame++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        const int cycles = 30;
        long allocationStart = GC.GetTotalAllocatedBytes(true);
        long meshBuildStart = _worldRenderer.SkinMeshesBuilt;
        _world.PerformanceMetrics.Reset();
        _world.CollectPerformanceMetrics = true;
        double stepMilliseconds = 0, snapshotMilliseconds = 0, renderMilliseconds = 0;
        long stepAllocated = 0, snapshotAllocated = 0, renderAllocated = 0;
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            long categoryAllocation = GC.GetAllocatedBytesForCurrentThread();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            _world.Step();
            stepMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            stepAllocated += GC.GetAllocatedBytesForCurrentThread() - categoryAllocation;
            categoryAllocation = GC.GetAllocatedBytesForCurrentThread();
            started = System.Diagnostics.Stopwatch.GetTimestamp();
            _snapshot = _world.CapturePresentationSnapshot();
        RefreshTrackingTarget();
            snapshotMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            snapshotAllocated += GC.GetAllocatedBytesForCurrentThread() - categoryAllocation;
            categoryAllocation = GC.GetAllocatedBytesForCurrentThread();
            started = System.Diagnostics.Stopwatch.GetTimestamp();
            _renderedRegions = _worldRenderer.UpdateOrganisms(_snapshot, _selectedId,
                CameraMapPosition);
            renderMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            renderAllocated += GC.GetAllocatedBytesForCurrentThread() - categoryAllocation;
        }
        long allocated = GC.GetTotalAllocatedBytes(false) - allocationStart;
        _world.CollectPerformanceMetrics = false;
        SimulationPerformanceMetrics profileMetrics = _world.PerformanceMetrics;
        long meshesBuilt = _worldRenderer.SkinMeshesBuilt - meshBuildStart;

        ResetWorld(ancestors, preRunSteps, $"1× 帧采样 {ancestors}");
        OrganismPresentationState trackedStart = _snapshot.Organisms[0];
        _selectedId = trackedStart.Id;
        SetCameraAnchor(trackedStart.Position, LowPolyWorldRenderer.OrganismElevation(trackedStart));
        _cameraDistance = 60f;
        UpdateCameraTransform();
        RefreshSnapshotAndWorld();
        _paused = false;
        for (int frame = 0; frame < 30; frame++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        List<double> frameTimes = new(4096);
        long previous = System.Diagnostics.Stopwatch.GetTimestamp();
        long samplingStarted = previous;
        long firstStep = _snapshot.Statistics.StepIndex;
        long steadyMeshStart = _worldRenderer.SkinMeshesBuilt;
        trackedStart = _snapshot.Organisms.First(organism => organism.Id == trackedStart.Id);
        ulong activeId=0;int activeRegionId=-1;
        double activeAngleMin=double.PositiveInfinity,activeAngleMax=double.NegativeInfinity;
        double activeLengthMin=double.PositiveInfinity,activeLengthMax=double.NegativeInfinity;
        double activeMatterMin=double.PositiveInfinity,activeMatterMax=double.NegativeInfinity;
        System.Numerics.Vector2 activeStartPosition=default,activeEndPosition=default;
        double samplingDuration=ancestors<=24?10.0:30.0;
        while (System.Diagnostics.Stopwatch.GetElapsedTime(samplingStarted).TotalSeconds < samplingDuration)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            frameTimes.Add(System.Diagnostics.Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
            previous = now;
            if(System.Diagnostics.Stopwatch.GetElapsedTime(samplingStarted).TotalSeconds>=samplingDuration-10.0)
            {
                if(activeId==0)
                {
                    OrganismPresentationState candidate=_snapshot.Organisms
                        .Where(o=>o.Maturity>=0.95&&o.Regions.Count>1)
                        .OrderByDescending(o=>o.Maturity).ThenBy(o=>o.Id).FirstOrDefault();
                    if(candidate.Id!=0)
                    {
                        activeId=candidate.Id;
                        activeRegionId=candidate.RegionInventories.OrderByDescending(r=>r.Activation)
                            .ThenBy(r=>r.RegionId).First().RegionId;
                        activeStartPosition=candidate.Position;
                    }
                }
                OrganismPresentationState active=_snapshot.Organisms.FirstOrDefault(o=>o.Id==activeId);
                if(active.Id!=0)
                {
                    BodyVisualRegion region=active.Regions.First(r=>r.RegionId==activeRegionId);
                    activeAngleMin=Math.Min(activeAngleMin,region.Angle);activeAngleMax=Math.Max(activeAngleMax,region.Angle);
                    activeLengthMin=Math.Min(activeLengthMin,region.Length);activeLengthMax=Math.Max(activeLengthMax,region.Length);
                    activeMatterMin=Math.Min(activeMatterMin,active.Body.TotalMatter);activeMatterMax=Math.Max(activeMatterMax,active.Body.TotalMatter);
                    activeEndPosition=active.Position;
                }
            }
        }
        _paused = true;
        CompleteSimulationBatch(wait: true);
        RefreshSnapshotAndWorld();
        frameTimes.Sort();
        double sampledSeconds = System.Diagnostics.Stopwatch.GetElapsedTime(samplingStarted).TotalSeconds;
        double averageFrame = frameTimes.Average();
        double p95 = frameTimes[(int)(frameTimes.Count * 0.95)];
        OrganismPresentationState trackedEnd = _snapshot.Organisms.First(organism => organism.Id == trackedStart.Id);
        double displacement = SphericalWorld.Distance(trackedStart.Position, trackedEnd.Position, _world.Config.WorldSize);
        double poseDelta = trackedStart.Regions.Join(trackedEnd.Regions,
            before => before.RegionId, after => after.RegionId,
            (before, after) => System.Numerics.Vector2.Distance(before.LocalCenter, after.LocalCenter) +
                Math.Abs(before.Length - after.Length) + Math.Abs(before.Angle - after.Angle)).DefaultIfEmpty().Max();
        long sampledSteps = _world.StepIndex - firstStep;
        int visibleFacing=_worldRenderer.VisibleOrganismCount;
        Vector3 towardTarget=(_cameraTarget-_camera.Position).Normalized();
        _camera.LookAt(_camera.Position-towardTarget*100f,_camera.GlobalBasis.Y);
        RefreshSnapshotAndWorld();
        await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        int visibleAway=_worldRenderer.VisibleOrganismCount;
        GD.Print($"STAGE2_PROFILE population={_snapshot.Statistics.Population} cycles={cycles} " +
            $"step_ms={stepMilliseconds / cycles:F3} snapshot_ms={snapshotMilliseconds / cycles:F3} " +
            $"render_submit_ms={renderMilliseconds / cycles:F3} allocated_mb={allocated / 1048576.0:F2} " +
            $"alloc_step_kb={stepAllocated / cycles / 1024.0:F1} alloc_snapshot_kb={snapshotAllocated / cycles / 1024.0:F1} " +
            $"alloc_render_kb={renderAllocated / cycles / 1024.0:F1} " +
            $"parts_ms={profileMetrics.EnvironmentMilliseconds/cycles:F2}/" +
            $"{profileMetrics.SeparationMilliseconds/cycles:F2}/" +
            $"{profileMetrics.ExchangeMilliseconds/cycles:F2}/" +
            $"{profileMetrics.ControlTransportMilliseconds/cycles:F2}/" +
            $"{profileMetrics.MechanicsMilliseconds/cycles:F2}/" +
            $"{profileMetrics.MetabolismGrowthMilliseconds/cycles:F2} " +
            $"skin_meshes_built={meshesBuilt} frame_avg_ms={averageFrame:F3} frame_p95_ms={p95:F3} " +
            $"frame_max_ms={frameTimes[^1]:F3} sampled_frames={frameTimes.Count} sampled_steps={sampledSteps} " +
            $"actual_scale={sampledSteps * FixedDelta / sampledSeconds:F3}x steady_mesh_builds={_worldRenderer.SkinMeshesBuilt-steadyMeshStart} " +
            $"visible={visibleFacing} cull_visible_away={visibleAway} tracked_displacement={displacement:F5} pose_delta={poseDelta:F5} errors=0");
        if(activeId!=0)
            GD.Print($"STAGE2_ACTIVE organism={activeId} region={activeRegionId} angle_pp={activeAngleMax-activeAngleMin:F6} " +
                $"length_pp={activeLengthMax-activeLengthMin:F6} matter_drift={activeMatterMax-activeMatterMin:F6} " +
                $"displacement={SphericalWorld.Distance(activeStartPosition,activeEndPosition,_world.Config.WorldSize):F6} window_s=10");
        GetTree().Quit();
    }

    private static string HeatmapName(HeatmapMode mode) => mode switch
    {
        HeatmapMode.Natural => "自然",
        HeatmapMode.Height => "高度",
        HeatmapMode.Temperature => "温度",
        HeatmapMode.Light => "光照",
        HeatmapMode.Minerals => "矿物",
        HeatmapMode.Detritus => "残骸",
        HeatmapMode.DissolvedOxygen => "溶解氧",
        _ => "空气氧"
    };

    private enum ToolMode
    {
        Select,
        Minerals,
        Temperature
    }
}
