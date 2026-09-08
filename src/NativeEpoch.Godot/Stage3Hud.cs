using Godot;

namespace NativeEpoch.Godot;

public sealed partial class Stage3Hud : CanvasLayer
{
    private Label _statistics = null!;
    private Label _inspector = null!;
    private Label _status = null!;
    private Label _mode = null!;
    private Button _pause = null!;
    private PanelContainer _toolPanel = null!;
    private PanelContainer _inspectorPanel = null!;
    private bool _compactLayout;
    private int _layoutBand = -1;

    public event Action? PauseRequested;
    public event Action? StepRequested;
    public event Action<double>? SpeedRequested;
    public event Action? SmallWorldRequested;
    public event Action? ObservationWorldRequested;
    public event Action? SelectToolRequested;
    public event Action? MineralToolRequested;
    public event Action? TemperatureToolRequested;
    public event Action? HeatmapRequested;
    public event Action? MorphologyLabRequested;

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
    }

    public void UpdateStatistics(string text)
    {
        if (_statistics.Text != text)
            _statistics.Text = text;
    }

    public void UpdateInspector(string text)
    {
        if (_inspector.Text != text)
            _inspector.Text = text;
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
        FadeIn(panel);

        VBoxContainer root = new();
        root.AddThemeConstantOverride("separation", 5);
        panel.AddChild(root);
        HBoxContainer row = new();
        row.AddThemeConstantOverride("separation", 7);
        root.AddChild(row);
        Label title = new() { Text = "原生纪 · 观察台" };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", new Color("8ed6c9"));
        row.AddChild(title);
        _statistics = new Label
        {
            Text = "准备模拟…",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.AddChild(_statistics);

        HFlowContainer controls = new();
        controls.AddThemeConstantOverride("h_separation", 7);
        controls.AddThemeConstantOverride("v_separation", 5);
        root.AddChild(controls);
        _pause = AddButton(controls, "暂停", "暂停或继续固定步模拟", () => PauseRequested?.Invoke());
        AddButton(controls, "单步", "只执行一个 0.1 秒模拟步", () => StepRequested?.Invoke());
        AddButton(controls, "1×", "正常倍率，固定步仍为 0.1 秒", () => SpeedRequested?.Invoke(1));
        AddButton(controls, "10×", "每秒目标执行 100 个固定步", () => SpeedRequested?.Invoke(10));
        AddButton(controls, "100×", "降低画面刷新频率但不跳过生命事件", () => SpeedRequested?.Invoke(100));
        AddButton(controls, "1000×", "按机器能力批量执行固定步", () => SpeedRequested?.Invoke(1000));
        AddButton(controls, "工具栏", "显示或折叠左侧工具栏", () => TogglePanel(_toolPanel));
        AddButton(controls, "检查器", "显示或折叠右侧检查器", () => TogglePanel(_inspectorPanel));
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
            CustomMinimumSize = new Vector2(286, 0)
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

        AddButton(root, "少量祖先 24", "重建一个便于观察生命循环的小世界", () => SmallWorldRequested?.Invoke());
        AddButton(root, "观察负载 300（预演 8 秒）", "重建并真实运行 80 个固定步，让身体结构立即可见", () => ObservationWorldRequested?.Invoke());
        root.AddChild(new HSeparator());
        AddButton(root, "选择工具 [1]", "点击最近个体，在右侧显示它的快照", () => SelectToolRequested?.Invoke());
        AddButton(root, "矿物笔刷 [2]", "连续圆形衰减；左键增加，Shift+左键移除", () => MineralToolRequested?.Invoke());
        AddButton(root, "温度笔刷 [3]", "连续圆形衰减；左键加热，Shift+左键降温", () => TemperatureToolRequested?.Invoke());
        AddButton(root, "切换热力图 [H]", "自然→高度→温度→光照→矿物→残骸", () => HeatmapRequested?.Invoke());
        AddButton(root, "形态样本台 A [F2]", "打开独立人工几何测试；不会写入模拟", () => MorphologyLabRequested?.Invoke());

        _status = new Label
        {
            Text = "左键选择；右键拖动旋转镜头。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(260, 54)
        };
        _status.AddThemeColorOverride("font_color", new Color("f2ca7c"));
        root.AddChild(_status);

        Label help = new()
        {
            Text = "W前进 / S后退 · 右键拖动旋转 · 滚轮缩放\n[ / ] 调笔刷 · F2 形态样本台 · F11 全屏 · Tab 隐藏界面\n\n基础运动由材料、几何和能量结算；没有阶段 2 的行为网络。",
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
        Label heading = new() { Text = "个体检查器" };
        heading.AddThemeFontSizeOverride("font_size", 17);
        root.AddChild(heading);
        _inspector = new Label
        {
            Text = "选择工具下点击一个生命。\n详情仅为选中个体创建和刷新。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(320, 220)
        };
        root.AddChild(_inspector);
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
            _inspectorPanel.Visible = true;
        }
    }

    internal (bool ToolVisible, bool InspectorVisible) VisiblePanels =>
        (_toolPanel.Visible, _inspectorPanel.Visible);

    private void TogglePanel(Control panel)
    {
        bool show = !panel.Visible;
        if (_compactLayout && show)
        {
            _toolPanel.Visible = false;
            _inspectorPanel.Visible = false;
        }
        panel.Visible = show;
    }
}
