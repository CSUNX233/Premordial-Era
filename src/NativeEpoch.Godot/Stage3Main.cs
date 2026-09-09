using Godot;
using NativeEpoch.Simulation;
using NumericsVector2 = System.Numerics.Vector2;

namespace NativeEpoch.Godot;

public sealed partial class Stage3Main : Node3D
{
    private const ulong DefaultSeed = 20260908;
    private const double FixedDelta = 0.1;
    private const int MaximumStepsPerFrame = 512;
    private SimulationWorld _world = null!;
    private WorldPresentationSnapshot _snapshot = null!;
    private LowPolyWorldRenderer _worldRenderer = null!;
    private Stage3Hud _hud = null!;
    private Camera3D _camera = null!;
    private Vector3 _cameraTarget = Vector3.Zero;
    private float _cameraYaw = -0.55f;
    private float _cameraPitch = -0.82f;
    private float _cameraDistance = 410f;
    private bool _rightDragging;
    private bool _paused;
    private double _timeScale = 1.0;
    private double _stepAccumulator;
    private double _renderAccumulator;
    private double _poseInterpolationElapsed;
    private double _statisticsAccumulator;
    private double _inspectorAccumulator;
    private double _rateWindowSeconds;
    private long _rateWindowSteps;
    private double _achievedScale;
    private int _renderedRegions;
    private ulong? _selectedId;
    private ToolMode _toolMode = ToolMode.Select;
    private HeatmapMode _heatmapMode = HeatmapMode.Natural;
    private float _brushRadius = 28f;

    public override void _Ready()
    {
        GetWindow().Title = "原生纪 · 阶段 3 低模世界观察台";
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
        else if (OS.GetCmdlineUserArgs().Contains("--capture-stage2-world"))
            CaptureSelectedWorldFrame();
    }

    public override void _Process(double delta)
    {
        if (_rightDragging && !Input.IsMouseButtonPressed(MouseButton.Right))
            _rightDragging = false;
        UpdateCameraMovement(delta);
        if (!_paused)
        {
            _stepAccumulator += delta * _timeScale;
            int executed = 0;
            while (_stepAccumulator >= FixedDelta && executed < MaximumStepsPerFrame)
            {
                _world.Step();
                _stepAccumulator -= FixedDelta;
                executed++;
            }
            _rateWindowSteps += executed;
        }

        _rateWindowSeconds += delta;
        if (_rateWindowSeconds >= 1.0)
        {
            _achievedScale = (_rateWindowSteps * FixedDelta) / _rateWindowSeconds;
            _rateWindowSeconds = 0.0;
            _rateWindowSteps = 0;
        }

        _renderAccumulator += delta;
        _poseInterpolationElapsed += delta;
        double renderInterval = _timeScale >= 100.0 ? 0.20 : 0.10;
        if (_renderAccumulator >= renderInterval)
        {
            RefreshSnapshotAndWorld();
            _renderAccumulator = 0.0;
            _poseInterpolationElapsed = 0.0;
        }
        _worldRenderer.InterpolateContinuousSkins(
            (float)Math.Clamp(_poseInterpolationElapsed / renderInterval, 0.0, 1.0));

        _statisticsAccumulator += delta;
        if (_statisticsAccumulator >= 0.50)
        {
            RefreshStatistics();
            _statisticsAccumulator = 0.0;
        }

        _inspectorAccumulator += delta;
        if (_inspectorAccumulator >= 0.20)
        {
            RefreshInspector();
            _inspectorAccumulator = 0.0;
        }
    }

