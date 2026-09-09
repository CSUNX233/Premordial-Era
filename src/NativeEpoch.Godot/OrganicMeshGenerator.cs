using Godot;
using NativeEpoch.Simulation;

namespace NativeEpoch.Godot;

public readonly record struct OrganicSegment(
    Vector3 Start,
    Vector3 End,
    float StartRadius,
    float EndRadius,
    float VerticalScale = 1.0f,
    int BoneIndex = 0,
    int ParentBoneIndex = -1);

public sealed record OrganicShapeParameters(
    string DiagnosticName,
    string ParameterSummary,
    Color Color,
    OrganicSegment[] Segments,
    float ConnectionBlend,
    int Resolution,
    ulong SourceGenomeFingerprint = 0,
    double ExpectedVolume = 0,
    bool SourceConnected = true);

public sealed record GeneratedOrganicMesh(
    ArrayMesh Solid,
    ArrayMesh Wire,
    int TriangleCount,
    Aabb Bounds,
    double SampledVolume,
    bool ClosedSurface,
    int BoundaryEdges,
    int NonManifoldEdges,
    int ConnectedComponents);

public sealed record OrganicMeshData(
    Vector3[] TriangleVertices,
    Vector3[] Normals,
    int[] BoneIndices,
    Aabb Bounds,
    float Spacing,
    double SampledVolume,
    bool ClosedSurface,
    int BoundaryEdges,
    int NonManifoldEdges,
    int ConnectedComponents);

/// <summary>
/// Diagnostic organic surface generator. Every sample is described by the same
/// continuous skeleton/radius parameter space; names only label artificial tests.
/// </summary>
public static class OrganicMeshGenerator
{
    private static readonly int[,] CubeCorners =
    {
        { 0, 0, 0 }, { 1, 0, 0 }, { 1, 1, 0 }, { 0, 1, 0 },
        { 0, 0, 1 }, { 1, 0, 1 }, { 1, 1, 1 }, { 0, 1, 1 }
    };

    private static readonly int[,] Tetrahedra =
    {
        { 0, 5, 1, 6 }, { 0, 1, 2, 6 }, { 0, 2, 3, 6 },
        { 0, 3, 7, 6 }, { 0, 7, 4, 6 }, { 0, 4, 5, 6 }
    };

