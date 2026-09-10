using Godot;
using NativeEpoch.Simulation;

namespace NativeEpoch.Godot;

public readonly record struct ResourceRenderCounts(
    int Minerals,int LandPlants,int Algae,int EdibleOrganics,int Detritus,int MetabolicWaste)
{
    public int Total=>Minerals+LandPlants+Algae+EdibleOrganics+Detritus+MetabolicWaste;
}

/// <summary>
/// Low-frequency, bounded visualization of immutable environment resource snapshots.
/// It never reads live simulation arrays and never creates a node per resource item.
/// </summary>
public sealed partial class ResourceFieldRenderer:Node3D
{
    // At strategic camera distances, a few readable patches communicate the
    // stock distribution better than thousands of sparkling points.
    private const int MineralBudget=120;
    private const int LandPlantBudget=80;
    private const int AlgaeBudget=160;
    private const int EdibleBudget=100;
    private const int DebrisBudget=80;
    private readonly List<VisualInstance> _minerals=[];
    private readonly List<VisualInstance> _landPlants=[];
    private readonly List<VisualInstance> _algae=[];
    private readonly List<VisualInstance> _edible=[];
    private readonly List<VisualInstance> _debris=[];
    private MultiMeshInstance3D _mineralLayer=null!;
    private MultiMeshInstance3D _landPlantLayer=null!;
    private MultiMeshInstance3D _algaeLayer=null!;
    private MultiMeshInstance3D _edibleLayer=null!;
    private MultiMeshInstance3D _debrisLayer=null!;
    private bool _overviewDetailsHidden;

    public override void _Ready()
    {
        _mineralLayer=CreateLayer("MineralDeposits",new CylinderMesh
            {TopRadius=0.5f,BottomRadius=0.5f,Height=1f,RadialSegments=6,Rings=1},0.82f,0.14f);
        _landPlantLayer=CreateLayer("LandPlants",CreatePlantMesh(),0.88f,0.0f);
        _algaeLayer=CreateLayer("WaterAlgae",new SphereMesh
            {Radius=0.5f,Height=1f,RadialSegments=6,Rings=3},0.86f,0.0f);
        _edibleLayer=CreateLayer("EdibleOrganics",new SphereMesh
            {Radius=0.5f,Height=1f,RadialSegments=6,Rings=3},0.96f,0.0f);
        _debrisLayer=CreateLayer("DetritusAndWaste",new BoxMesh{Size=Vector3.One},0.98f,0.0f);
    }

