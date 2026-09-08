using Godot;

namespace NativeEpoch.Godot;

/// <summary>
/// Independent artificial-input geometry laboratory. It never creates or edits
/// SimulationWorld organisms.
/// </summary>
public sealed partial class MorphologyLab : Node3D
{
    private sealed record SampleView(
        OrganicShapeParameters Parameters,
        GeneratedOrganicMesh Geometry,
        MeshInstance3D Solid,
        MeshInstance3D Wire,
        Label3D Label,
        Vector3 WorldPosition);

    private readonly List<SampleView> _samples = [];
    private Camera3D _camera = null!;
    private Label _details = null!;
    private Label _lodLabel = null!;
    private PanelContainer _detailsPanel = null!;
    private bool _rightDragging;
    private bool _wireframe;
    private bool _highDetail = true;
    private int _selectedIndex;
    private Vector3 _target = Vector3.Zero;
    private float _yaw = -0.55f;
    private float _pitch = -0.43f;
    private float _distance = 32f;
    private bool _compactLayout;

    public override void _Ready()
    {
        GetWindow().Title = "原生纪 · 人工形态样本台";
        BuildLightingAndFloor();
        BuildCamera();
        BuildUi();
        RebuildSamples();
        SelectSample(0, focus: false);
        UpdateCamera();

        if (OS.GetCmdlineUserArgs().Contains("--morphology-smoke"))
            RunSmoke();
    }

