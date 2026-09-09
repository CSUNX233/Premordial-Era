using System.Diagnostics;
using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct CorePrecisionDiagnosticResult(
    bool Passed,int ReferenceSamples,int BalancedSamples,double SurfaceWorkReduction,
    double MaximumRelativeEffectError,double ReferenceMilliseconds,double BalancedMilliseconds,
    double TimingReduction,double ReferenceMatterError,double BalancedMatterError,
    double ReferenceOxygenError,double BalancedOxygenError);

/// <summary>Repeatable surface-quadrature accuracy and isolated hot-loop timing check.</summary>
public static class CorePrecisionDiagnostics
{
    public static CorePrecisionDiagnosticResult Run(int measuredSteps=1600)
    {
        if(measuredSteps<100)throw new ArgumentOutOfRangeException(nameof(measuredSteps));
        // Let tiered JIT compilation settle before timing. Alternating order then taking
        // the median keeps either profile from inheriting a systematic first-run cost.
        _=Trial(CorePrecisionProfile.Reference,1200);
        _=Trial(CorePrecisionProfile.Balanced,1200);
        TrialResult reference=Trial(CorePrecisionProfile.Reference,measuredSteps);
        TrialResult balanced=Trial(CorePrecisionProfile.Balanced,measuredSteps);
        double[] referenceTimes=
        [
            reference.ElapsedMilliseconds,
            Trial(CorePrecisionProfile.Reference,measuredSteps).ElapsedMilliseconds,
            Trial(CorePrecisionProfile.Reference,measuredSteps).ElapsedMilliseconds
        ];
        double[] balancedTimes=
        [
            balanced.ElapsedMilliseconds,
            Trial(CorePrecisionProfile.Balanced,measuredSteps).ElapsedMilliseconds,
            Trial(CorePrecisionProfile.Balanced,measuredSteps).ElapsedMilliseconds
        ];
        Array.Sort(referenceTimes);
        Array.Sort(balancedTimes);
        double referenceMilliseconds=referenceTimes[1];
        double balancedMilliseconds=balancedTimes[1];
        double maximumError=new[]
        {
            Relative(reference.SubstrateUptake,balanced.SubstrateUptake),
            Relative(reference.OxygenUptake,balanced.OxygenUptake),
            Relative(reference.LightEnergy,balanced.LightEnergy),
            Relative(reference.WaterExchange,balanced.WaterExchange),
            Relative(reference.FinalEnergy,balanced.FinalEnergy)
        }.Max();
        double workReduction=1.0-CorePrecisionProfile.Balanced.SurfaceSamplesPerRegion/
            (double)CorePrecisionProfile.Reference.SurfaceSamplesPerRegion;
        double timingReduction=1.0-balancedMilliseconds/Math.Max(1e-9,referenceMilliseconds);
        bool passed=maximumError<=0.10+1e-12&&Math.Abs(reference.MatterError)<1e-9&&
            Math.Abs(balanced.MatterError)<1e-9&&Math.Abs(reference.OxygenError)<1e-8&&
            Math.Abs(balanced.OxygenError)<1e-8;
        return new(passed,CorePrecisionProfile.Reference.SurfaceSamplesPerRegion,
            CorePrecisionProfile.Balanced.SurfaceSamplesPerRegion,workReduction,maximumError,referenceMilliseconds,
            balancedMilliseconds,timingReduction,reference.MatterError,balanced.MatterError,
            reference.OxygenError,balanced.OxygenError);
    }

    private static TrialResult Trial(CorePrecisionProfile precision,int steps)
    {
        SimulationConfig config=new(){WorldSize=64,EnvironmentGridSize=16};
        Genome genome=Genome.CreateAncestor();
        BodyRegion[] state=genome.Regions.Select(gene=>new BodyRegion(gene.RegionId,
            BodyCalculator.TargetMatter(gene),1.0,Substrate:2.0,Oxygen:0.15,Water:1.0,Energy:80.0,
            TransportAvailability:1.0,ExchangeExpression:gene.ExchangeExpression,
            BarrierExpression:gene.BarrierExpression,ContractileExpression:gene.ContractileExpression,
            StructuralExpression:gene.StructuralExpression,SensoryExpression:gene.SensoryExpression)).ToArray();
        DevelopingBody body=new(genome,state,precision);
        BilinearEnvironmentField environment=new(config,new DeterministicRandom(91273,17));
        Vector2 position=FindWater(environment,config.WorldSize);
        float depth=(float)Math.Min(3.0,environment.Sample(position).WaterDepth*0.4);
        double matterBefore=environment.TotalMinerals+environment.TotalDetritus+
            environment.TotalMetabolicWaste+body.Cache.TotalMatter+body.TotalSubstrate;
        double oxygenBefore=environment.TotalOxygen+body.TotalOxygen;
        double substrate=0,oxygen=0,light=0,water=0;
        Stopwatch stopwatch=Stopwatch.StartNew();
        for(int step=0;step<steps;step++)
        {
            RegionalExchangeResult exchange=RegionalPhysiology.ExchangeWithEnvironment(body,genome,
                environment,position,0,depth,config,config.FixedDeltaSeconds,
                lightEnergyAllowance:2.0);
            substrate+=exchange.SubstrateUptake;oxygen+=exchange.OxygenUptake;
            light+=exchange.LightEnergy;water+=exchange.WaterUptake-exchange.WaterLost;
        }
        stopwatch.Stop();
        double matterAfter=environment.TotalMinerals+environment.TotalDetritus+
            environment.TotalMetabolicWaste+body.Cache.TotalMatter+body.TotalSubstrate;
        double oxygenAfter=environment.TotalOxygen+body.TotalOxygen;
        return new(substrate,oxygen,light,water,body.TotalEnergy,stopwatch.Elapsed.TotalMilliseconds,
            matterAfter-matterBefore,oxygenAfter-oxygenBefore);
    }

    private static Vector2 FindWater(IEnvironmentField environment,float worldSize)
    {
        for(int y=1;y<15;y++)for(int x=1;x<15;x++)
        {
            Vector2 position=new(worldSize*x/16f,worldSize*y/16f);
            if(environment.Sample(position).WaterDepth>6)return position;
        }
        throw new InvalidOperationException("Precision diagnostic requires a water column.");
    }

    private static double Relative(double reference,double balanced)=>
        Math.Abs(reference-balanced)/Math.Max(1e-9,Math.Abs(reference));

    private readonly record struct TrialResult(double SubstrateUptake,double OxygenUptake,
        double LightEnergy,double WaterExchange,double FinalEnergy,double ElapsedMilliseconds,
        double MatterError,double OxygenError);
}