    public static OrganicMeshData GenerateData(OrganicShapeParameters parameters)
    {
        if (parameters.Segments.Length == 0)
            throw new ArgumentException("A shape needs at least one skeleton segment.", nameof(parameters));

        float maximumRadius = parameters.Segments.Max(segment =>
            Math.Max(segment.StartRadius, segment.EndRadius));
        Vector3 minimum = parameters.Segments[0].Start;
        Vector3 maximum = minimum;
        foreach (OrganicSegment segment in parameters.Segments)
        {
            minimum = minimum.Min(segment.Start).Min(segment.End);
            maximum = maximum.Max(segment.Start).Max(segment.End);
        }

        float margin = maximumRadius * 1.45f + parameters.ConnectionBlend;
        minimum -= Vector3.One * margin;
        maximum += Vector3.One * margin;
        Vector3 size = maximum - minimum;
        float longest = Math.Max(size.X, Math.Max(size.Y, size.Z));
        Vector3 padding = (Vector3.One * longest - size) * 0.5f;
        minimum -= padding;
        maximum += padding;

        int resolution = Math.Clamp(parameters.Resolution, 10, 28);
        float spacing = longest / (resolution - 1);
        float[,,] field = new float[resolution, resolution, resolution];
        int insideCells = 0;
        for (int z = 0; z < resolution; z++)
        for (int y = 0; y < resolution; y++)
        for (int x = 0; x < resolution; x++)
        {
            Vector3 point = minimum + new Vector3(x, y, z) * spacing;
            field[x, y, z] = SampleField(point, parameters);
            if (field[x, y, z] >= 0f)
                insideCells++;
        }

        List<Vector3> triangleVertices = [];
        Vector3[] cornerPositions = new Vector3[8];
        float[] cornerValues = new float[8];
        Vector3[] tetrahedronPositions = new Vector3[4];
        float[] tetrahedronValues = new float[4];
        for (int z = 0; z < resolution - 1; z++)
        for (int y = 0; y < resolution - 1; y++)
        for (int x = 0; x < resolution - 1; x++)
        {
            for (int corner = 0; corner < 8; corner++)
            {
                int cx = x + CubeCorners[corner, 0];
                int cy = y + CubeCorners[corner, 1];
                int cz = z + CubeCorners[corner, 2];
                cornerPositions[corner] = minimum + new Vector3(cx, cy, cz) * spacing;
                cornerValues[corner] = field[cx, cy, cz];
            }

            for (int tetrahedron = 0; tetrahedron < Tetrahedra.GetLength(0); tetrahedron++)
            {
                for (int vertex = 0; vertex < 4; vertex++)
                {
                    int corner = Tetrahedra[tetrahedron, vertex];
                    tetrahedronPositions[vertex] = cornerPositions[corner];
                    tetrahedronValues[vertex] = cornerValues[corner];
                }
                PolygoniseTetrahedron(tetrahedronPositions, tetrahedronValues, triangleVertices);
            }
        }

        OrientTriangles(triangleVertices, parameters);
        SealBoundaryLoops(triangleVertices, spacing);
        OrientTriangles(triangleVertices, parameters);

        Vector3 meshCenter = (minimum + maximum) * 0.5f;
        double meshVolume = MeshVolume(triangleVertices, meshCenter);
        TopologyAnalysis topology = AnalyzeTopology(triangleVertices, spacing);
        Vector3[] vertices=triangleVertices.ToArray();
        Vector3[] normals=new Vector3[vertices.Length];
        int[] bones=new int[vertices.Length];
        float epsilon=spacing*0.45f;
        for(int index=0;index<vertices.Length;index++)
        {
            Vector3 vertex=vertices[index];
            Vector3 gradient=new(
                SampleField(vertex+Vector3.Right*epsilon,parameters)-SampleField(vertex-Vector3.Right*epsilon,parameters),
                SampleField(vertex+Vector3.Up*epsilon,parameters)-SampleField(vertex-Vector3.Up*epsilon,parameters),
                SampleField(vertex+Vector3.Back*epsilon,parameters)-SampleField(vertex-Vector3.Back*epsilon,parameters));
            normals[index]=gradient.LengthSquared()>1e-10f?-gradient.Normalized():Vector3.Up;
            bones[index]=NearestBone(vertex,parameters);
        }
        return new OrganicMeshData(
            vertices,normals,bones,
            new Aabb(minimum, maximum - minimum),
            spacing,
            meshVolume,
            topology.BoundaryEdges == 0 && topology.NonManifoldEdges == 0 && topology.ConnectedComponents == 1,
            topology.BoundaryEdges, topology.NonManifoldEdges, topology.ConnectedComponents);
    }

    public static GeneratedOrganicMesh BuildMesh(OrganicMeshData data,OrganicShapeParameters parameters,
        bool includeWire=true)
    {
        ArrayMesh solid=BuildSolidMesh(data);
        ArrayMesh wire=includeWire?BuildWireMesh(data.TriangleVertices):new ArrayMesh();
        return new GeneratedOrganicMesh(solid,wire,data.TriangleVertices.Length/3,data.Bounds,
            data.SampledVolume,data.ClosedSurface,data.BoundaryEdges,data.NonManifoldEdges,
            data.ConnectedComponents);
    }

    public static GeneratedOrganicMesh Generate(OrganicShapeParameters parameters)
        =>BuildMesh(GenerateData(parameters),parameters);

    public static OrganicShapeParameters FromBodyGeometry(
        string name, Genome genome, BodyGeometry geometry, bool highDetail)
        => FromGeometry(name, genome.Fingerprint, geometry, null, highDetail);

