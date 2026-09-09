using Godot;
using NativeEpoch.Simulation;

namespace NativeEpoch.Godot;

public enum HeatmapMode
{
    Natural,
    Height,
    Temperature,
    Light,
    Minerals,
    Detritus,
    DissolvedOxygen,
    AirOxygen
}

public sealed partial class LowPolyWorldRenderer : Node3D
{
    private const int TerrainSegments = 48;
    private const int TerrainChunksPerAxis = 4;
    private const int DetailedRegionBudget = 20_000;
    private const int NearContinuousSkinBudget = 8;
    // Stage 3 uses the simulation geometry scale directly. The full curved-body
    // volume/physics remap belongs to morphology checkpoint B.
    private const float OrganismVisualScale = 1.0f;
    private readonly List<MeshInstance3D> _terrainChunks = [];
    private Node3D _terrainRoot = null!;
    private MeshInstance3D _water = null!;
    private MultiMeshInstance3D _organisms = null!;
    private MeshInstance3D _selection = null!;
    private MeshInstance3D _selectedSkin = null!;
    private Skeleton3D _selectedSkeleton = null!;
    private ulong _selectedSkinKey;
    private BodyGeometry? _selectedRestGeometry;
    private readonly List<NearSkinView> _nearSkins = [];
    private readonly Dictionary<ulong, SkinTemplate> _skinCache = [];
    private readonly Dictionary<ulong, PendingSkinBuild> _pendingSkinBuilds = [];
    private readonly HashSet<ulong> _previousNearIds = [];
    private readonly HashSet<ulong> _previousVisibleIds = [];
    private float _worldSize;
    private long _lastSnapshotStep = -1;
    private float _interpolationAlpha = 1f;
    private OrganismPresentationState? _selectedPrevious;
    private OrganismPresentationState? _selectedCurrent;
    private bool _meshCommittedThisFrame;

