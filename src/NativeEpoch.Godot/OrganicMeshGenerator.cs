using Godot;

namespace NativeEpoch.Godot;

public readonly record struct OrganicSegment(
    Vector3 Start,
    Vector3 End,
    float StartRadius,
    float EndRadius,
    float VerticalScale = 1.0f);

public sealed record OrganicShapeParameters(
    string DiagnosticName,
    string ParameterSummary,
    Color Color,
    OrganicSegment[] Segments,
    float ConnectionBlend,
    int Resolution);

public sealed record GeneratedOrganicMesh(
    ArrayMesh Solid,
    ArrayMesh Wire,
    int TriangleCount,
    Aabb Bounds);

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

    public static GeneratedOrganicMesh Generate(OrganicShapeParameters parameters)
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
        for (int z = 0; z < resolution; z++)
        for (int y = 0; y < resolution; y++)
        for (int x = 0; x < resolution; x++)
        {
            Vector3 point = minimum + new Vector3(x, y, z) * spacing;
            field[x, y, z] = SampleField(point, parameters);
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

        ArrayMesh solid = BuildSolidMesh(triangleVertices, parameters, spacing);
        ArrayMesh wire = BuildWireMesh(triangleVertices);
        return new GeneratedOrganicMesh(
            solid,
            wire,
            triangleVertices.Count / 3,
            new Aabb(minimum, maximum - minimum));
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
        float value = float.NegativeInfinity;
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
            value = float.IsNegativeInfinity(value)
                ? segmentField
                : SmoothMaximum(value, segmentField, parameters.ConnectionBlend);
        }
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

    private static ArrayMesh BuildSolidMesh(
        IReadOnlyList<Vector3> vertices,
        OrganicShapeParameters parameters,
        float spacing)
    {
        SurfaceTool surface = new();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        float epsilon = spacing * 0.45f;
        foreach (Vector3 vertex in vertices)
        {
            Vector3 gradient = new(
                SampleField(vertex + Vector3.Right * epsilon, parameters) -
                    SampleField(vertex - Vector3.Right * epsilon, parameters),
                SampleField(vertex + Vector3.Up * epsilon, parameters) -
                    SampleField(vertex - Vector3.Up * epsilon, parameters),
                SampleField(vertex + Vector3.Back * epsilon, parameters) -
                    SampleField(vertex - Vector3.Back * epsilon, parameters));
            Vector3 outward = gradient.LengthSquared() > 1e-10f ? -gradient.Normalized() : Vector3.Up;
            surface.SetNormal(outward);
            surface.AddVertex(vertex);
        }
        ArrayMesh mesh = surface.Commit();
        return mesh;
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