    public override void _Input(InputEvent inputEvent)
    {
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
            _cameraYaw -= motion.Relative.X * 0.006f;
            _cameraPitch = Math.Clamp(_cameraPitch - (motion.Relative.Y * 0.006f), -1.42f, -0.12f);
            UpdateCameraTransform();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseButton mouseButton)
        {
            if (mouseButton.Pressed && mouseButton.ButtonIndex == MouseButton.WheelUp)
            {
                _cameraDistance = Math.Max(GetMinimumCameraDistance(), _cameraDistance * 0.82f);
                UpdateCameraTransform();
            }
            else if (mouseButton.Pressed && mouseButton.ButtonIndex == MouseButton.WheelDown)
            {
                _cameraDistance = Math.Min(900f, _cameraDistance * 1.18f);
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
        _hud.PauseRequested += TogglePause;
        _hud.StepRequested += SingleStep;
        _hud.SpeedRequested += SetSpeed;
        _hud.SmallWorldRequested += () => ResetWorld(24, 0, "少量祖先 24");
        _hud.ObservationWorldRequested += () => ResetWorld(300, 80, "观察负载 300（真实预演 8 秒）");
        _hud.SelectToolRequested += () => SetTool(ToolMode.Select);
        _hud.MineralToolRequested += () => SetTool(ToolMode.Minerals);
        _hud.TemperatureToolRequested += () => SetTool(ToolMode.Temperature);
        _hud.HeatmapRequested += CycleHeatmap;
        _hud.MediumDiagnosticRequested += MoveSelectedToLandDiagnostic;
        _hud.MorphologyLabRequested += OpenMorphologyLab;
    }

    private void ResetWorld(int ancestors, int preRunSteps, string label)
    {
        SimulationConfig config = new()
        {
            WorldSize = 512f,
            EnvironmentGridSize = 128,
            MaxPopulation = 5_000,
            // Default-seed late samples at 1800/2100/2400 s: 932/929/975.
            InitialMineralScale = 1.0,
            ResourceBudgetReferenceAncestors = 24
        };
        _world = new SimulationWorld(config, DefaultSeed, ancestors);
        if (preRunSteps > 0)
            _world.Run(preRunSteps);
        _selectedId = null;
        _stepAccumulator = 0.0;
        _heatmapMode = HeatmapMode.Natural;
        _worldRenderer.BuildEnvironment(_world.Environment, config.WorldSize, _heatmapMode);
        RefreshSnapshotAndWorld();
        RefreshStatistics();
        RefreshInspector();
        _hud.UpdateStatus($"已载入 {label}。资源承载目标约 1000 只，随演化与环境变化；不同起始数量共用同一物质预算。");
        UpdateModeLabel();
    }

    private void TogglePause()
    {
        _paused = !_paused;
        _hud.SetPaused(_paused);
        UpdateModeLabel();
    }

    private void SingleStep()
    {
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
        int count = Enum.GetValues<HeatmapMode>().Length;
        _heatmapMode = (HeatmapMode)(((int)_heatmapMode + 1) % count);
        _worldRenderer.BuildEnvironment(_world.Environment, _world.Config.WorldSize, _heatmapMode);
        _hud.UpdateStatus($"环境图层：{HeatmapName(_heatmapMode)}。");
    }

    private void ApplyBrush(NumericsVector2 position, double direction)
    {
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
        if (_selectedId is null && _snapshot.Organisms.Count > 0)
            _selectedId = _snapshot.Organisms[0].Id;
        if (_selectedId is null)
        {
            _hud.UpdateStatus("人工水陆对照无法开始：当前没有存活个体。");
            return;
        }
        NumericsVector2? land = null;
        for (int y = 0; y <= 48 && land is null; y++) for (int x = 0; x <= 48; x++)
        {
            NumericsVector2 candidate = new(
                _world.Config.WorldSize * x / 48f, _world.Config.WorldSize * y / 48f);
            if (_world.Environment.Sample(candidate).WaterDepth <= 0.0)
            { land = candidate; break; }
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
        _hud.UpdateStatus("人工诊断：已把选中个体移至陆地。观察含水、气侧摄氧、失水代价和受限移动；这不是自然上岸演化。");
    }

    private void SelectAndFocus(ulong organismId)
    {
        foreach (OrganismPresentationState organism in _snapshot.Organisms)
        {
            if (organism.Id != organismId) continue;
            _cameraTarget = new Vector3(
                organism.Position.X - (_world.Config.WorldSize * 0.5f),
                LowPolyWorldRenderer.OrganismElevation(organism),
                organism.Position.Y - (_world.Config.WorldSize * 0.5f));
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
            Vector3 worldPosition = new(
                organism.Position.X - (_world.Config.WorldSize * 0.5f),
                LowPolyWorldRenderer.OrganismElevation(organism),
                organism.Position.Y - (_world.Config.WorldSize * 0.5f));
            if (_camera.IsPositionBehind(worldPosition))
                continue;
            Vector2 organismScreen = _camera.UnprojectPosition(worldPosition);
            float distance = organismScreen.DistanceTo(screenPosition);
            if (distance < nearestDistance)
            {
                nearest = organism;
                nearestDistance = distance;
            }
        }

        _selectedId = nearest?.Id;
        if (nearest is not null)
        {
            OrganismPresentationState organism = nearest.Value;
            _cameraTarget = new Vector3(
                organism.Position.X - (_world.Config.WorldSize * 0.5f),
                LowPolyWorldRenderer.OrganismElevation(organism),
                organism.Position.Y - (_world.Config.WorldSize * 0.5f));
            _cameraDistance = Math.Min(_cameraDistance, 18f);
            UpdateCameraTransform();
        }
        RefreshSnapshotAndWorld();
        RefreshInspector();
        _hud.UpdateStatus(nearest is null
            ? "点击位置附近没有生命；可滚轮拉近后重试。"
            : $"已选择并聚焦个体 {nearest.Value.Id}，屏幕命中距离 {nearestDistance:F1}px。 ");
    }

    private void RefreshSnapshotAndWorld()
    {
        _snapshot = _world.CapturePresentationSnapshot();
        _renderedRegions = _worldRenderer.UpdateOrganisms(_snapshot, _selectedId,
            new System.Numerics.Vector2(_cameraTarget.X + _world.Config.WorldSize*0.5f,
                _cameraTarget.Z + _world.Config.WorldSize*0.5f));
        if (_selectedId is not null && !_snapshot.Organisms.Any(organism => organism.Id == _selectedId.Value))
            _selectedId = null;
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
            $"生物量 {stats.OrganismBodyMatter:F1} · 环境矿物 {stats.EnvironmentMinerals:F1} · 残骸/废物 {stats.EnvironmentDetritus + stats.EnvironmentMetabolicWaste:F1} · 拥挤 {crowdedPopulation:N0}\n" +
            $"基因组 {stats.GenomeCount:N0} · 身体区域 {stats.TotalBodyRegions:N0}（渲染实例 {_renderedRegions:N0}" +
            $"{(_worldRenderer.UsesSimplifiedProxies ? "，远景代理" : "，完整区域")}） · " +
            $"均速 {stats.AverageSpeed:F2} · 目标 {_timeScale:0}× / 实际 {_achievedScale:0.0}× · " +
            $"物质误差 {stats.MatterError:E2}");
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
            $"基因区域 {organism.GenomeRegionCount} · 当前身体区域 {organism.Regions.Count}\n" +
            $"年龄 {organism.AgeSeconds:F1}s · 成熟 {organism.Maturity:P1} · 发育 {organism.DevelopmentCompletion:P1}\n" +
            $"能量 {organism.Energy:F3} · 储存物质 {organism.StoredMatter:F3} · 繁殖冷却 {organism.ReproductionCooldownSeconds:F1}s\n" +
            $"探索倾向 {organism.ExplorationDrive:P0} · 食物信号变化 {organism.ForagingTrend:+0.000;-0.000;0.000}\n" +
            $"接触压力 {organism.ContactPressure:P1} · 接触邻居 {organism.ContactNeighborCount}\n" +
            $"互动 {interaction} · 对方 ID {opponent} · 争位强度 {organism.InteractionIntensity:P1}\n" +
            $"本步食物需求满足 {foodSatisfaction}\n" +
            $"身体物质 {organism.Body.TotalMatter:F3} · 质量 {organism.Body.PhysicalMass:F3} · 半径 {organism.Body.BoundingRadius:F3}\n" +
            $"摄光面 {organism.Body.LightCaptureSurface:F3} · 摄取面 {organism.Body.MatterUptakeSurface:F3} · 维护 {organism.Body.MaintenanceEnergyPerSecond:F3}/s\n" +
            $"介质 {(organism.Immersion >= 0.8 ? "水中" : organism.Immersion > 0.05 ? "水线" : "陆地")} · 个体深度 {organism.Depth:F2}/{organism.Environment.WaterDepth:F2} · 浸没 {organism.Immersion:P0}\n" +
            $"含水 {organism.Hydration:P1} · 区域氧 {organism.InternalOxygen:F4}/{organism.OxygenCapacity:F4} · 本步摄氧/耗氧 {organism.OxygenUptakeLastStep:F5}/{organism.OxygenConsumedLastStep:F5}\n" +
            $"代谢产能 {organism.MetabolicEnergyLastStep:F5} · 失水/压力代价 {organism.DehydrationCostLastStep:F5}\n" +
            $"表面样本 外露/遮蔽 {organism.ExposedSurfaceSamples}/{organism.OccludedSurfaceSamples} · 水/气暴露面 {organism.WaterExposedArea:F3}/{organism.AirExposedArea:F3}\n" +
            $"区域库存（最多显示 6 区）：\n{regionalInventory}\n" +
            $"环境：海底 {organism.Environment.TerrainHeight:F2} · 压力 {organism.Environment.Pressure:F3} · 光 {organism.Environment.Light:F3}\n" +
            $"溶解氧可用度 {organism.Environment.DissolvedOxygenAvailability:F3} · 空气氧可用度 {organism.Environment.AirOxygenAvailability:F3}（均为各介质内部无量纲势）\n" +
            $"温度 {organism.Environment.Temperature:F3} · 矿物 {organism.Environment.Minerals:F3} · 代谢废物 {organism.Environment.MetabolicWaste:F3}\n" +
            $"速度 ({organism.Velocity.X:F2}, {organism.Velocity.Y:F2}) · 局部反力 ({organism.LocalActuationForce.X:F3}, {organism.LocalActuationForce.Y:F3}) · 力矩 {organism.ActuationTorque:F3}\n" +
            $"控制输出 收缩 {organism.ControllerOutputs.ContractionActivation:F2} / 通透 {organism.ControllerOutputs.PermeabilityGate:F2} / 分泌 {organism.ControllerOutputs.SecretionActivation:F2}\n" +
            "身体形变与活性表面驱动均消耗局部能量；低能量时减少探索。");
    }

