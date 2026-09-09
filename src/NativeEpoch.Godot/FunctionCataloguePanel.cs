using Godot;

namespace NativeEpoch.Godot;

public sealed partial class FunctionCataloguePanel : PanelContainer
{
    private FunctionCatalogue _catalogue=null!;
    private ItemList _list=null!;
    private RichTextLabel _details=null!;
    private CheckButton _favoritesOnly=null!;
    private Button _favorite=null!,_focus=null!;
    private Label _status=null!;
    private List<ObservedFunction> _shown=[];
    private string? _selectedKey;
    public Func<ObservedFunction,bool>? CanFocus { get; set; }
    public event Action<ObservedFunction>? FocusRequested;

    public void Initialize(FunctionCatalogue catalogue)=>_catalogue=catalogue;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        OffsetLeft=20;OffsetTop=20;OffsetRight=-20;OffsetBottom=-20;
        MouseFilter=MouseFilterEnum.Stop;
        Theme=GD.Load<Theme>("res://ui/stage3_theme.tres");
        AddThemeStyleboxOverride("panel",new StyleBoxFlat
        {
            BgColor=new Color("0d171d"),BorderColor=new Color("31515a"),
            BorderWidthLeft=1,BorderWidthRight=1,BorderWidthTop=1,BorderWidthBottom=1,
            CornerRadiusTopLeft=10,CornerRadiusTopRight=10,CornerRadiusBottomLeft=10,CornerRadiusBottomRight=10,
            ContentMarginLeft=12,ContentMarginRight=12,ContentMarginTop=12,ContentMarginBottom=12
        });
        VBoxContainer root=new();root.AddThemeConstantOverride("separation",10);AddChild(root);
        HBoxContainer header=new();root.AddChild(header);
        Label title=new(){Text="功能图鉴",SizeFlagsHorizontal=SizeFlags.ExpandFill};
        title.AddThemeFontSizeOverride("font_size",22);header.AddChild(title);
        _favoritesOnly=new CheckButton{Text="只看收藏"};header.AddChild(_favoritesOnly);
        _favoritesOnly.Toggled+=_=>Refresh();
        Button close=new(){Text="关闭（G）"};header.AddChild(close);close.Pressed+=Hide;
        Label help=new()
        {
            Text="记录实际观察到的作用与功能组合。收藏后可定位代表个体，通过环境调整观察后代。",
            AutowrapMode=TextServer.AutowrapMode.WordSmart
        };root.AddChild(help);
        HSplitContainer body=new(){SizeFlagsVertical=SizeFlags.ExpandFill};root.AddChild(body);
        body.Resized+=()=>body.SplitOffsets=[(int)(Math.Clamp(body.Size.X*0.28f,180f,360f)-body.Size.X*0.5f)];
        _list=new ItemList{CustomMinimumSize=new Vector2(180,160),SizeFlagsHorizontal=SizeFlags.ExpandFill};body.AddChild(_list);
        _list.ItemSelected+=index=>{_selectedKey=_shown[(int)index].Key;RefreshDetails();};
        VBoxContainer right=new(){SizeFlagsHorizontal=SizeFlags.ExpandFill};body.AddChild(right);
        _details=new RichTextLabel
        {
            BbcodeEnabled=false,SizeFlagsVertical=SizeFlags.ExpandFill,
            SizeFlagsHorizontal=SizeFlags.ExpandFill,CustomMinimumSize=new Vector2(220,160),
            SelectionEnabled=true
        };right.AddChild(_details);
        HFlowContainer actions=new();right.AddChild(actions);
        _favorite=new Button{Text="收藏"};actions.AddChild(_favorite);
        _favorite.Pressed+=()=>
        {
            if(_selectedKey is null)return;
            _catalogue.ToggleFavorite(_selectedKey);_catalogue.Save();Refresh();
        };
        _focus=new Button{Text="定位代表个体"};actions.AddChild(_focus);
        _focus.Pressed+=()=>
        {
            ObservedFunction? entry=_shown.FirstOrDefault(value=>value.Key==_selectedKey);
            if(entry is null||CanFocus?.Invoke(entry)!=true)return;
            FocusRequested?.Invoke(entry);Hide();
        };
        _status=new Label{AutowrapMode=TextServer.AutowrapMode.WordSmart};root.AddChild(_status);
        Refresh();Hide();
    }

    public void Toggle()
    {
        Visible=!Visible;
        if(Visible)Refresh();
    }

    public void Refresh()
    {
        _shown=_catalogue.Entries.Where(entry=>!_favoritesOnly.ButtonPressed||entry.Favorite)
            .OrderByDescending(entry=>entry.Favorite).ThenBy(entry=>entry.Name,StringComparer.Ordinal).ToList();
        _list.Clear();
        foreach(ObservedFunction entry in _shown)
            _list.AddItem((entry.Favorite?"★ ":"")+entry.Name);
        int selected=_shown.FindIndex(entry=>entry.Key==_selectedKey);
        if(selected<0&&_shown.Count>0)selected=0;
        _selectedKey=selected>=0?_shown[selected].Key:null;
        if(selected>=0)_list.Select(selected);
        RefreshDetails();
    }

    private void RefreshDetails()
    {
        ObservedFunction? entry=_shown.FirstOrDefault(value=>value.Key==_selectedKey);
        _favorite.Disabled=entry is null;_focus.Disabled=entry is null||CanFocus?.Invoke(entry)!=true;
        _favorite.Text=entry?.Favorite==true?"取消收藏":"收藏";
        _status.Text=$"已记录 {_catalogue.Entries.Count} 项 · 收藏 {_catalogue.Entries.Count(value=>value.Favorite)} 项  {_catalogue.StorageStatus}";
        if(entry is null)
        {
            _details.Text=_favoritesOnly.ButtonPressed?"还没有收藏。切换到全部功能后，选择记录并点击收藏。":
                "还没有观察记录。运行世界后，实际发生的感知、支撑、推进和供氧作用会出现在这里。";
            return;
        }
        string availability=_focus.Disabled?"代表个体当前不可定位；历史记录仍保留。":"代表个体仍在当前世界，可点击定位。";
        _details.Text=$"{entry.Name}\n\n{entry.Evidence}\n\n"+
            $"首次观察：种子 {entry.FirstSeed} · {entry.FirstTime:F1}s · 第 {entry.FirstGeneration} 代\n"+
            $"首次基因：{entry.FirstGenome}\n"+
            $"最近代表：ID {entry.RepresentativeId} · 第 {entry.RepresentativeGeneration} 代\n"+
            $"代表基因：{entry.RepresentativeGenome}\n"+
            $"已保存基因示例：{entry.ExampleGenomes.Count}{(entry.ExampleGenomes.Count==32?"+":"")}\n\n"+
            $"{availability}\n\n引导观察\n{entry.Guidance}\n\n"+
            "证据等级：已观察到功能作用。是否带来跨代生存优势，仍需后代和失活对照验证。";
    }
}