    public static OrganicShapeParameters FromGeometry(
        string name, ulong genomeFingerprint, BodyGeometry geometry,
        IReadOnlyList<BodyVisualRegion>? pose, bool highDetail)
    {
        List<OrganicSegment> segments = [];
        Dictionary<int, BodyVisualRegion>? posed = pose?.ToDictionary(region => region.RegionId);
        int boneIndex = 0;
        Dictionary<int,int> boneByRegion = geometry.Regions.Select((region,index)=>(region.RegionId,index))
            .ToDictionary(pair=>pair.RegionId,pair=>pair.index);
        foreach (BodyGeometryRegion region in geometry.Regions)
        {
            BodyVisualRegion? visual = posed is not null && posed.TryGetValue(region.RegionId, out BodyVisualRegion found)
                ? found : null;
            System.Numerics.Vector2 planarCenter = visual?.LocalCenter ?? region.Center;
            double angle = visual?.Angle ?? region.Angle;
            double length = visual?.Length ?? region.Length;
            System.Numerics.Vector2 direction2 = new((float)Math.Cos(angle), (float)Math.Sin(angle));
            Vector3 start = new(planarCenter.X - direction2.X*(float)length*0.5f, 0,
                planarCenter.Y - direction2.Y*(float)length*0.5f);
            Vector3 end = new(planarCenter.X + direction2.X*(float)length*0.5f, 0,
                planarCenter.Y + direction2.Y*(float)length*0.5f);
            Vector3 axis = end - start;
            Vector3 side = axis.LengthSquared() > 1e-9f
                ? new Vector3(-axis.Z, 0, axis.X).Normalized()
                : Vector3.Back;
            int curveSlices = length < 1e-5 ? 1 : 4;
            int parentBone = region.ParentRegionId >= 0 ? boneByRegion[region.ParentRegionId] : -1;
            for (int slice = 0; slice < curveSlices; slice++)
            {
                float t0 = slice / (float)curveSlices;
                float t1 = (slice + 1) / (float)curveSlices;
                Vector3 CurvePoint(float t) => start.Lerp(end, t) +
                    side * (float)(region.Curvature * length * 0.28 * Math.Sin(Math.PI * t));
                double radiusScale = visual is null ? 1.0 : visual.Value.Width /
                    Math.Max(1e-8, region.StartRadius + region.EndRadius);
                Vector3 sliceStart=CurvePoint(t0),sliceEnd=CurvePoint(t1);
                Vector3 sliceDirection=(sliceEnd-sliceStart).Normalized();
                float overlap=(float)(Math.Min(region.StartRadius,region.EndRadius)*radiusScale*0.18);
                if(slice>0)sliceStart-=sliceDirection*overlap;
                if(slice<curveSlices-1)sliceEnd+=sliceDirection*overlap;
                segments.Add(new OrganicSegment(
                    sliceStart, sliceEnd,
                    Mathf.Lerp((float)(region.StartRadius*radiusScale), (float)(region.EndRadius*radiusScale), t0),
                    Mathf.Lerp((float)(region.StartRadius*radiusScale), (float)(region.EndRadius*radiusScale), t1),
                    (float)region.VerticalScale, boneIndex, parentBone));
            }
            boneIndex++;
        }
        double averageRoundness = geometry.Regions.Average(r => r.Roundness);
        return new OrganicShapeParameters(name,
            $"真实基因 {genomeFingerprint:X8} / 区域 {geometry.Regions.Count} / 圆润 {averageRoundness:F2}",
            ToColor(geometry.Regions[0].Color), segments.ToArray(),
            (float)(0.12 + 0.16 * averageRoundness), highDetail ? 24 :
                (geometry.Regions.Count>1||geometry.Regions.Any(r=>Math.Abs(r.Curvature)>0.5)?24:
                    geometry.Regions.All(r=>r.Length<1e-5)?22:20),
            genomeFingerprint, geometry.ExpectedVolume, geometry.Connected);
    }

    private static Color ToColor(System.Numerics.Vector3 value) => new(value.X, value.Y, value.Z, 1f);

    private readonly record struct TopologyAnalysis(int BoundaryEdges,int NonManifoldEdges,int ConnectedComponents);