    private void UpdateModeLabel()
    {
        string state = _paused ? "已暂停" : $"运行 {_timeScale:0}×";
        _hud.UpdateMode($"{state} · 固定步 0.1 秒 · 图层 {HeatmapName(_heatmapMode)}");
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
        Vector3 movement = GetCameraMovement(
            Input.IsKeyPressed(Key.W),
            Input.IsKeyPressed(Key.S),
            Input.IsKeyPressed(Key.A),
            Input.IsKeyPressed(Key.D));
        ApplyCameraMovement(movement, delta);
    }

    private Vector3 GetCameraMovement(bool w, bool s, bool a, bool d)
    {
        Vector3 forward = new(-MathF.Sin(_cameraYaw), 0f, -MathF.Cos(_cameraYaw));
        Vector3 right = new(-forward.Z, 0f, forward.X);
        Vector3 movement = Vector3.Zero;
        if (w) movement += forward;
        if (s) movement -= forward;
        if (d) movement += right;
        if (a) movement -= right;
        return movement;
    }

    private void ApplyCameraMovement(Vector3 movement, double delta)
    {
        if (movement.LengthSquared() <= 0f)
            return;

        float speed = (float)(delta * Math.Max(2.0, _cameraDistance * 0.32));
        _cameraTarget += movement.Normalized() * speed;
        float extent = _world.Config.WorldSize * 0.52f;
        _cameraTarget.X = Math.Clamp(_cameraTarget.X, -extent, extent);
        _cameraTarget.Z = Math.Clamp(_cameraTarget.Z, -extent, extent);
        UpdateCameraTargetHeight();
        UpdateCameraTransform();
    }