    public ResourceRenderCounts UpdateResources(EnvironmentResourceSnapshot snapshot,
        System.Numerics.Vector2? viewCenter=null)
    {
        if(snapshot.GridSize<2||snapshot.WorldSize<=0)return Clear();
        Validate(snapshot);
        Camera3D? camera=GetViewport().GetCamera3D();
        UpdateOverviewDetailState(camera,snapshot.WorldSize);
        if(_overviewDetailsHidden)return Clear();
        _minerals.Clear();_landPlants.Clear();_algae.Clear();_edible.Clear();_debris.Clear();
        int detritusCount=0,wasteCount=0;
        float longitudeSpacing=snapshot.WorldSize/snapshot.GridSize;
        float latitudeSpacing=snapshot.WorldSize/(snapshot.GridSize-1f);
        float visualSpacing=MathF.Sqrt(longitudeSpacing*latitudeSpacing);
        for(int index=0;index<snapshot.TerrainHeight.Length;index++)
        {
            int x=index%snapshot.GridSize,z=index/snapshot.GridSize;
            float worldX=x*longitudeSpacing,worldZ=z*latitudeSpacing;
            float terrain=(float)snapshot.TerrainHeight[index];
            float waterDepth=(float)snapshot.WaterDepth[index];
            float waterSurface=terrain+waterDepth;
            System.Numerics.Vector2 mapPosition=new(worldX,worldZ);
            Vector3 cullPoint=PlanetProjection.MapToWorld(
                mapPosition,waterDepth>0?waterSurface:terrain,snapshot.WorldSize);
            if(!IsVisibleInViewport(camera,cullPoint,mapPosition,snapshot.WorldSize))continue;
            float distance=viewCenter.HasValue
                ?SurfaceDistance(viewCenter.Value,mapPosition,snapshot.WorldSize):0f;
            bool distant=camera is not null&&camera.GlobalPosition.DistanceTo(cullPoint)>110f;
            int lodSlots=distant||distance>130f?1:2;
            float lodScale=distant?2.10f:1f;

            float mineral=Density(snapshot.Minerals[index],0.45);
            bool submergedMineral=waterDepth>0.05f;
            AddCellInstances(_minerals,index,0,mineral,Math.Min(2,lodSlots),visualSpacing,
                worldX,submergedMineral?waterSurface+0.06f:terrain+0.14f,worldZ,snapshot.WorldSize,
                submergedMineral?new Color(0.025f,0.12f,0.19f):new Color(0.04f,0.14f,0.12f),
                submergedMineral?ResourceShape.WaterColumnMineral:ResourceShape.Mineral,lodScale);

            float land=Density(snapshot.LandPlants[index],0.34);
            AddCellInstances(_landPlants,index,1,land,waterDepth<=0?Math.Min(3,lodSlots+1):0,
                visualSpacing,worldX,terrain,worldZ,snapshot.WorldSize,
                new Color(0.025f,0.19f,0.045f),ResourceShape.LandPlant,lodScale);

            float algae=Density(snapshot.Algae[index],0.055);
            float algaeOffset=waterDepth>0.25f
                ?Math.Min(Math.Max(0.18f,waterDepth*0.12f),Math.Min(4f,waterDepth*0.65f))
                :0f;
            float algaeY=waterSurface-algaeOffset;
            AddCellInstances(_algae,index,2,algae,waterDepth>0.25f?Math.Min(2,lodSlots):0,
                visualSpacing,worldX,algaeY,worldZ,snapshot.WorldSize,
                new Color(0.018f,0.15f,0.075f,0.92f),ResourceShape.Algae,lodScale);

            // Edible organic matter is intentionally sparse in the primitive
            // world; use a lower visual reference while retaining a zero-stock
            // hard cutoff so depletion still removes every marker.
            float edible=Density(snapshot.EdibleOrganics[index],0.002);
            float edibleY=waterDepth>0.25f
                ?waterSurface-Math.Min(2f,waterDepth*0.40f)
                :terrain+0.18f;
            AddCellInstances(_edible,index,3,edible,Math.Min(2,lodSlots),visualSpacing,
                worldX,edibleY,worldZ,snapshot.WorldSize,
                new Color(0.36f,0.12f,0.025f),ResourceShape.Edible,lodScale);

            float detritus=Density(snapshot.Detritus[index],0.12);
            int before=_debris.Count;
            AddCellInstances(_debris,index,4,detritus,1,visualSpacing,worldX,terrain+0.09f,worldZ,snapshot.WorldSize,
                new Color(0.12f,0.06f,0.025f),ResourceShape.Debris,lodScale);
            detritusCount+=_debris.Count-before;
            float waste=Density(snapshot.MetabolicWaste[index],0.08);
            before=_debris.Count;
            AddCellInstances(_debris,index,5,waste,1,visualSpacing,worldX,terrain+0.12f,worldZ,snapshot.WorldSize,
                new Color(0.22f,0.075f,0.03f),ResourceShape.Debris,lodScale);
            wasteCount+=_debris.Count-before;
        }

        int minerals=Commit(_mineralLayer,_minerals,MineralBudget);
        int landPlants=Commit(_landPlantLayer,_landPlants,LandPlantBudget);
        int algaeCount=Commit(_algaeLayer,_algae,AlgaeBudget);
        int edibleCount=Commit(_edibleLayer,_edible,EdibleBudget);
        int debrisBefore=_debris.Count;
        int debrisRendered=Commit(_debrisLayer,_debris,DebrisBudget);
        double retained=debrisBefore>0?debrisRendered/(double)debrisBefore:0.0;
        LastCounts=new(minerals,landPlants,algaeCount,edibleCount,
            (int)Math.Round(detritusCount*retained),(int)Math.Round(wasteCount*retained));
        return LastCounts;
    }

    public ResourceRenderCounts LastCounts{get;private set;}