    private static TopologyAnalysis AnalyzeTopology(IReadOnlyList<Vector3> vertices, float spacing)
    {
        TopologyAnalysis best=new(int.MaxValue,int.MaxValue,0);
        foreach(float factor in new[]{0.00001f,0.0001f,0.0005f,0.001f,0.005f})
        {
            TopologyAnalysis candidate=AnalyzeTopologyAt(vertices,Math.Max(1e-7f,spacing*factor));
            if(candidate.BoundaryEdges==0&&candidate.NonManifoldEdges==0)return candidate;
            if(candidate.BoundaryEdges+candidate.NonManifoldEdges*4<best.BoundaryEdges+best.NonManifoldEdges*4)best=candidate;
        }
        return best;
    }

    private static TopologyAnalysis AnalyzeTopologyAt(IReadOnlyList<Vector3> vertices,float quantum)
    {
        if (vertices.Count == 0) return new(0,0,0);
        static string Key(Vector3 v, float q) => $"{MathF.Round(v.X/q)},{MathF.Round(v.Y/q)},{MathF.Round(v.Z/q)}";
        Dictionary<string, int> edges = [];
        void Add(Vector3 a, Vector3 b)
        {
            string ka = Key(a, quantum), kb = Key(b, quantum);
            string key = string.CompareOrdinal(ka, kb) <= 0 ? ka + "|" + kb : kb + "|" + ka;
            edges[key] = edges.GetValueOrDefault(key) + 1;
        }
        for (int i = 0; i + 2 < vertices.Count; i += 3)
        { Add(vertices[i], vertices[i+1]); Add(vertices[i+1], vertices[i+2]); Add(vertices[i+2], vertices[i]); }
        int openEdges = edges.Values.Count(count => count == 1);
        int nonManifold = edges.Values.Count(count => count != 1 && count != 2);
        Dictionary<string,HashSet<string>> graph=[];
        foreach(string edge in edges.Keys)
        {
            string[] parts=edge.Split('|');
            if(!graph.TryGetValue(parts[0],out HashSet<string>? a))graph[parts[0]]=a=[];
            if(!graph.TryGetValue(parts[1],out HashSet<string>? b))graph[parts[1]]=b=[];
            a.Add(parts[1]);b.Add(parts[0]);
        }
        int components=0;HashSet<string> visited=[];
        foreach(string node in graph.Keys) if(visited.Add(node))
        {components++;Queue<string> q=new();q.Enqueue(node);while(q.Count>0)foreach(string n in graph[q.Dequeue()])if(visited.Add(n))q.Enqueue(n);}
        return new(openEdges,nonManifold,components);
    }

    private static double MeshVolume(IReadOnlyList<Vector3> vertices, Vector3 origin)
    {
        double signed = 0;
        for (int i=0;i+2<vertices.Count;i+=3)
        {
            Vector3 a=vertices[i]-origin,b=vertices[i+1]-origin,c=vertices[i+2]-origin;
            signed += a.Dot(b.Cross(c))/6.0;
        }
        return Math.Abs(signed);
    }

    private static void OrientTriangles(List<Vector3> vertices, OrganicShapeParameters parameters)
    {
        const float epsilon=0.0005f;
        for(int i=0;i+2<vertices.Count;i+=3)
        {
            Vector3 a=vertices[i],b=vertices[i+1],c=vertices[i+2];
            Vector3 center=(a+b+c)/3f;
            Vector3 gradient=new(
                SampleField(center+Vector3.Right*epsilon,parameters)-SampleField(center-Vector3.Right*epsilon,parameters),
                SampleField(center+Vector3.Up*epsilon,parameters)-SampleField(center-Vector3.Up*epsilon,parameters),
                SampleField(center+Vector3.Back*epsilon,parameters)-SampleField(center-Vector3.Back*epsilon,parameters));
            Vector3 outward=-gradient;
            if((b-a).Cross(c-a).Dot(outward)<0) (vertices[i+1],vertices[i+2])=(vertices[i+2],vertices[i+1]);
        }
    }

