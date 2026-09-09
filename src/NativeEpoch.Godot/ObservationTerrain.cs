using NativeEpoch.Simulation;
using System.Numerics;

namespace NativeEpoch.Godot;

/// <summary>Immutable terrain grid for camera queries while the simulation owns its environment.</summary>
internal sealed class ObservationTerrain
{
    private readonly double[] _heights;
    private readonly int _size;
    private readonly float _worldSize;
    public double WaterSurface { get; }

    public ObservationTerrain(IEnvironmentField environment, SimulationConfig config)
    {
        _size=config.EnvironmentGridSize;
        _worldSize=config.WorldSize;
        _heights=new double[_size*_size];
        for(int y=0;y<_size;y++) for(int x=0;x<_size;x++)
            _heights[y*_size+x]=environment.Sample(new Vector2(
                x*_worldSize/(_size-1),y*_worldSize/(_size-1))).TerrainHeight;
        WaterSurface=environment.Sample(Vector2.Zero).WaterSurface;
    }

    public double Height(Vector2 position)
    {
        double gx=Math.Clamp(position.X/_worldSize,0f,1f)*(_size-1);
        double gy=Math.Clamp(position.Y/_worldSize,0f,1f)*(_size-1);
        int x=Math.Min((int)gx,_size-2),y=Math.Min((int)gy,_size-2);
        double tx=gx-x,ty=gy-y;
        double lower=_heights[y*_size+x]*(1-tx)+_heights[y*_size+x+1]*tx;
        double upper=_heights[(y+1)*_size+x]*(1-tx)+_heights[(y+1)*_size+x+1]*tx;
        return lower*(1-ty)+upper*ty;
    }
}
