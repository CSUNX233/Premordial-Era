using Godot;

namespace NativeEpoch.Godot;

public sealed partial class Stage3Hud : CanvasLayer
{
    private Label _statistics = null!;
    private Label _detailedStatistics = null!;
    private Label _inspector = null!;
    private Label _detailedInspector = null!;
    private VBoxContainer _lifeBars = null!;
    private ProgressBar _growthBar = null!;
    private ProgressBar _energyBar = null!;
    private Label _status = null!;
    private Label _mode = null!;
    private Button _pause = null!;
    private LineEdit _seed = null!;
    private PanelContainer _toolPanel = null!;
    private PanelContainer _inspectorPanel = null!;
    private PanelContainer _timePanel = null!;
    private bool _compactLayout;
    private int _layoutBand = -1;
    private bool _inspectorWanted;

    private Label _tracking = null!;
    public event Action? RandomIndividualRequested;
    public void SetTracking(ulong? id) => _tracking.Text = id.HasValue ? $"追踪 #{id.Value}" : "自由视角";
    public event Action? PauseRequested;
    public event Action? StepRequested;
    public event Action<double>? SpeedRequested;
    public event Action? SmallWorldRequested;
    public event Action? ObservationWorldRequested;
    public event Action? SelectToolRequested;
    public event Action? MineralToolRequested;
    public event Action? TemperatureToolRequested;
    public event Action? HeatmapRequested;
    public event Action? MediumDiagnosticRequested;
    public event Action? MorphologyLabRequested;
    public event Action? FunctionCatalogueRequested;
    public event Action<ulong>? SeedWorldRequested;

    public override void _Ready()
    {
        Theme sharedTheme = GD.Load<Theme>("res://ui/stage3_theme.tres");
        BuildTimeBar(sharedTheme);
        BuildToolPanel(sharedTheme);
        BuildInspectorPanel(sharedTheme);
        ApplyResponsiveLayout(GetViewport().GetVisibleRect().Size.X, force: true);
    }

    public override void _Process(double delta)
    {
        _ = delta;
        ApplyResponsiveLayout(GetViewport().GetVisibleRect().Size.X, force: false);
        float contentTop = _timePanel.OffsetTop + _timePanel.Size.Y + 12;
        if (Math.Abs(_toolPanel.OffsetTop - contentTop) > 0.5f)
        {
            _toolPanel.OffsetTop = contentTop;
            _inspectorPanel.OffsetTop = contentTop;
        }
    }

    public void UpdateStatistics(string text, string? summary = null)
    {
        if (_detailedStatistics.Text != text) _detailedStatistics.Text = text;
        string visible = summary ?? text.Split('\n')[0];
        if (_statistics.Text != visible) _statistics.Text = visible;
    }

    public void RevealInspector()
    {
        _inspectorWanted = true;
        _inspectorPanel.Visible = true;
        if (_compactLayout) _toolPanel.Visible = false;
    }

    public void UpdateInspector(string text, string? summary = null, double growth = 0, double energy = 0)
    {
        string visible = summary ?? text;
        if (_inspector.Text != visible) _inspector.Text = visible;
        if (_detailedInspector.Text != text) _detailedInspector.Text = text;
        _lifeBars.Visible = summary is not null;
        _growthBar.Value = Math.Clamp(growth * 100, 0, 100);
        _energyBar.Value = Math.Clamp(energy * 100, 0, 100);
    }

    public void UpdateStatus(string text)
    {
        if (_status.Text != text)
            _status.Text = text;
    }

    public void UpdateMode(string text)
    {
        if (_mode.Text != text)
            _mode.Text = text;
    }