    private static void SealBoundaryLoops(List<Vector3> vertices, float spacing)
    {
        float quantum = Math.Max(1e-7f, spacing * 0.00001f);
        static string Key(Vector3 value, float q) =>
            $"{MathF.Round(value.X/q)},{MathF.Round(value.Y/q)},{MathF.Round(value.Z/q)}";
        Dictionary<string,(int Count,Vector3 A,Vector3 B,string KA,string KB)> edges=[];
        void Add(Vector3 a,Vector3 b)
        {
            string ka=Key(a,quantum),kb=Key(b,quantum);
            string key=string.CompareOrdinal(ka,kb)<=0?ka+"|"+kb:kb+"|"+ka;
            if(edges.TryGetValue(key,out var old))edges[key]=(old.Count+1,old.A,old.B,old.KA,old.KB);
            else edges[key]=(1,a,b,ka,kb);
        }
        for(int i=0;i+2<vertices.Count;i+=3)
        {Add(vertices[i],vertices[i+1]);Add(vertices[i+1],vertices[i+2]);Add(vertices[i+2],vertices[i]);}
        var boundary=edges.Values.Where(edge=>edge.Count==1).ToArray();
        if(boundary.Length==0)return;
        Dictionary<string,List<int>> incidence=[];
        for(int i=0;i<boundary.Length;i++)foreach(string key in new[]{boundary[i].KA,boundary[i].KB})
        {if(!incidence.TryGetValue(key,out List<int>? list))incidence[key]=list=[];list.Add(i);}
        HashSet<int> seen=[];
        for(int seed=0;seed<boundary.Length;seed++)if(seen.Add(seed))
        {
            Queue<int> queue=new();queue.Enqueue(seed);List<int> component=[];HashSet<string> nodes=[];
            while(queue.Count>0)
            {
                int edge=queue.Dequeue();component.Add(edge);nodes.Add(boundary[edge].KA);nodes.Add(boundary[edge].KB);
                foreach(string key in new[]{boundary[edge].KA,boundary[edge].KB})
                    foreach(int other in incidence[key])if(seen.Add(other))queue.Enqueue(other);
            }
            if(nodes.Any(node=>incidence[node].Count!=2))continue;
            Vector3 center=Vector3.Zero;int count=0;
            foreach(int edge in component){center+=boundary[edge].A+boundary[edge].B;count+=2;}
            center/=count;
            foreach(int edge in component)
            {vertices.Add(boundary[edge].B);vertices.Add(boundary[edge].A);vertices.Add(center);}
        }
    }

    public static OrganicShapeParameters[] BuildArtificialSamples(bool highDetail)
    {
        int resolution = highDetail ? 17 : 12;
        static OrganicSegment Segment(
            Vector3 start,
            Vector3 end,
            float startRadius,
            float endRadius,
            float verticalScale = 1f) =>
            new(start, end, startRadius, endRadius, verticalScale);

        List<OrganicSegment> curve = [];
        for (int index = 0; index < 6; index++)
        {
            float t0 = index / 6f;
            float t1 = (index + 1) / 6f;
            Vector3 start = new(-4f + 8f * t0, 0.6f * MathF.Sin(t0 * MathF.PI), 2.2f * MathF.Sin(t0 * MathF.PI));
            Vector3 end = new(-4f + 8f * t1, 0.6f * MathF.Sin(t1 * MathF.PI), 2.2f * MathF.Sin(t1 * MathF.PI));
            curve.Add(Segment(start, end, 0.9f - 0.25f * t0, 0.9f - 0.25f * t1, 0.82f));
        }

        OrganicSegment[] branches =
        [
            Segment(new Vector3(-3.4f, 0, 0), Vector3.Zero, 1.35f, 1.2f, 0.9f),
            Segment(Vector3.Zero, new Vector3(3.4f, 0.15f, 0), 1.15f, 0.55f, 0.9f),
            Segment(Vector3.Zero, new Vector3(2.7f, 0.8f, 2.7f), 1.05f, 0.48f, 0.9f),
            Segment(Vector3.Zero, new Vector3(2.5f, -0.45f, -2.8f), 1.0f, 0.42f, 0.9f)
        ];

        return
        [
            new("球形", "轴比 1.00 / 圆润度 1.00 / 渐细 0.00", new Color("69c7ae"),
                [Segment(Vector3.Zero, Vector3.Zero, 2.8f, 2.8f)], 0.35f, resolution),
            new("卵圆", "轴比 1.65 / 圆润度 0.92 / 渐细 0.08", new Color("7fc9e8"),
                [Segment(new Vector3(-1.7f, 0, 0), new Vector3(1.7f, 0, 0), 2.0f, 1.65f, 0.92f)], 0.32f, resolution),
            new("渐细杆", "长宽比 4.9 / 渐细 0.68 / 厚度 0.90", new Color("e3b66b"),
                [Segment(new Vector3(-4.2f, 0, 0), new Vector3(4.2f, 0, 0), 1.65f, 0.52f, 0.9f)], 0.28f, resolution),
            new("弯曲丝", "曲率 0.55 / 6 段连续中心线 / 渐细 0.28", new Color("b998e3"),
                curve.ToArray(), 0.34f, resolution),
            new("扁平体", "轴比 2.05 / 垂直厚度 0.28 / 渐细 0.12", new Color("e88181"),
                [Segment(new Vector3(-2.1f, 0, 0), new Vector3(2.1f, 0, 0), 2.25f, 1.95f, 0.28f)], 0.30f, resolution),
            new("三分枝", "4 条连接段 / 连接混合 0.62 / 末端渐细", new Color("8fce74"),
                branches, 0.62f, resolution)
        ];
    }

