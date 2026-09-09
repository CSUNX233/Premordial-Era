using System.Numerics;

namespace NativeEpoch.Simulation;

internal readonly record struct OccupancyShape(float HorizontalRadius,float VerticalHalfExtent)
{
    public static OccupancyShape FromBody(DevelopingBody body)
    {
        float horizontal=(float)Math.Max(0.08,body.Cache.BoundingRadius);
        double vertical=0.08;
        foreach(BodyGeometryRegion region in body.Geometry.Regions)
            vertical=Math.Max(vertical,Math.Max(region.StartRadius,region.EndRadius)*region.VerticalScale);
        return new(horizontal,(float)vertical);
    }
}

internal readonly record struct SpatialOccupant(
    ulong Id,Vector2 Position,float Depth,OccupancyShape Shape);

/// <summary>Mutable broad phase shared by contact and same-step birth placement.</summary>
internal sealed class SpatialOccupancyIndex
{
    private readonly float _cellSize;
    private readonly Dictionary<(int X,int Y),List<ulong>> _cells=[];
    private readonly Dictionary<ulong,SpatialOccupant> _occupants=[];
    private readonly Dictionary<ulong,(int X,int Y)> _keys=[];
    private float _maximumRadius;

    public SpatialOccupancyIndex(float cellSize)=>_cellSize=Math.Max(0.25f,cellSize);
    public IEnumerable<SpatialOccupant> Occupants=>_occupants.Values;

    public void Upsert(SpatialOccupant occupant)
    {
        (int X,int Y) key=Key(occupant.Position);
        if(_keys.TryGetValue(occupant.Id,out var oldKey)&&oldKey!=key)
            RemoveFromCell(oldKey,occupant.Id);
        if(!_keys.ContainsKey(occupant.Id)||oldKey!=key)
        {
            if(!_cells.TryGetValue(key,out List<ulong>? bucket))_cells[key]=bucket=[];
            bucket.Add(occupant.Id);
        }
        _keys[occupant.Id]=key;_occupants[occupant.Id]=occupant;
        _maximumRadius=Math.Max(_maximumRadius,occupant.Shape.HorizontalRadius);
    }

    public void Remove(ulong id)
    {
        if(!_keys.Remove(id,out var key))return;
        RemoveFromCell(key,id);_occupants.Remove(id);
        // A stale high maximum only widens queries; it cannot miss a collision.
    }

    public IEnumerable<SpatialOccupant> Query(Vector2 position,float radius)
    {
        int range=(int)Math.Ceiling((radius+_maximumRadius)/_cellSize);
        (int X,int Y) center=Key(position);
        for(int y=-range;y<=range;y++)for(int x=-range;x<=range;x++)
            if(_cells.TryGetValue((center.X+x,center.Y+y),out List<ulong>? bucket))
                foreach(ulong id in bucket)yield return _occupants[id];
    }

    public bool CanPlace(Vector2 position,float depth,OccupancyShape shape,ulong ignoreId=0)
    {
        foreach(SpatialOccupant other in Query(position,shape.HorizontalRadius))
        {
            if(other.Id==ignoreId)continue;
            float horizontal=shape.HorizontalRadius+other.Shape.HorizontalRadius;
            float vertical=shape.VerticalHalfExtent+other.Shape.VerticalHalfExtent;
            Vector2 planar=position-other.Position;
            double normalized=planar.LengthSquared()/Math.Max(1e-8,horizontal*horizontal)+
                Math.Pow((depth-other.Depth)/Math.Max(1e-4,vertical),2);
            if(normalized<1.0)return false;
        }
        return true;
    }

    private (int X,int Y) Key(Vector2 position)=>
        ((int)MathF.Floor(position.X/_cellSize),(int)MathF.Floor(position.Y/_cellSize));
    private void RemoveFromCell((int X,int Y) key,ulong id)
    {
        if(!_cells.TryGetValue(key,out List<ulong>? bucket))return;
        bucket.Remove(id);if(bucket.Count==0)_cells.Remove(key);
    }
}

public enum InteractionState
{
    None,
    Yielding,
    Contesting
}