    public void SetPaused(bool paused) => _pause.Text = paused ? "继续" : "暂停";
    public void SetSeed(ulong seed) => _seed.Text = seed.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void BuildTimeBar(Theme theme)
    {
        PanelContainer panel = new()
        {
            Theme = theme,
            AnchorRight = 1.0f,
            OffsetLeft = 16,
            OffsetTop = 14,
            OffsetRight = -16,
            CustomMinimumSize = new Vector2(0, 52)
        };
        AddChild(panel);
        _timePanel = panel;
        FadeIn(panel);

        VBoxContainer root = new();
        root.AddThemeConstantOverride("separation", 5);
        panel.AddChild(root);
        HBoxContainer row = new();
        row.AddThemeConstantOverride("separation", 7);
        root.AddChild(row);
        VBoxContainer branding = new();
        row.AddChild(branding);
        Label title = new() { Text = "原生纪" };
        title.AddThemeFontSizeOverride("font_size", 25);
        title.AddThemeColorOverride("font_color", new Color("b5efdf"));
        branding.AddChild(title);
        Label subtitle = new() { Text = "一颗星球，生命的无数可能" };
        subtitle.AddThemeFontSizeOverride("font_size", 12);
        subtitle.AddThemeColorOverride("font_color", new Color("86a5b4"));
        branding.AddChild(subtitle);
        _statistics = new Label
        {
            Text = "准备模拟…",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.AddChild(_statistics);

        HFlowContainer controls = new();
        controls.AddThemeConstantOverride("h_separation", 7);
        controls.AddThemeConstantOverride("v_separation", 5);
        root.AddChild(controls);
        AddButton(controls, "随机个体", "随机定位一个存活个体并追踪；缩远自动停止", () => RandomIndividualRequested?.Invoke());
        _tracking = new Label { Text = "自由视角" };
        controls.AddChild(_tracking);
        _pause = AddButton(controls, "暂停", "暂停或继续固定步模拟", () => PauseRequested?.Invoke());
        AddButton(controls, "功能图鉴（G）", "查看、收藏与定位已观察到的功能", () => FunctionCatalogueRequested?.Invoke());
        AddButton(controls, "单步", "只执行一个 0.1 秒模拟步", () => StepRequested?.Invoke());
        AddButton(controls, "1×", "正常倍率，固定步仍为 0.1 秒", () => SpeedRequested?.Invoke(1));
        AddButton(controls, "10×", "每秒目标执行 100 个固定步", () => SpeedRequested?.Invoke(10));
        AddButton(controls, "100×", "降低画面刷新频率但不跳过生命事件", () => SpeedRequested?.Invoke(100));
        AddButton(controls, "1000×", "按机器能力批量执行固定步", () => SpeedRequested?.Invoke(1000));
        AddButton(controls, "世界", "显示或折叠世界与环境工具", () => TogglePanel(_toolPanel));
        AddButton(controls, "生命", "显示或折叠个体详情", () => TogglePanel(_inspectorPanel));
    }

    private void BuildToolPanel(Theme theme)
    {
        _toolPanel = new PanelContainer
        {
            Theme = theme,
            OffsetLeft = 16,
            OffsetTop = 130,
            AnchorBottom = 1.0f,
            OffsetBottom = -16,
            CustomMinimumSize = new Vector2(264, 0)
        };
        AddChild(_toolPanel);
        FadeIn(_toolPanel);

        ScrollContainer scroll = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _toolPanel.AddChild(scroll);
        VBoxContainer root = new();
        root.AddThemeConstantOverride("separation", 7);
        scroll.AddChild(root);
        Label heading = new() { Text = "世界与工具" };
        heading.AddThemeFontSizeOverride("font_size", 17);
        root.AddChild(heading);

        _mode = new Label { Text = "准备模拟" };
        _mode.AddThemeColorOverride("font_color", new Color("8ed6c9"));
        root.AddChild(_mode);

        root.AddChild(new Label { Text = "世界种子", TooltipText = "相同种子生成相同地形" });
        _seed = new LineEdit { Text = "20260908", MaxLength = 20,
            PlaceholderText = "输入非负整数", CustomMinimumSize = new Vector2(226, 0) };
        root.AddChild(_seed);
        void GenerateSeed()
        {
            if (!ulong.TryParse(_seed.Text.Trim(), out ulong value))
            {
                UpdateStatus("种子请输入 0 至 18446744073709551615 的整数。");
                return;
            }
            _seed.ReleaseFocus();
            SeedWorldRequested?.Invoke(value);
        }
        _seed.TextSubmitted += _ => GenerateSeed();
        HBoxContainer seedButtons = new();
        root.AddChild(seedButtons);
        AddButton(seedButtons, "按种子生成", "替换当前世界，生成 24 只水生祖先", GenerateSeed);
        AddButton(seedButtons, "随机星球", "选择新种子并替换当前世界", () =>
        {
            SetSeed((ulong)System.Random.Shared.NextInt64(1, 1_000_000_000));
            GenerateSeed();
        });

        AddButton(root, "少量祖先 24", "重建一个便于观察生命循环的小世界", () => SmallWorldRequested?.Invoke());
        AddButton(root, "观察负载 300（预演 8 秒）", "重建并真实运行 80 个固定步，让身体结构立即可见", () => ObservationWorldRequested?.Invoke());
        _detailedStatistics = new Label { Visible = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(228, 0) };
        _detailedStatistics.AddThemeFontSizeOverride("font_size", 12);
        _detailedStatistics.AddThemeColorOverride("font_color", new Color("9cb4bf"));
        AddButton(root, "生态统计", "展开详细种群、资源与性能统计", () =>
            _detailedStatistics.Visible = !_detailedStatistics.Visible);
        root.AddChild(_detailedStatistics);
        root.AddChild(new HSeparator());
        AddButton(root, "选择工具 [1]", "点击最近个体，在右侧显示它的快照", () => SelectToolRequested?.Invoke());
        AddButton(root, "矿物笔刷 [2]", "连续圆形衰减；左键增加，Shift+左键移除", () => MineralToolRequested?.Invoke());
        AddButton(root, "温度笔刷 [3]", "连续圆形衰减；左键加热，Shift+左键降温", () => TemperatureToolRequested?.Invoke());
        AddButton(root, "切换热力图 [H]", "自然→高度→温度→光照→矿物→残骸→溶解氧→空气氧", () => HeatmapRequested?.Invoke());
        AddButton(root, "水陆交换对照（人工）", "把选中个体移到陆地；只用于观察失水与介质约束，不代表自然演化", () => MediumDiagnosticRequested?.Invoke());
        AddButton(root, "形态样本台 A [F2]", "打开独立人工几何测试；不会写入模拟", () => MorphologyLabRequested?.Invoke());

        _status = new Label
        {
            Text = "左键选择生命；右键拖动查看星球。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(228, 54)
        };
        _status.AddThemeColorOverride("font_color", new Color("92b6b3"));
        root.AddChild(_status);

        Label help = new()
        {
            Text = "W/S 前后、A/D 左右沿球面移动\n右键：远景转动星球，近景旋转视角 · 滚轮缩放\n[ / ] 调笔刷 · F2 形态样本台 · F11 全屏 · Tab 隐藏界面\n\n绿植与水藻属于可摄食生物量。观察生产、摄食、分解和捕食通量；图鉴收藏不会直接改变基因。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        help.AddThemeColorOverride("font_color", new Color("aebccc"));
        root.AddChild(help);
    }

    private void BuildInspectorPanel(Theme theme)
    {
        _inspectorPanel = new PanelContainer
        {
            Theme = theme,
            AnchorLeft = 1.0f,
            AnchorRight = 1.0f,
            AnchorBottom = 1.0f,
            OffsetLeft = -360,
            OffsetTop = 130,
            OffsetRight = -16,
            OffsetBottom = -16,
            CustomMinimumSize = new Vector2(344, 0)
        };
        AddChild(_inspectorPanel);
        FadeIn(_inspectorPanel);

        ScrollContainer scroll = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _inspectorPanel.AddChild(scroll);
        VBoxContainer root = new();
        root.AddThemeConstantOverride("separation", 7);
        scroll.AddChild(root);
        Label heading = new() { Text = "生命档案" };
        heading.AddThemeFontSizeOverride("font_size", 17);
        root.AddChild(heading);
        _inspector = new Label
        {
            Text = "点击星球上的生命，\n观察它的成长与变化。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(270, 0)
        };
        root.AddChild(_inspector);
        _lifeBars = new VBoxContainer { Visible = false };
        _lifeBars.AddThemeConstantOverride("separation", 9);
        root.AddChild(_lifeBars);
        ProgressBar Bar(string text, Color color)
        {
            Label label = new() { Text = text };
            label.AddThemeColorOverride("font_color", new Color("8faebc"));
            label.AddThemeFontSizeOverride("font_size", 12);
            _lifeBars.AddChild(label);
            ProgressBar bar = new() { ShowPercentage = false, CustomMinimumSize = new Vector2(264, 7) };
            bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color("10232e"),
                CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3 });
            bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = color,
                CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3 });
            _lifeBars.AddChild(bar);
            return bar;
        }
        _growthBar = Bar("成长", new Color("75cdb3"));
        _energyBar = Bar("可用能量", new Color("e0bf7c"));
        _detailedInspector = new Label { Visible = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(270, 0) };
        _detailedInspector.AddThemeFontSizeOverride("font_size", 12);
        _detailedInspector.AddThemeColorOverride("font_color", new Color("9cb4bf"));
        AddButton(root, "基因与生理明细", "展开当前个体的完整组织与生理数据", () =>
            _detailedInspector.Visible = !_detailedInspector.Visible);
        root.AddChild(_detailedInspector);
    }

    private static Button AddButton(Container parent, string text, string tooltip, Action action)
    {
        Button button = new()
        {
            Text = text,
            TooltipText = tooltip,
            FocusMode = Control.FocusModeEnum.All
        };
        button.Pressed += action;
        parent.AddChild(button);
        return button;
    }

    private static void FadeIn(Control control)
    {
        Color modulate = control.Modulate;
        modulate.A = 0.0f;
        control.Modulate = modulate;
        control.CreateTween().TweenProperty(control, "modulate:a", 1.0f, 0.18f);
    }

    internal void ApplyResponsiveLayout(float width, bool force)
    {
        int layoutBand = width < 620f ? 0 : width < 940f ? 1 : 2;
        if (!force && layoutBand == _layoutBand)
            return;
        _layoutBand = layoutBand;
        _compactLayout = layoutBand < 2;
        if (_compactLayout)
        {
            _inspectorPanel.Visible = false;
            _toolPanel.Visible = layoutBand == 1;
        }
        else
        {
            _toolPanel.Visible = true;
            _inspectorPanel.Visible = _inspectorWanted;
        }
    }

    internal (bool ToolVisible, bool InspectorVisible) VisiblePanels =>
        (_toolPanel.Visible, _inspectorPanel.Visible);

    private void TogglePanel(Control panel)
    {
        bool show = !panel.Visible;
        if (panel == _inspectorPanel) _inspectorWanted = show;
        if (_compactLayout && show)
        {
            _toolPanel.Visible = false;
            _inspectorPanel.Visible = false;
        }
        panel.Visible = show;
    }
}
