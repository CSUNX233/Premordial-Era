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
    // Stage 3 uses the simulation geometry scale directly. The full curved-body
    // volume/physics remap belongs to morphology checkpoint B.
    private const float OrganismVisualScale = 1.0f;
    private readonly List<MeshInstance3D> _terrainChunks = [];
    private Node3D _terrainRoot = null!;
    private MeshInstance3D _water = null!;
    private MultiMeshInstance3D _organisms = null!;
    private MeshInstance3D _selection = null!;
    private float _worldSize;

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
        _selection = new MeshInstance3D
        {
            Mesh = new BoxMesh
            {
                Size = Vector3.One,
                Material = selectionMaterial
            },
            Visible = false
        };
        AddChild(_selection);
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

    public int UpdateOrganisms(WorldPresentationSnapshot snapshot, ulong? selectedId)
    {
        int totalRegions = snapshot.Organisms.Sum(organism => organism.Regions.Count);
        UsesSimplifiedProxies = totalRegions > DetailedRegionBudget;
        int renderedCount = UsesSimplifiedProxies
            ? snapshot.Organisms.Count + (selectedId is null ? 0 : GenomeValidator.MaximumRegions)
            : totalRegions;
        MultiMesh multimesh = _organisms.Multimesh;
        multimesh.InstanceCount = renderedCount;
        int instance = 0;

        foreach (OrganismPresentationState organism in snapshot.Organisms)
        {
            float centerHeight = OrganismElevation(organism);
            double headingCosine = Math.Cos(organism.HeadingRadians);
            double headingSine = Math.Sin(organism.HeadingRadians);
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
                float localX = (float)((region.LocalCenter.X * headingCosine) -
                    (region.LocalCenter.Y * headingSine)) * OrganismVisualScale;
                float localZ = (float)((region.LocalCenter.X * headingSine) +
                    (region.LocalCenter.Y * headingCosine)) * OrganismVisualScale;
                Vector3 origin = new(
                    organism.Position.X - (_worldSize * 0.5f) + localX,
                    organism.Immersion > 0.05 ? centerHeight : centerHeight + (thickness * 0.5f),
                    organism.Position.Y - (_worldSize * 0.5f) + localZ);
                Basis basis = new Basis(
                    Vector3.Up,
                    -(float)(region.Angle + organism.HeadingRadians)).Scaled(new Vector3(
                    (float)region.Width * OrganismVisualScale,
                    thickness,
                    (float)region.Length * OrganismVisualScale));
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
        float diameter = Math.Max(2.0f, (float)organism.Body.BoundingRadius * OrganismVisualScale * 2.6f);
        _selection.Position = new Vector3(
            organism.Position.X - (_worldSize * 0.5f),
            OrganismElevation(organism),
            organism.Position.Y - (_worldSize * 0.5f));
        _selection.Scale = new Vector3(diameter, 0.08f, diameter);
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