    private static float SampleField(Vector3 point, OrganicShapeParameters parameters)
    {
        Dictionary<int,(float Field,int Parent)> byBone=[];
        foreach (OrganicSegment segment in parameters.Segments)
        {
            Vector3 axis = segment.End - segment.Start;
            float lengthSquared = axis.LengthSquared();
            float t = lengthSquared > 1e-8f
                ? Math.Clamp((point - segment.Start).Dot(axis) / lengthSquared, 0f, 1f)
                : 0f;
            Vector3 closest = segment.Start + axis * t;
            Vector3 offset = point - closest;
            offset.Y /= Math.Max(0.12f, segment.VerticalScale);
            float radius = Mathf.Lerp(segment.StartRadius, segment.EndRadius, t);
            float segmentField = radius - offset.Length();
            if(!byBone.TryGetValue(segment.BoneIndex,out var current))
                byBone[segment.BoneIndex]=(segmentField,segment.ParentBoneIndex);
            else
                byBone[segment.BoneIndex]=(SmoothMaximum(current.Field,segmentField,
                    Math.Min(parameters.ConnectionBlend,Math.Max(0.01f,radius*0.12f))),segment.ParentBoneIndex);
        }
        (float Field,int Parent)[] roots=byBone.Where(pair=>pair.Value.Parent<0)
            .Select(pair=>pair.Value).OrderByDescending(root=>root.Field).ToArray();
        float value=roots.Length>0?roots[0].Field:float.NegativeInfinity;
        if(roots.Length>1&&Math.Abs(roots[0].Field-roots[1].Field)<Math.Max(0.025f,parameters.ConnectionBlend*0.6f))
            value=Math.Min(value,-0.02f);
        foreach((int bone,(float field,int parent)) in byBone.Where(pair=>pair.Value.Parent>=0).OrderBy(pair=>pair.Key))
            value=Math.Max(value,SmoothMaximum(byBone[parent].Field,field,parameters.ConnectionBlend));
        return value;
    }

    private static float SmoothMaximum(float a, float b, float blend)
    {
        if (blend <= 1e-5f)
            return Math.Max(a, b);
        float h = Math.Clamp(0.5f + (0.5f * (a - b) / blend), 0f, 1f);
        return Mathf.Lerp(b, a, h) + blend * h * (1f - h);
    }