    private void UpdateOverviewDetailState(Camera3D? camera,float worldSize)
    {
        if(camera is null)
        {
            _overviewDetailsHidden=false;
            return;
        }
        float ratio=camera.GlobalPosition.Length()/PlanetProjection.Radius(worldSize);
        if(_overviewDetailsHidden)
            _overviewDetailsHidden=ratio>2.30f;
        else
            _overviewDetailsHidden=ratio>=2.60f;
    }

    private ResourceRenderCounts Clear()
    {
        foreach(MultiMeshInstance3D layer in new[]{_mineralLayer,_landPlantLayer,_algaeLayer,_edibleLayer,_debrisLayer})
            if(layer is not null)layer.Multimesh.InstanceCount=0;
        return LastCounts=default;
    }

    private MultiMeshInstance3D CreateLayer(string name,Mesh mesh,float roughness,float metallic)
    {
        MultiMeshInstance3D layer=new()
        {
            Name=name,
            MaterialOverride=new StandardMaterial3D
            {
                VertexColorUseAsAlbedo=true,Roughness=roughness,Metallic=metallic,
                CullMode=BaseMaterial3D.CullModeEnum.Disabled,
                ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded
            }
        };
        layer.Multimesh=new MultiMesh
        {
            TransformFormat=MultiMesh.TransformFormatEnum.Transform3D,
            UseColors=true,Mesh=mesh,InstanceCount=0
        };
        AddChild(layer);
        return layer;
    }

    private static Mesh CreatePlantMesh()
    {
        ImmediateMesh mesh=new();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        for(int frond=0;frond<3;frond++)
        {
            float angle=frond*Mathf.Tau/3f;
            Vector3 side=new(MathF.Cos(angle),0,MathF.Sin(angle));
            Vector3 normal=Vector3.Up.Cross(side).Normalized();
            mesh.SurfaceSetNormal(normal);
            mesh.SurfaceAddVertex(-side*0.24f);
            mesh.SurfaceAddVertex(new Vector3(0,1,0));
            mesh.SurfaceAddVertex(side*0.24f);
            mesh.SurfaceSetNormal(-normal);
            mesh.SurfaceAddVertex(side*0.24f);
            mesh.SurfaceAddVertex(new Vector3(0,1,0));
            mesh.SurfaceAddVertex(-side*0.24f);
        }
        mesh.SurfaceSetNormal(Vector3.Up);
        Vector3 crownCenter=new(0,0.72f,0);
        for(int leaf=0;leaf<6;leaf++)
        {
            float a0=leaf*Mathf.Tau/6f;
            float a1=(leaf+1)*Mathf.Tau/6f;
            mesh.SurfaceAddVertex(crownCenter);
            mesh.SurfaceAddVertex(new Vector3(MathF.Cos(a0)*0.42f,0.58f,MathF.Sin(a0)*0.42f));
            mesh.SurfaceAddVertex(new Vector3(MathF.Cos(a1)*0.42f,0.58f,MathF.Sin(a1)*0.42f));
        }
        mesh.SurfaceEnd();
        return mesh;
    }