    public override void _Process(double delta)
    {
        _ = delta;
        if (_rightDragging && !Input.IsMouseButtonPressed(MouseButton.Right))
            _rightDragging = false;
        ApplyResponsiveLayout();
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
            _yaw -= motion.Relative.X * 0.006f;
            _pitch = Math.Clamp(_pitch - motion.Relative.Y * 0.006f, -1.35f, 1.10f);
            UpdateCamera();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseButton mouseButton && mouseButton.Pressed)
        {
            if (mouseButton.ButtonIndex == MouseButton.WheelUp)
            {
                _distance = Math.Max(2.0f, _distance * 0.82f);
                UpdateCamera();
            }
            else if (mouseButton.ButtonIndex == MouseButton.WheelDown)
            {
                _distance = Math.Min(90f, _distance * 1.18f);
                UpdateCamera();
            }
            else if (mouseButton.ButtonIndex == MouseButton.Left)
            {
                SelectNearest(mouseButton.Position);
            }
        }
        else if (inputEvent is InputEventKey key && key.Pressed && !key.Echo)
        {
            if (key.Keycode is >= Key.Key1 and <= Key.Key6)
            {
                SelectSample((int)(key.Keycode - Key.Key1), focus: true);
                return;
            }

            switch (key.Keycode)
            {
                case Key.W:
                    ToggleWireframe();
                    break;
                case Key.L:
                    ToggleLod();
                    break;
                case Key.Escape:
                case Key.F2:
                    ReturnToWorld();
                    break;
                case Key.F11:
                    GetWindow().Mode = GetWindow().Mode == Window.ModeEnum.Fullscreen
                        ? Window.ModeEnum.Windowed
                        : Window.ModeEnum.Fullscreen;
                    break;
            }
        }
    }

    private void BuildCamera()
    {
        _camera = new Camera3D
        {
            Current = true,
            Near = 0.03f,
            Far = 400f,
            Fov = 52f
        };
        AddChild(_camera);
    }

    private void BuildLightingAndFloor()
    {
        WorldEnvironment worldEnvironment = new();
        worldEnvironment.Environment = new global::Godot.Environment
        {
            BackgroundMode = global::Godot.Environment.BGMode.Color,
            BackgroundColor = new Color("101820"),
            AmbientLightSource = global::Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color("8296a3"),
            AmbientLightEnergy = 0.72f
        };
        AddChild(worldEnvironment);

        DirectionalLight3D key = new()
        {
            RotationDegrees = new Vector3(-56f, -28f, 0f),
            LightColor = new Color("d9f3ec"),
            LightEnergy = 1.25f,
            ShadowEnabled = true
        };
        AddChild(key);

        MeshInstance3D floor = new()
        {
            Position = new Vector3(0, -4.5f, 0),
            Mesh = new PlaneMesh { Size = new Vector2(50f, 34f), SubdivideWidth = 12, SubdivideDepth = 8 },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color("172630"),
                Roughness = 0.92f
            }
        };
        AddChild(floor);
    }

    private void BuildUi()
    {
        Theme theme = GD.Load<Theme>("res://ui/stage3_theme.tres");
        CanvasLayer canvas = new();
        AddChild(canvas);

        PanelContainer header = new()
        {
            Theme = theme,
            AnchorRight = 1f,
            OffsetLeft = 16,
            OffsetTop = 14,
            OffsetRight = -16,
            CustomMinimumSize = new Vector2(0, 82)
        };
        canvas.AddChild(header);
        VBoxContainer headerBox = new();
        headerBox.AddThemeConstantOverride("separation", 5);
        header.AddChild(headerBox);
        Label title = new()
        {
            Text = "形态样本台 A · 人工几何测试输入，非自然演化结果"
        };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", new Color("8ed6c9"));
        headerBox.AddChild(title);
        Label help = new()
        {
            Text = "左键选择 / 数字 1–6 · 右键拖动旋转 · 滚轮近看 · W 线框 · L LOD · F2/Esc 返回世界",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        headerBox.AddChild(help);

        _detailsPanel = new PanelContainer
        {
            Theme = theme,
            AnchorLeft = 1f,
            AnchorRight = 1f,
            AnchorBottom = 1f,
            OffsetLeft = -380,
            OffsetTop = 112,
            OffsetRight = -16,
            OffsetBottom = -16
        };
        canvas.AddChild(_detailsPanel);
        ScrollContainer scroll = new();
        _detailsPanel.AddChild(scroll);
        VBoxContainer content = new();
        content.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(content);
        Label heading = new() { Text = "选中样本" };
        heading.AddThemeFontSizeOverride("font_size", 17);
        content.AddChild(heading);
        _lodLabel = new Label();
        _lodLabel.AddThemeColorOverride("font_color", new Color("f2ca7c"));
        content.AddChild(_lodLabel);
        _details = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(330, 160)
        };
        content.AddChild(_details);
        AddButton(content, "切换线框 [W]", ToggleWireframe);
        AddButton(content, "切换高/低 LOD [L]", ToggleLod);
        AddButton(content, "返回世界 [F2 / Esc]", ReturnToWorld);
        Label boundary = new()
        {
            Text = "本场景只验证统一曲面参数、连接过渡与三角形预算；不会写入模拟、共同祖先或遗传数据。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        boundary.AddThemeColorOverride("font_color", new Color("aebccc"));
        content.AddChild(boundary);
        ApplyResponsiveLayout(force: true);
    }

    private void RebuildSamples()
    {
        foreach (SampleView sample in _samples)
        {
            sample.Solid.QueueFree();
            sample.Wire.QueueFree();
            sample.Label.QueueFree();
        }
        _samples.Clear();

        OrganicShapeParameters[] parameters = OrganicMeshGenerator.BuildArtificialSamples(_highDetail);
        Vector3[] positions =
        [
            new(-12f, 0, -7f), new(0, 0, -7f), new(12f, 0, -7f),
            new(-12f, 0, 7f), new(0, 0, 7f), new(12f, 0, 7f)
        ];
        for (int index = 0; index < parameters.Length; index++)
        {
            GeneratedOrganicMesh geometry = OrganicMeshGenerator.Generate(parameters[index]);
            StandardMaterial3D material = new()
            {
                AlbedoColor = parameters[index].Color,
                Roughness = 0.58f,
                Metallic = 0.02f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled
            };
            MeshInstance3D solid = new()
            {
                Name = $"Sample{index + 1}_{parameters[index].DiagnosticName}",
                Mesh = geometry.Solid,
                MaterialOverride = material,
                Position = positions[index]
            };
            AddChild(solid);

            StandardMaterial3D lineMaterial = new()
            {
                AlbedoColor = new Color(0.92f, 1f, 0.98f, 0.88f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                NoDepthTest = true
            };
            MeshInstance3D wire = new()
            {
                Name = $"Wire{index + 1}",
                Mesh = geometry.Wire,
                MaterialOverride = lineMaterial,
                Position = positions[index],
                Visible = _wireframe
            };
            AddChild(wire);
            Label3D label = new()
            {
                Text = $"{index + 1}  {parameters[index].DiagnosticName}\n{geometry.TriangleCount:N0} △",
                Position = positions[index] + new Vector3(0, 4.8f, 0),
                FontSize = 34,
                OutlineSize = 8,
                Modulate = new Color("dcebe8"),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true
            };
            AddChild(label);
            _samples.Add(new SampleView(parameters[index], geometry, solid, wire, label, positions[index]));
        }
        _selectedIndex = Math.Clamp(_selectedIndex, 0, _samples.Count - 1);
        UpdateDetails();
    }

    private void SelectNearest(Vector2 screenPosition)
    {
        float bestDistance = 72f;
        int bestIndex = -1;
        for (int index = 0; index < _samples.Count; index++)
        {
            Vector3 position = _samples[index].WorldPosition;
            if (_camera.IsPositionBehind(position))
                continue;
            float distance = _camera.UnprojectPosition(position).DistanceTo(screenPosition);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = index;
            }
        }
        if (bestIndex >= 0)
            SelectSample(bestIndex, focus: true);
    }

    private void SelectSample(int index, bool focus)
    {
        if (index < 0 || index >= _samples.Count)
            return;
        _selectedIndex = index;
        if (focus)
        {
            _target = _samples[index].WorldPosition;
            _distance = Math.Min(_distance, 15f);
            UpdateCamera();
        }
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        if (_samples.Count == 0 || _details is null)
            return;
        SampleView sample = _samples[_selectedIndex];
        _lodLabel.Text = $"LOD：{(_highDetail ? "高" : "低")} · 线框：{(_wireframe ? "开" : "关")}";
        _details.Text =
            $"{_selectedIndex + 1}. {sample.Parameters.DiagnosticName}\n\n" +
            $"{sample.Parameters.ParameterSummary}\n" +
            $"骨架连接段：{sample.Parameters.Segments.Length}\n" +
            $"连接过渡：{sample.Parameters.ConnectionBlend:F2}\n" +
            $"采样分辨率：{sample.Parameters.Resolution}³\n" +
            $"三角形：{sample.Geometry.TriangleCount:N0}\n\n" +
            (_selectedIndex == 5
                ? "三条分枝与主干由同一等值面提取，连接处是一个连续表面。"
                : "形状由连续骨架、半径、厚度、渐细与曲率参数共同生成。");
    }

    private void ToggleWireframe()
    {
        _wireframe = !_wireframe;
        foreach (SampleView sample in _samples)
            sample.Wire.Visible = _wireframe;
        UpdateDetails();
    }

    private void ToggleLod()
    {
        _highDetail = !_highDetail;
        RebuildSamples();
    }

    private void ReturnToWorld() => GetTree().ChangeSceneToFile("res://scenes/Stage3Main.tscn");

    private void UpdateCamera()
    {
        float horizontal = _distance * MathF.Cos(_pitch);
        Vector3 offset = new(
            horizontal * MathF.Sin(_yaw),
            _distance * MathF.Sin(-_pitch),
            horizontal * MathF.Cos(_yaw));
        _camera.Position = _target + offset;
        _camera.LookAt(_target, Vector3.Up);
    }

    private void ApplyResponsiveLayout(bool force = false)
    {
        if (_detailsPanel is null)
            return;
        bool compact = GetViewport().GetVisibleRect().Size.X < 780f;
        if (!force && compact == _compactLayout)
            return;
        _compactLayout = compact;
        if (compact)
        {
            _detailsPanel.AnchorLeft = 0f;
            _detailsPanel.AnchorTop = 1f;
            _detailsPanel.AnchorRight = 1f;
            _detailsPanel.AnchorBottom = 1f;
            _detailsPanel.OffsetLeft = 16;
            _detailsPanel.OffsetTop = -270;
            _detailsPanel.OffsetRight = -16;
            _detailsPanel.OffsetBottom = -16;
        }
        else
        {
            _detailsPanel.AnchorLeft = 1f;
            _detailsPanel.AnchorTop = 0f;
            _detailsPanel.AnchorRight = 1f;
            _detailsPanel.AnchorBottom = 1f;
            _detailsPanel.OffsetLeft = -380;
            _detailsPanel.OffsetTop = 112;
            _detailsPanel.OffsetRight = -16;
            _detailsPanel.OffsetBottom = -16;
        }
    }

    private void RunSmoke()
    {
        bool sixSamples = _samples.Count == 6;
        bool finiteTriangles = _samples.All(sample => sample.Geometry.TriangleCount > 0);
        bool continuousBranchSurface = _samples.Count == 6 &&
            _samples[5].Parameters.Segments.Length == 4 &&
            _samples[5].Geometry.Solid.GetSurfaceCount() == 1;
        int[] highTriangles = _samples.Select(sample => sample.Geometry.TriangleCount).ToArray();
        ToggleWireframe();
        bool wireframeWorks = _samples.All(sample => sample.Wire.Visible);
        ToggleLod();
        int[] lowTriangles = _samples.Select(sample => sample.Geometry.TriangleCount).ToArray();
        bool lodReduced = lowTriangles.Length == highTriangles.Length &&
            lowTriangles.Zip(highTriangles).All(pair => pair.First > 0 && pair.First < pair.Second);
        bool passed = sixSamples && finiteTriangles && continuousBranchSurface && wireframeWorks && lodReduced;
        GD.Print(
            $"MORPHOLOGY_SMOKE {(passed ? "PASS" : "FAIL")} samples={_samples.Count} " +
            $"high_triangles={string.Join(',', highTriangles)} " +
            $"low_triangles={string.Join(',', lowTriangles)} branch_single_surface={continuousBranchSurface} " +
            $"wireframe={wireframeWorks} lod_reduced={lodReduced}");
        GetTree().Quit(passed ? 0 : 1);
    }

    private static void AddButton(Container parent, string text, Action action)
    {
        Button button = new() { Text = text, FocusMode = Control.FocusModeEnum.All };
        button.Pressed += action;
        parent.AddChild(button);
    }
}