    private void UpdateCameraTransform()
    {
        float horizontal = _cameraDistance * MathF.Cos(_cameraPitch);
        Vector3 offset = new(
            horizontal * MathF.Sin(_cameraYaw),
            -_cameraDistance * MathF.Sin(_cameraPitch),
            horizontal * MathF.Cos(_cameraYaw));
        _camera.Position = _cameraTarget + offset;
        _camera.LookAt(_cameraTarget, Vector3.Up);
    }

    private bool TryGetWorldPoint(Vector2 screenPosition, out NumericsVector2 worldPoint)
    {
        Vector3 origin = _camera.ProjectRayOrigin(screenPosition);
        Vector3 direction = _camera.ProjectRayNormal(screenPosition);
        if (Math.Abs(direction.Y) < 1e-6f)
        {
            worldPoint = default;
            return false;
        }

        float distance = -origin.Y / direction.Y;
        if (distance <= 0f)
        {
            worldPoint = default;
            return false;
        }

        float half = _world.Config.WorldSize * 0.5f;
        Vector3 hit = origin + (direction * distance);
        for (int iteration = 0; iteration < 4; iteration++)
        {
            float sampleX = hit.X + half;
            float sampleY = hit.Z + half;
            if (sampleX < 0f || sampleX > _world.Config.WorldSize ||
                sampleY < 0f || sampleY > _world.Config.WorldSize)
                break;
            double terrainHeight = _world.Environment.Sample(new NumericsVector2(sampleX, sampleY)).TerrainHeight;
            distance = ((float)terrainHeight - origin.Y) / direction.Y;
            if (distance <= 0f)
                break;
            hit = origin + (direction * distance);
        }
        float worldX = hit.X + half;
        float worldY = hit.Z + half;
        if (worldX < 0f || worldX > _world.Config.WorldSize ||
            worldY < 0f || worldY > _world.Config.WorldSize)
        {
            worldPoint = default;
            return false;
        }

        worldPoint = new NumericsVector2(worldX, worldY);
        return true;
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
        float half = _world.Config.WorldSize * 0.5f;
        float worldX = Math.Clamp(_cameraTarget.X + half, 0f, _world.Config.WorldSize);
        float worldY = Math.Clamp(_cameraTarget.Z + half, 0f, _world.Config.WorldSize);
        EnvironmentSample sample = _world.Environment.Sample(new NumericsVector2(worldX, worldY));
        _cameraTarget.Y = sample.WaterDepth > 0.0
            ? (float)sample.WaterSurface
            : (float)sample.TerrainHeight + 0.5f;
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
            LightColor = new Color("e8f2de"),
            LightEnergy = 1.25f,
            ShadowEnabled = true
        };
        AddChild(sun);