    private static void PolygoniseTetrahedron(
        Span<Vector3> positions,
        Span<float> values,
        List<Vector3> triangles)
    {
        Span<int> inside = stackalloc int[4];
        Span<int> outside = stackalloc int[4];
        int insideCount = 0;
        int outsideCount = 0;
        for (int index = 0; index < 4; index++)
        {
            if (values[index] >= 0f)
                inside[insideCount++] = index;
            else
                outside[outsideCount++] = index;
        }

        if (insideCount is 0 or 4)
            return;

        if (insideCount == 1)
        {
            int source = inside[0];
            AddTriangle(
                Interpolate(positions[source], positions[outside[0]], values[source], values[outside[0]]),
                Interpolate(positions[source], positions[outside[1]], values[source], values[outside[1]]),
                Interpolate(positions[source], positions[outside[2]], values[source], values[outside[2]]),
                triangles);
            return;
        }

        if (insideCount == 3)
        {
            int source = outside[0];
            AddTriangle(
                Interpolate(positions[source], positions[inside[0]], values[source], values[inside[0]]),
                Interpolate(positions[source], positions[inside[2]], values[source], values[inside[2]]),
                Interpolate(positions[source], positions[inside[1]], values[source], values[inside[1]]),
                triangles);
            return;
        }

        Vector3 a = Interpolate(positions[inside[0]], positions[outside[0]], values[inside[0]], values[outside[0]]);
        Vector3 b = Interpolate(positions[inside[0]], positions[outside[1]], values[inside[0]], values[outside[1]]);
        Vector3 c = Interpolate(positions[inside[1]], positions[outside[0]], values[inside[1]], values[outside[0]]);
        Vector3 d = Interpolate(positions[inside[1]], positions[outside[1]], values[inside[1]], values[outside[1]]);
        AddTriangle(a, b, c, triangles);
        AddTriangle(b, d, c, triangles);
    }

    private static Vector3 Interpolate(Vector3 a, Vector3 b, float valueA, float valueB)
    {
        float denominator = valueA - valueB;
        float t = Math.Abs(denominator) <= 1e-8f ? 0.5f : valueA / denominator;
        return a.Lerp(b, Math.Clamp(t, 0f, 1f));
    }

    private static void AddTriangle(Vector3 a, Vector3 b, Vector3 c, List<Vector3> triangles)
    {
        if ((b - a).Cross(c - a).LengthSquared() <= 1e-10f)
            return;
        triangles.Add(a);
        triangles.Add(b);
        triangles.Add(c);
    }

    private static ArrayMesh BuildSolidMesh(OrganicMeshData data)
    {
        SurfaceTool surface = new();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        int[] bones=[0,0,0,0];
        float[] weights=[1f,0f,0f,0f];
        for(int index=0;index<data.TriangleVertices.Length;index++)
        {
            surface.SetNormal(data.Normals[index]);
            bones[0]=data.BoneIndices[index];
            surface.SetBones(bones);
            surface.SetWeights(weights);
            surface.AddVertex(data.TriangleVertices[index]);
        }
        ArrayMesh mesh = surface.Commit();
        return mesh;
    }

    private static int NearestBone(Vector3 point, OrganicShapeParameters parameters)
    {
        float best = float.PositiveInfinity; int bone = 0;
        foreach (OrganicSegment segment in parameters.Segments)
        {
            Vector3 axis = segment.End-segment.Start;
            float t = axis.LengthSquared()>1e-8f ? Math.Clamp((point-segment.Start).Dot(axis)/axis.LengthSquared(),0f,1f):0f;
            float distance=(point-(segment.Start+axis*t)).LengthSquared();
            if(distance<best){best=distance;bone=segment.BoneIndex;}
        }
        return bone;
    }

    private static ArrayMesh BuildWireMesh(IReadOnlyList<Vector3> vertices)
    {
        SurfaceTool surface = new();
        surface.Begin(Mesh.PrimitiveType.Lines);
        for (int index = 0; index + 2 < vertices.Count; index += 3)
        {
            Vector3 a = vertices[index];
            Vector3 b = vertices[index + 1];
            Vector3 c = vertices[index + 2];
            surface.AddVertex(a); surface.AddVertex(b);
            surface.AddVertex(b); surface.AddVertex(c);
            surface.AddVertex(c); surface.AddVertex(a);
        }
        return surface.Commit();
    }
}