    private static void AddCellInstances(List<VisualInstance> target,int cell,int kind,float density,
        int maximum,float spacing,float x,float elevation,float z,float worldSize,
        Color color,ResourceShape shape,float lodScale)
    {
        if(density<0.08f||maximum<=0)return;
        int count=Math.Min(maximum,Math.Max(1,(int)MathF.Ceiling(density*maximum)));
        for(int slot=0;slot<count;slot++)
        {
            uint hash=Mix((uint)(cell*17+(kind*7919)+slot));
            float offsetX=(Unit(hash)-0.5f)*spacing*0.68f;
            float offsetZ=(Unit(Mix(hash+0x9e3779b9u))-0.5f)*spacing*0.68f;
            float yaw=Unit(Mix(hash+0x85ebca6bu))*Mathf.Tau;
            float variation=0.82f+(0.36f*Unit(Mix(hash+0xc2b2ae35u)));
            Vector3 scale=shape switch
            {
                ResourceShape.Mineral=>new Vector3(0.22f,0.40f,0.22f)*(0.8f+density)*variation,
                ResourceShape.WaterColumnMineral=>new Vector3(0.62f,0.035f,0.62f)*(0.75f+density)*variation,
                ResourceShape.LandPlant=>new Vector3(0.65f,0.75f+(1.35f*density),0.65f)*variation,
                ResourceShape.Algae=>new Vector3(0.55f+(0.55f*density),0.24f,0.55f+(0.55f*density))*variation,
                ResourceShape.Edible=>new Vector3(0.32f+(0.45f*density),0.20f,0.32f+(0.45f*density))*variation,
                _=>new Vector3(0.24f+(0.32f*density),0.10f,0.24f+(0.32f*density))*variation
            };
            System.Numerics.Vector2 mapPosition=SphericalWorld.OffsetPosition(
                new System.Numerics.Vector2(x,z),
                new System.Numerics.Vector2(offsetX,offsetZ),worldSize);
            Vector3 displayScale=shape==ResourceShape.LandPlant&&lodScale>1f
                ?new Vector3(scale.X*lodScale*3.0f,scale.Y*0.28f,scale.Z*lodScale*3.0f)
                :scale*lodScale;
            Basis basis=PlanetProjection.BasisAt(mapPosition,worldSize)*
                new Basis(Vector3.Up,yaw)*Basis.FromScale(displayScale);
            Color instanceColor=color.Lightened(0.10f*(variation-0.82f)/0.36f);
            target.Add(new(new Transform3D(basis,
                    PlanetProjection.MapToWorld(mapPosition,elevation,worldSize)),
                instanceColor,density+(Unit(hash)*0.015f),cell*8+kind*2+slot));
        }
    }

    private static int Commit(MultiMeshInstance3D layer,List<VisualInstance> source,int budget)
    {
        if(source.Count>budget)
            source.Sort(static(a,b)=>
            {
                int priority=b.Priority.CompareTo(a.Priority);
                return priority!=0?priority:a.StableId.CompareTo(b.StableId);
            });
        int count=Math.Min(source.Count,budget);
        MultiMesh multimesh=layer.Multimesh;
        if(multimesh.InstanceCount!=count)multimesh.InstanceCount=count;
        for(int index=0;index<count;index++)
        {
            multimesh.SetInstanceTransform(index,source[index].Transform);
            multimesh.SetInstanceColor(index,source[index].Color);
        }
        return count;
    }

    private bool IsVisibleInViewport(Camera3D? camera,Vector3 point,
        System.Numerics.Vector2 mapPosition,float worldSize)
    {
        if(camera is null)return true;
        if(camera.IsPositionBehind(point)||
           !PlanetProjection.IsSurfaceVisible(mapPosition,camera.GlobalPosition,worldSize,-0.025f))
            return false;
        Vector2 viewport=GetViewport().GetVisibleRect().Size;
        return new Rect2(-viewport*0.22f,viewport*1.44f).HasPoint(camera.UnprojectPosition(point));
    }

    private static float SurfaceDistance(System.Numerics.Vector2 a,System.Numerics.Vector2 b,float worldSize)=>
        PlanetProjection.UnitNormal(a,worldSize).DistanceTo(
            PlanetProjection.UnitNormal(b,worldSize))*PlanetProjection.Radius(worldSize);

    private static float Density(double amount,double reference)=>
        (float)Math.Clamp(Math.Sqrt(Math.Max(0.0,amount)/reference),0.0,1.0);

    private static void Validate(EnvironmentResourceSnapshot snapshot)
    {
        int count=snapshot.GridSize*snapshot.GridSize;
        if(snapshot.TerrainHeight.Length!=count||snapshot.WaterDepth.Length!=count||
            snapshot.Minerals.Length!=count||snapshot.LandPlants.Length!=count||
            snapshot.Algae.Length!=count||snapshot.EdibleOrganics.Length!=count||
            snapshot.Detritus.Length!=count||snapshot.MetabolicWaste.Length!=count)
            throw new ArgumentException("Resource snapshot arrays must match its grid size.",nameof(snapshot));
    }

    private static uint Mix(uint value)
    {
        value^=value>>16;value*=0x7feb352du;value^=value>>15;value*=0x846ca68bu;value^=value>>16;
        return value;
    }
    private static float Unit(uint value)=>(value&0x00ffffffu)/16777215f;

    private enum ResourceShape{Mineral,WaterColumnMineral,LandPlant,Algae,Edible,Debris}
    private readonly record struct VisualInstance(
        Transform3D Transform,Color Color,float Priority,int StableId);
}