        WorldEnvironment worldEnvironment = new()
        {
            Environment = new global::Godot.Environment
            {
                BackgroundMode = global::Godot.Environment.BGMode.Color,
                BackgroundColor = new Color("081018"),
                AmbientLightSource = global::Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color("9ac5bf"),
                AmbientLightEnergy = 0.62f,
                TonemapMode = global::Godot.Environment.ToneMapper.Filmic
            }
        };
        AddChild(worldEnvironment);
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
        _cameraTarget = new Vector3(
            targetOrganism.Position.X - (_world.Config.WorldSize * 0.5f),
            LowPolyWorldRenderer.OrganismElevation(targetOrganism),
            targetOrganism.Position.Y - (_world.Config.WorldSize * 0.5f));
        _cameraDistance = 48f;
        UpdateCameraTransform();
        SelectNearest(_camera.UnprojectPosition(_cameraTarget));
        RefreshSnapshotAndWorld();
        RefreshStatistics();
        RefreshInspector();

        SimulationSnapshot statistics = _snapshot.Statistics;
        double tolerance = Math.Max(1e-8, Math.Abs(statistics.InitialMatter) * 1e-10);
        Vector3 expectedForward = new(-MathF.Sin(_cameraYaw), 0f, -MathF.Cos(_cameraYaw));
        Vector3 viewForward = (_cameraTarget - _camera.Position) * new Vector3(1f, 0f, 1f);
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
        bool passed =
            statistics.StepIndex == 81 &&
            statistics.Population >= 300 &&
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
            oxygenBudget;
        GD.Print(
            $"STAGE3_SMOKE {(passed ? "PASS" : "FAIL")} " +
            $"step={statistics.StepIndex} population={statistics.Population} " +
            $"body_regions={statistics.TotalBodyRegions} render_instances={_renderedRegions} " +
            $"interventions={_world.RecentInterventions.Count} heatmap={_heatmapMode} " +
            $"forward_aligned={forwardAligned} w_forward={wMovesForward} s_backward={sMovesBackward} " +
            $"right_drag={rightDragRotates} close_zoom={closeZoomAvailable} responsive={responsivePanels} " +
            $"movement_paid={movementPaid} average_speed={statistics.AverageSpeed:F4} " +
            $"aquatic_depth={aquaticDepthValid} oxygen_budget={oxygenBudget} " +
            $"matter_error={statistics.MatterError:E6} oxygen_error={statistics.OxygenError:E6}");
        GetTree().Quit(passed ? 0 : 1);
    }

    private async void CaptureSelectedWorldFrame()
    {
        ResetWorld(300, 80, "观察负载 300（截图）");
        _paused = true;
        OrganismPresentationState selected = _snapshot.Organisms[0];
        _selectedId = selected.Id;
        _cameraTarget = new Vector3(
            selected.Position.X - (_world.Config.WorldSize * 0.5f),
            LowPolyWorldRenderer.OrganismElevation(selected),
            selected.Position.Y - (_world.Config.WorldSize * 0.5f));
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
            snapshotMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            snapshotAllocated += GC.GetAllocatedBytesForCurrentThread() - categoryAllocation;
            categoryAllocation = GC.GetAllocatedBytesForCurrentThread();
            started = System.Diagnostics.Stopwatch.GetTimestamp();
            _renderedRegions = _worldRenderer.UpdateOrganisms(_snapshot, _selectedId,
                new System.Numerics.Vector2(_cameraTarget.X + _world.Config.WorldSize * 0.5f,
                    _cameraTarget.Z + _world.Config.WorldSize * 0.5f));
            renderMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            renderAllocated += GC.GetAllocatedBytesForCurrentThread() - categoryAllocation;
        }
        long allocated = GC.GetTotalAllocatedBytes(false) - allocationStart;
        _world.CollectPerformanceMetrics = false;
        long meshesBuilt = _worldRenderer.SkinMeshesBuilt - meshBuildStart;

        ResetWorld(ancestors, preRunSteps, $"1× 帧采样 {ancestors}");
        OrganismPresentationState trackedStart = _snapshot.Organisms[0];
        _selectedId = trackedStart.Id;
        RefreshSnapshotAndWorld();
        _paused = false;
        for (int frame = 0; frame < 30; frame++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        List<double> frameTimes = new(4096);
        long previous = System.Diagnostics.Stopwatch.GetTimestamp();
        long samplingStarted = previous;
        long firstStep = _world.StepIndex;
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
        frameTimes.Sort();
        double sampledSeconds = System.Diagnostics.Stopwatch.GetElapsedTime(samplingStarted).TotalSeconds;
        double averageFrame = frameTimes.Average();
        double p95 = frameTimes[(int)(frameTimes.Count * 0.95)];
        OrganismPresentationState trackedEnd = _snapshot.Organisms.First(organism => organism.Id == trackedStart.Id);
        double displacement = System.Numerics.Vector2.Distance(trackedStart.Position, trackedEnd.Position);
        double poseDelta = trackedStart.Regions.Join(trackedEnd.Regions,
            before => before.RegionId, after => after.RegionId,
            (before, after) => System.Numerics.Vector2.Distance(before.LocalCenter, after.LocalCenter) +
                Math.Abs(before.Length - after.Length) + Math.Abs(before.Angle - after.Angle)).DefaultIfEmpty().Max();
        long sampledSteps = _world.StepIndex - firstStep;
        int visibleFacing=_worldRenderer.VisibleOrganismCount;
        Vector3 towardTarget=(_cameraTarget-_camera.Position).Normalized();
        _camera.LookAt(_camera.Position-towardTarget*100f,Vector3.Up);
        RefreshSnapshotAndWorld();
        await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        int visibleAway=_worldRenderer.VisibleOrganismCount;
        GD.Print($"STAGE2_PROFILE population={_snapshot.Statistics.Population} cycles={cycles} " +
            $"step_ms={stepMilliseconds / cycles:F3} snapshot_ms={snapshotMilliseconds / cycles:F3} " +
            $"render_submit_ms={renderMilliseconds / cycles:F3} allocated_mb={allocated / 1048576.0:F2} " +
            $"alloc_step_kb={stepAllocated / cycles / 1024.0:F1} alloc_snapshot_kb={snapshotAllocated / cycles / 1024.0:F1} " +
            $"alloc_render_kb={renderAllocated / cycles / 1024.0:F1} " +
            $"parts_ms={_world.PerformanceMetrics.EnvironmentMilliseconds/cycles:F2}/" +
            $"{_world.PerformanceMetrics.SeparationMilliseconds/cycles:F2}/" +
            $"{_world.PerformanceMetrics.ExchangeMilliseconds/cycles:F2}/" +
            $"{_world.PerformanceMetrics.ControlTransportMilliseconds/cycles:F2}/" +
            $"{_world.PerformanceMetrics.MechanicsMilliseconds/cycles:F2}/" +
            $"{_world.PerformanceMetrics.MetabolismGrowthMilliseconds/cycles:F2} " +
            $"skin_meshes_built={meshesBuilt} frame_avg_ms={averageFrame:F3} frame_p95_ms={p95:F3} " +
            $"frame_max_ms={frameTimes[^1]:F3} sampled_frames={frameTimes.Count} sampled_steps={sampledSteps} " +
            $"actual_scale={sampledSteps * FixedDelta / sampledSeconds:F3}x steady_mesh_builds={_worldRenderer.SkinMeshesBuilt-steadyMeshStart} " +
            $"visible={visibleFacing} cull_visible_away={visibleAway} tracked_displacement={displacement:F5} pose_delta={poseDelta:F5} errors=0");
        if(activeId!=0)
            GD.Print($"STAGE2_ACTIVE organism={activeId} region={activeRegionId} angle_pp={activeAngleMax-activeAngleMin:F6} " +
                $"length_pp={activeLengthMax-activeLengthMin:F6} matter_drift={activeMatterMax-activeMatterMin:F6} " +
                $"displacement={System.Numerics.Vector2.Distance(activeStartPosition,activeEndPosition):F6} window_s=10");
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