    public override void _Ready()
    {
        _terrainRoot = new Node3D { Name = "TerrainChunks" };
        AddChild(_terrainRoot);

        StandardMaterial3D organismMaterial = new()
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 0.72f,
            Metallic = 0.08f
        };
        SphereMesh regionMesh = new()
        {
            Radius = 0.5f,
            Height = 1.0f,
            RadialSegments = 8,
            Rings = 4,
            Material = organismMaterial
        };
        _organisms = new MultiMeshInstance3D();
        _organisms.Multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = regionMesh,
            InstanceCount = 0
        };
        AddChild(_organisms);

        StandardMaterial3D selectionMaterial = new()
        {
            AlbedoColor = new Color(1.0f, 0.78f, 0.18f, 0.48f),
            EmissionEnabled = true,
            Emission = new Color(1.0f, 0.58f, 0.08f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        ImmediateMesh selectionRing = new();
        selectionRing.SurfaceBegin(Mesh.PrimitiveType.LineStrip, selectionMaterial);
        const int ringSegments = 48;
        for (int index = 0; index <= ringSegments; index++)
        {
            float angle = Mathf.Tau * index / ringSegments;
            selectionRing.SurfaceAddVertex(new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle)));
        }
        selectionRing.SurfaceEnd();
        _selection = new MeshInstance3D { Mesh = selectionRing, Visible = false };
        AddChild(_selection);
        _selectedSkeleton = new Skeleton3D { Name = "SelectedBodySkeleton", Visible = false };
        AddChild(_selectedSkeleton);
        _selectedSkin = new MeshInstance3D { Visible = false, Skeleton = new NodePath("..") };
        _selectedSkeleton.AddChild(_selectedSkin);
        for (int index = 0; index < NearContinuousSkinBudget; index++)
        {
            Skeleton3D skeleton=new(){Visible=false};
            MeshInstance3D mesh=new(){Visible=false,Skeleton=new NodePath("..")};
            skeleton.AddChild(mesh);AddChild(skeleton);
            _nearSkins.Add(new NearSkinView(skeleton,mesh));
        }
    }

    public void BuildEnvironment(IEnvironmentField environment, float worldSize, HeatmapMode mode)
    {
        _worldSize = worldSize;
        StandardMaterial3D terrainMaterial = new()
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 0.92f
        };
        EnsureTerrainChunks();
        float spacing = worldSize / TerrainSegments;
        int segmentsPerChunk = TerrainSegments / TerrainChunksPerAxis;
        int chunkIndex = 0;
        for (int chunkZ = 0; chunkZ < TerrainChunksPerAxis; chunkZ++)
        {
            for (int chunkX = 0; chunkX < TerrainChunksPerAxis; chunkX++)
            {
                SurfaceTool surface = new();
                surface.Begin(Mesh.PrimitiveType.Triangles);
                surface.SetMaterial(terrainMaterial);
                int startX = chunkX * segmentsPerChunk;
                int startZ = chunkZ * segmentsPerChunk;
                for (int z = startZ; z < startZ + segmentsPerChunk; z++)
                {
                    for (int x = startX; x < startX + segmentsPerChunk; x++)
                    {
                        float x0 = x * spacing;
                        float x1 = (x + 1) * spacing;
                        float z0 = z * spacing;
                        float z1 = (z + 1) * spacing;
                        AddTerrainTriangle(surface, environment, mode, worldSize, x0, z0, x1, z0, x1, z1);
                        AddTerrainTriangle(surface, environment, mode, worldSize, x0, z0, x1, z1, x0, z1);
                    }
                }
                surface.GenerateNormals();
                _terrainChunks[chunkIndex++].Mesh = surface.Commit();
            }
        }

        StandardMaterial3D waterMaterial = new()
        {
            AlbedoColor = new Color(0.055f, 0.31f, 0.49f, 0.37f),
            Roughness = 0.18f,
            Metallic = 0.05f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        _water ??= new MeshInstance3D();
        if (_water.GetParent() is null)
            AddChild(_water);
        _water.Mesh = new PlaneMesh
        {
            Size = new Vector2(worldSize, worldSize),
            Material = waterMaterial
        };
        _water.Position = Vector3.Zero;
    }

    public int UpdateOrganisms(WorldPresentationSnapshot snapshot, ulong? selectedId,
        System.Numerics.Vector2? viewCenter = null)
    {
        bool poseAdvanced = snapshot.Statistics.StepIndex != _lastSnapshotStep;
        _lastSnapshotStep = snapshot.Statistics.StepIndex;
        if (poseAdvanced) _interpolationAlpha = 0f;
        _meshCommittedThisFrame=false;
        List<OrganismPresentationState> visibleOrganisms = VisibleOrganisms(snapshot, selectedId);
        HashSet<ulong> requiredSkinKeys=visibleOrganisms
            .Select(o=>o.VisualTemplateGeometry.GeometryKey).ToHashSet();
        foreach(ulong stale in _pendingSkinBuilds
                    .Where(pair=>pair.Value.Work.IsCompleted&&!requiredSkinKeys.Contains(pair.Key))
                    .Select(pair=>pair.Key).ToArray())
            _pendingSkinBuilds.Remove(stale);
        bool selectedSkinReady=UpdateSelectedSkin(snapshot,selectedId,poseAdvanced);
        HashSet<ulong> continuousSkinIds = UpdateNearSkins(
            visibleOrganisms, selectedId, viewCenter, poseAdvanced,selectedSkinReady);
        int totalRegions = visibleOrganisms.Sum(organism => organism.Regions.Count);
        UsesSimplifiedProxies = totalRegions > DetailedRegionBudget;
        int selectedRegionCount = visibleOrganisms.Where(o => continuousSkinIds.Contains(o.Id))
            .Sum(o => o.Regions.Count);
        int renderedCount = UsesSimplifiedProxies
            ? visibleOrganisms.Count + (selectedId is null ? 0 : GenomeValidator.MaximumRegions)
            : totalRegions - selectedRegionCount;
        MultiMesh multimesh = _organisms.Multimesh;
        multimesh.InstanceCount = renderedCount;
        int instance = 0;

        foreach (OrganismPresentationState organism in visibleOrganisms)
        {
            if (continuousSkinIds.Contains(organism.Id))
                continue;
            float centerHeight = OrganismElevation(organism);
            bool showDetailed = !UsesSimplifiedProxies || organism.Id == selectedId;
            if (!showDetailed)
            {
                BodyVisualRegion visual = organism.Regions[0];
                float diameter = Math.Max(0.55f, (float)organism.Body.BoundingRadius * 2f) * OrganismVisualScale;
                float height = Math.Max(0.20f, diameter * 0.28f);
                Vector3 proxyOrigin = new(
                    organism.Position.X - (_worldSize * 0.5f),
                    organism.Immersion > 0.05 ? centerHeight : centerHeight + (height * 0.5f),
                    organism.Position.Y - (_worldSize * 0.5f));
                Basis proxyBasis = Basis.Identity.Scaled(new Vector3(diameter, height, diameter));
                multimesh.SetInstanceTransform(instance, new Transform3D(proxyBasis, proxyOrigin));
                multimesh.SetInstanceColor(instance, new Color(
                    visual.Color.X, visual.Color.Y, visual.Color.Z, 1.0f));
                instance++;
                continue;
            }

            foreach (BodyVisualRegion region in organism.Regions)
            {
                float thickness = (float)region.Thickness * OrganismVisualScale;
                var localPose = RegionPose(organism,region);
                Basis headingBasis=new(Vector3.Up,-(float)organism.HeadingRadians);
                Vector3 offset=(headingBasis*localPose.Center)*OrganismVisualScale;
                Vector3 origin = new(
                    organism.Position.X - (_worldSize * 0.5f) + offset.X,
                    centerHeight+offset.Y,
                    organism.Position.Y - (_worldSize * 0.5f) + offset.Z);
                Basis basis = (headingBasis*new Basis(localPose.Rotation))*Basis.FromScale(new Vector3(
                    (localPose.Length+(float)region.Width) * OrganismVisualScale,
                    thickness,
                    (float)region.Width * OrganismVisualScale));
                multimesh.SetInstanceTransform(instance, new Transform3D(basis, origin));
                multimesh.SetInstanceColor(instance, new Color(
                    region.Color.X,
                    region.Color.Y,
                    region.Color.Z,
                    1.0f));
                instance++;
            }
        }

        if (instance != renderedCount)
            multimesh.InstanceCount = instance;

        UpdateSelection(snapshot, selectedId);
        return instance;
    }

    public bool UsesSimplifiedProxies { get; private set; }
    public long SkinMeshesBuilt { get; private set; }
    public int VisibleOrganismCount { get; private set; }

    private HashSet<ulong> UpdateNearSkins(IReadOnlyList<OrganismPresentationState> visibleOrganisms, ulong? selectedId,
        System.Numerics.Vector2? viewCenter, bool poseAdvanced,bool selectedSkinReady)
    {
        HashSet<ulong> rendered = [];
        int viewIndex = 0;
        System.Numerics.Vector2 focus=viewCenter??new(_worldSize*0.5f,_worldSize*0.5f);
        foreach (OrganismPresentationState organism in visibleOrganisms.Where(o=>o.Id!=selectedId)
                     .OrderBy(o=>System.Numerics.Vector2.DistanceSquared(o.Position,focus)*
                         (_previousNearIds.Contains(o.Id)?0.85f:1f)).Take(NearContinuousSkinBudget))
        {
            if(!TryGetSkinTemplate(organism,"世界近景个体",out SkinTemplate? template))continue;
            SkinTemplate ready=template!;
            NearSkinView view = _nearSkins[viewIndex++];
            if(view.GeometryKey!=organism.VisualTemplateGeometry.GeometryKey)
            {
                ConfigureSkeleton(view.Skeleton,view.Mesh,ready.RestGeometry);
                view.Mesh.Mesh=ready.Mesh;view.GeometryKey=organism.VisualTemplateGeometry.GeometryKey;
                view.RestGeometry=ready.RestGeometry;
                view.OrganismId=organism.Id;view.Previous=organism;view.Current=organism;
            }
            else if(view.OrganismId!=organism.Id||view.Current is null)
            {view.OrganismId=organism.Id;view.Previous=organism;view.Current=organism;}
            else if(poseAdvanced)
            {view.Previous=view.Current;view.Current=organism;}
            else view.Current=organism;
            view.Mesh.MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(organism.Regions[0].Color.X, organism.Regions[0].Color.Y,
                    organism.Regions[0].Color.Z), Roughness = 0.7f
            };
            ApplyInterpolatedPose(view.Skeleton,view.RestGeometry!,view.Previous!.Value,
                view.Current!.Value,_interpolationAlpha);
            view.Skeleton.Visible=true;view.Mesh.Visible = true;
            rendered.Add(organism.Id);
        }
        for (; viewIndex<_nearSkins.Count; viewIndex++){_nearSkins[viewIndex].Skeleton.Visible=false;_nearSkins[viewIndex].Mesh.Visible=false;}
        _previousNearIds.Clear();_previousNearIds.UnionWith(rendered);
        if (selectedSkinReady&&selectedId is not null) rendered.Add(selectedId.Value);
        return rendered;
    }

    private List<OrganismPresentationState> VisibleOrganisms(
        WorldPresentationSnapshot snapshot, ulong? selectedId)
    {
        Camera3D? camera = GetViewport().GetCamera3D();
        if (camera is null)
        {
            VisibleOrganismCount = snapshot.Organisms.Count;
            return snapshot.Organisms.ToList();
        }

        Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
        Rect2 guard = new(-viewportSize * 0.18f, viewportSize * 1.36f);
        Rect2 retainedGuard = new(-viewportSize * 0.28f, viewportSize * 1.56f);
        List<OrganismPresentationState> visible = new(Math.Min(snapshot.Organisms.Count, 512));
        HashSet<ulong> nextVisible = [];
        foreach (OrganismPresentationState organism in snapshot.Organisms)
        {
            Vector3 worldPosition = new(
                organism.Position.X - (_worldSize * 0.5f), OrganismElevation(organism),
                organism.Position.Y - (_worldSize * 0.5f));
            bool selected = selectedId == organism.Id;
            bool inFront = !camera.IsPositionBehind(worldPosition);
            bool retained = _previousVisibleIds.Contains(organism.Id);
            bool inGuard = inFront && (retained ? retainedGuard : guard)
                .HasPoint(camera.UnprojectPosition(worldPosition));
            if (!selected && !inGuard)
                continue;
            visible.Add(organism);
            nextVisible.Add(organism.Id);
        }
        _previousVisibleIds.Clear();
        _previousVisibleIds.UnionWith(nextVisible);
        VisibleOrganismCount = visible.Count;
        return visible;
    }

    private static void ConfigureSkeleton(Skeleton3D skeleton,MeshInstance3D mesh,BodyGeometry geometry)
    {
        skeleton.ClearBones();
        foreach(BodyGeometryRegion region in geometry.Regions)
        {int bone=skeleton.GetBoneCount();skeleton.AddBone($"region_{region.RegionId}");
            skeleton.SetBoneRest(bone,new Transform3D(new Basis(Vector3.Up,-(float)region.Angle),new Vector3(region.Center.X,0,region.Center.Y)));}
        mesh.Skin=skeleton.CreateSkinFromRestTransforms();
    }

    private void ApplyInterpolatedPose(Skeleton3D skeleton,BodyGeometry restGeometry,
        OrganismPresentationState previous,OrganismPresentationState current,float alpha)
    {
        for(int index=0;index<restGeometry.Regions.Count;index++)
        {
            BodyGeometryRegion rest=restGeometry.Regions[index];
            bool hasFrom=TryFindRegion(previous.Regions,rest.RegionId,out BodyVisualRegion from);
            bool hasTo=TryFindRegion(current.Regions,rest.RegionId,out BodyVisualRegion to);
            if(!hasFrom&&!hasTo)
            {
                skeleton.SetBonePosePosition(index,new Vector3(rest.Center.X,0,rest.Center.Y));
                skeleton.SetBonePoseRotation(index,new Quaternion(Vector3.Up,-(float)rest.Angle));
                skeleton.SetBonePoseScale(index,Vector3.Zero);
                continue;
            }
            if(!hasFrom)
                from=new BodyVisualRegion(to.RegionId,to.LocalCenter,to.Angle,0,0,0,to.Color);
            if(!hasTo)
                to=new BodyVisualRegion(from.RegionId,from.LocalCenter,from.Angle,0,0,0,from.Color);
            var fromPose=RegionPose(previous,from);
            var toPose=RegionPose(current,to);
            Vector3 center=fromPose.Center.Lerp(toPose.Center,alpha);
            Quaternion rotation=fromPose.Rotation.Slerp(toPose.Rotation,alpha);
            double length=Mathf.Lerp(fromPose.Length,toPose.Length,alpha);
            double width=Mathf.Lerp((float)from.Width,(float)to.Width,alpha);
            double thickness=Mathf.Lerp((float)from.Thickness,(float)to.Thickness,alpha);
            skeleton.SetBonePosePosition(index,center);
            skeleton.SetBonePoseRotation(index,rotation);
            skeleton.SetBonePoseScale(index,new Vector3(
                rest.Length<1e-6?1f:(float)(length/rest.Length),
                (float)(thickness/Math.Max(1e-8,(rest.StartRadius+rest.EndRadius)*rest.VerticalScale)),
                (float)(width/Math.Max(1e-8,rest.StartRadius+rest.EndRadius))));
        }
        float worldX=Mathf.Lerp(previous.Position.X,current.Position.X,alpha)-(_worldSize*0.5f);
        float worldZ=Mathf.Lerp(previous.Position.Y,current.Position.Y,alpha)-(_worldSize*0.5f);
        float elevation=Mathf.Lerp(OrganismElevation(previous),OrganismElevation(current),alpha);
        double heading=previous.HeadingRadians+
            NormalizeAngle(current.HeadingRadians-previous.HeadingRadians)*alpha;
        skeleton.Position=new Vector3(worldX,elevation,worldZ);
        skeleton.Rotation=new Vector3(0,-(float)heading,0);
    }

    private static bool TryFindRegion(IReadOnlyList<BodyVisualRegion> regions,int regionId,
        out BodyVisualRegion found)
    {
        for(int index=0;index<regions.Count;index++)
        {
            BodyVisualRegion region=regions[index];
            if(region.RegionId!=regionId)continue;
            found=region;
            return true;
        }
        found=default;
        return false;
    }

    private static (Vector3 Center,Quaternion Rotation,float Length) RegionPose(
        OrganismPresentationState organism,BodyVisualRegion region)
    {
        if(organism.AppendageRegions is not null)
        {
            for(int index=0;index<organism.AppendageRegions.Count;index++)
            {
                var segment=organism.AppendageRegions[index];
                if(segment.RegionId!=region.RegionId)continue;
                Vector3 start=new(segment.LocalStart.X,segment.LocalStart.Y,segment.LocalStart.Z);
                Vector3 end=new(segment.LocalEnd.X,segment.LocalEnd.Y,segment.LocalEnd.Z);
                Vector3 axis=end-start;
                float length=axis.Length();
                Quaternion rotation=new(Vector3.Up,-(float)region.Angle);
                if(length>1e-6f)
                {
                    Vector3 x=axis/length;
                    Vector3 up=Math.Abs(x.Dot(Vector3.Up))>0.98f?Vector3.Back:Vector3.Up;
                    Vector3 z=x.Cross(up).Normalized();
                    Vector3 y=z.Cross(x).Normalized();
                    rotation=new Basis(x,y,z).GetRotationQuaternion();
                }
                return ((start+end)*0.5f,rotation,length);
            }
        }
        return (new Vector3(region.LocalCenter.X,0,region.LocalCenter.Y),
            new Quaternion(Vector3.Up,-(float)region.Angle),(float)region.Length);
    }

    private static double NormalizeAngle(double angle)
    {
        while(angle>Math.PI)angle-=Math.Tau;
        while(angle<-Math.PI)angle+=Math.Tau;
        return angle;
    }

    private sealed class NearSkinView(Skeleton3D skeleton,MeshInstance3D mesh)
    {public Skeleton3D Skeleton{get;}=skeleton;public MeshInstance3D Mesh{get;}=mesh;public ulong GeometryKey{get;set;} public BodyGeometry? RestGeometry{get;set;} public ulong OrganismId{get;set;} public OrganismPresentationState? Previous{get;set;} public OrganismPresentationState? Current{get;set;}}

    private sealed record SkinTemplate(ArrayMesh Mesh,BodyGeometry RestGeometry);
    private sealed record PendingSkinBuild(OrganicShapeParameters Parameters,BodyGeometry RestGeometry,
        Task<OrganicMeshData> Work);

    private bool TryGetSkinTemplate(OrganismPresentationState organism,string name,out SkinTemplate? template)
    {
        ulong key=organism.VisualTemplateGeometry.GeometryKey;
        if(_skinCache.TryGetValue(key,out template))return true;
        if(_pendingSkinBuilds.TryGetValue(key,out PendingSkinBuild? pending))
        {
            if(!pending.Work.IsCompleted||_meshCommittedThisFrame)return false;
            if(!pending.Work.IsCompletedSuccessfully)
            {
                _pendingSkinBuilds.Remove(key);
                GD.PushError($"连续表皮后台构建失败: {pending.Work.Exception?.GetBaseException().Message}");
                return false;
            }
            GeneratedOrganicMesh built=OrganicMeshGenerator.BuildMesh(pending.Work.Result,pending.Parameters,false);
            template=new SkinTemplate(built.Solid,pending.RestGeometry);
            if(_skinCache.Count>=64)_skinCache.Remove(_skinCache.Keys.First());
            _skinCache[key]=template;
            _pendingSkinBuilds.Remove(key);
            _meshCommittedThisFrame=true;
            SkinMeshesBuilt++;
            return true;
        }
        if(_pendingSkinBuilds.Count<4)
        {
            OrganicShapeParameters parameters=OrganicMeshGenerator.FromGeometry(
                name,organism.GenomeFingerprint,organism.VisualTemplateGeometry,null,false);
            _pendingSkinBuilds[key]=new PendingSkinBuild(parameters,organism.VisualTemplateGeometry,
                Task.Run(()=>OrganicMeshGenerator.GenerateData(parameters)));
        }
        template=null;
        return false;
    }

    private bool UpdateSelectedSkin(WorldPresentationSnapshot snapshot, ulong? selectedId,bool poseAdvanced)
    {
        OrganismPresentationState? found = selectedId is null
            ? null : snapshot.Organisms.FirstOrDefault(o => o.Id == selectedId.Value);
        if (found is null || found.Value.Id == 0)
        {
            _selectedSkin.Visible = false;
            _selectedSkeleton.Visible = false;
            _selectedPrevious=null;_selectedCurrent=null;
            return false;
        }
        OrganismPresentationState organism = found.Value;
        ulong geometryKey = organism.VisualTemplateGeometry.GeometryKey;
        if (geometryKey != _selectedSkinKey)
        {
            if(!TryGetSkinTemplate(organism,"世界选中个体",out SkinTemplate? template))
            {
                _selectedSkin.Visible=false;_selectedSkeleton.Visible=false;
                return false;
            }
            SkinTemplate ready=template!;
            ConfigureSkeleton(_selectedSkeleton,_selectedSkin,ready.RestGeometry);
            _selectedSkin.Mesh = ready.Mesh;
            _selectedSkin.MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(organism.Regions[0].Color.X, organism.Regions[0].Color.Y,
                    organism.Regions[0].Color.Z), Roughness = 0.65f
            };
            _selectedSkinKey = geometryKey;
            _selectedRestGeometry = ready.RestGeometry;
            _selectedPrevious=organism;_selectedCurrent=organism;
        }
        else if(_selectedCurrent is null||_selectedCurrent.Value.Id!=organism.Id)
        {_selectedPrevious=organism;_selectedCurrent=organism;}
        else if(poseAdvanced){_selectedPrevious=_selectedCurrent;_selectedCurrent=organism;}
        else _selectedCurrent=organism;
        ApplyInterpolatedPose(_selectedSkeleton,_selectedRestGeometry??organism.VisualTemplateGeometry,
            _selectedPrevious!.Value,_selectedCurrent!.Value,_interpolationAlpha);
        _selectedSkeleton.Visible = true;
        _selectedSkin.Visible = true;
        return true;
    }

    public void InterpolateContinuousSkins(float alpha)
    {
        _interpolationAlpha=Math.Clamp(alpha,0f,1f);
        foreach(NearSkinView view in _nearSkins)
            if(view.Skeleton.Visible&&view.RestGeometry is not null&&view.Previous is not null&&view.Current is not null)
                ApplyInterpolatedPose(view.Skeleton,view.RestGeometry,view.Previous.Value,view.Current.Value,_interpolationAlpha);
        if(_selectedSkeleton.Visible&&_selectedRestGeometry is not null&&_selectedPrevious is not null&&_selectedCurrent is not null)
            ApplyInterpolatedPose(_selectedSkeleton,_selectedRestGeometry,_selectedPrevious.Value,_selectedCurrent.Value,_interpolationAlpha);
    }

    private void UpdateSelection(WorldPresentationSnapshot snapshot, ulong? selectedId)
    {
        if (selectedId is null)
        {
            _selection.Visible = false;
            return;
        }

        OrganismPresentationState? selected = null;
        foreach (OrganismPresentationState candidate in snapshot.Organisms)
        {
            if (candidate.Id == selectedId.Value)
            {
                selected = candidate;
                break;
            }
        }
        if (selected is null)
        {
            _selection.Visible = false;
            return;
        }

        OrganismPresentationState organism = selected.Value;
        float radius = Math.Max(0.75f, (float)organism.Body.BoundingRadius * OrganismVisualScale * 1.45f);
        _selection.Position = new Vector3(
            organism.Position.X - (_worldSize * 0.5f),
            OrganismElevation(organism) + Math.Max(0.08f, (float)organism.Body.BoundingRadius * 0.16f),
            organism.Position.Y - (_worldSize * 0.5f));
        _selection.Scale = new Vector3(radius, 1f, radius);
        _selection.Visible = true;
    }

    private static void AddTerrainTriangle(
        SurfaceTool surface,
        IEnvironmentField environment,
        HeatmapMode mode,
        float worldSize,
        float ax,
        float az,
        float bx,
        float bz,
        float cx,
        float cz)
    {
        AddTerrainVertex(surface, environment, mode, worldSize, ax, az);
        AddTerrainVertex(surface, environment, mode, worldSize, bx, bz);
        AddTerrainVertex(surface, environment, mode, worldSize, cx, cz);
    }

    private static void AddTerrainVertex(
        SurfaceTool surface,
        IEnvironmentField environment,
        HeatmapMode mode,
        float worldSize,
        float x,
        float z)
    {
        EnvironmentSample sample = environment.Sample(new System.Numerics.Vector2(x, z));
        surface.SetColor(ColorFor(sample, mode));
        surface.AddVertex(new Vector3(
            x - (worldSize * 0.5f),
            (float)sample.TerrainHeight,
            z - (worldSize * 0.5f)));
    }

    private static Color ColorFor(EnvironmentSample sample, HeatmapMode mode)
    {
        float value = mode switch
        {
            HeatmapMode.Height => Normalize(sample.TerrainHeight, -105.0, 8.0),
            HeatmapMode.Temperature => Normalize(sample.Temperature, 0.35, 1.05),
            HeatmapMode.Light => Normalize(sample.Light, 0.45, 1.0),
            HeatmapMode.Minerals => Normalize(sample.Minerals, 0.0, 0.35),
            HeatmapMode.Detritus => Normalize(sample.Detritus, 0.0, 2.0),
            HeatmapMode.DissolvedOxygen => Normalize(sample.DissolvedOxygenAvailability, 0.0, 0.4),
            HeatmapMode.AirOxygen => Normalize(sample.AirOxygenAvailability, 0.0, 0.4),
            _ => 0f
        };

        if (mode != HeatmapMode.Natural)
            return HeatColor(value);
        if (sample.TerrainHeight < 0.0)
        {
            float depth = Normalize(-sample.TerrainHeight, 0.0, 100.0);
            return new Color(0.045f + (0.05f * (1f - depth)),
                0.11f + (0.22f * (1f - depth)), 0.18f + (0.29f * (1f - depth)));
        }

        float height = Normalize(sample.TerrainHeight, 0.0, 8.0);
        return new Color(0.22f + (0.22f * height), 0.39f + (0.20f * height), 0.19f + (0.11f * height));
    }

    private static Color HeatColor(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        if (value < 0.5f)
            return new Color(0.08f, 0.28f + value, 0.72f - (value * 0.45f));
        float upper = (value - 0.5f) * 2f;
        return new Color(0.35f + (0.65f * upper), 0.78f - (0.58f * upper), 0.24f - (0.16f * upper));
    }

    private static float Normalize(double value, double minimum, double maximum) =>
        (float)Math.Clamp((value - minimum) / (maximum - minimum), 0.0, 1.0);

    public static float OrganismElevation(OrganismPresentationState organism) =>
        double.IsFinite(organism.BodyCenterElevation) ? (float)organism.BodyCenterElevation :
        organism.Environment.WaterDepth > 0.0 && organism.Immersion > 0.05
            ? (float)(organism.Environment.WaterSurface - organism.Depth)
            : (float)organism.Environment.TerrainHeight + 0.12f;

    private void EnsureTerrainChunks()
    {
        int required = TerrainChunksPerAxis * TerrainChunksPerAxis;
        while (_terrainChunks.Count < required)
        {
            MeshInstance3D chunk = new() { Name = $"TerrainChunk{_terrainChunks.Count:D2}" };
            _terrainChunks.Add(chunk);
            _terrainRoot.AddChild(chunk);
        }
    }
}
