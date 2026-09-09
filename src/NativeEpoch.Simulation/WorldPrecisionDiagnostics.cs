using System.Diagnostics;
using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct WorldPrecisionMetrics(
    int Population,long Births,long Deaths,double LivingEnergy,double StoredMatter,
    double BodyMatter,double OxygenUptake,double MovementEnergy,double AverageSpeed,
    double MeanFounderDisplacement,double MatterError,double OxygenError,bool AllFinite);

public readonly record struct WorldPrecisionDiagnosticResult(
    bool Passed,bool MutationsEnabled,int Ancestors,int WarmupSteps,int MeasuredSteps,
    double ReferenceMilliseconds,double BalancedMilliseconds,double TimingReduction,
    double MaximumRelativeEffectError,double DeathCohortDifference,WorldPrecisionMetrics Reference,
    WorldPrecisionMetrics Balanced);

/// <summary>
/// Measures the complete simulation step with the same random founders, seed and load,
/// and compares the resulting ecological state after a fixed simulated duration.
/// </summary>
public static class WorldPrecisionDiagnostics
{
    public static WorldPrecisionDiagnosticResult Run(int ancestors=300,int warmupSteps=80,
        int measuredSteps=300,ulong seed=20260908)
    {
        if(ancestors<1)throw new ArgumentOutOfRangeException(nameof(ancestors));
        if(warmupSteps<0||measuredSteps<100)throw new ArgumentOutOfRangeException(nameof(measuredSteps));

        // Settle tiered JIT on the same code paths before collecting either profile.
        _=Trial(CorePrecisionProfile.Reference,seed,Math.Min(32,ancestors),20,100);
        _=Trial(CorePrecisionProfile.Balanced,seed,Math.Min(32,ancestors),20,100);

        TrialResult reference=Trial(CorePrecisionProfile.Reference,seed,ancestors,warmupSteps,measuredSteps);
        TrialResult balanced=Trial(CorePrecisionProfile.Balanced,seed,ancestors,warmupSteps,measuredSteps);
        TrialResult balancedSecond=Trial(CorePrecisionProfile.Balanced,seed,ancestors,warmupSteps,measuredSteps);
        TrialResult referenceSecond=Trial(CorePrecisionProfile.Reference,seed,ancestors,warmupSteps,measuredSteps);
        TrialResult referenceThird=Trial(CorePrecisionProfile.Reference,seed,ancestors,warmupSteps,measuredSteps);
        TrialResult balancedThird=Trial(CorePrecisionProfile.Balanced,seed,ancestors,warmupSteps,measuredSteps);

        double referenceMs=Median(reference.ElapsedMilliseconds,referenceSecond.ElapsedMilliseconds,
            referenceThird.ElapsedMilliseconds);
        double balancedMs=Median(balanced.ElapsedMilliseconds,balancedSecond.ElapsedMilliseconds,
            balancedThird.ElapsedMilliseconds);
        double timingReduction=1.0-balancedMs/Math.Max(1e-9,referenceMs);
        WorldPrecisionMetrics referenceMetrics=reference.Metrics;
        WorldPrecisionMetrics balancedMetrics=balanced.Metrics;
        double maximumError=new[]
        {
            Relative(referenceMetrics.Population,balancedMetrics.Population,1),
            Relative(referenceMetrics.Births,balancedMetrics.Births,1),
            Math.Abs(referenceMetrics.Deaths-balancedMetrics.Deaths)/(double)ancestors,
            Relative(referenceMetrics.LivingEnergy,balancedMetrics.LivingEnergy,1),
            Relative(referenceMetrics.StoredMatter,balancedMetrics.StoredMatter,1),
            Relative(referenceMetrics.BodyMatter,balancedMetrics.BodyMatter,1),
            Relative(referenceMetrics.OxygenUptake,balancedMetrics.OxygenUptake,1),
            Relative(referenceMetrics.MovementEnergy,balancedMetrics.MovementEnergy,1),
            Relative(referenceMetrics.AverageSpeed,balancedMetrics.AverageSpeed,1e-6),
            Relative(referenceMetrics.MeanFounderDisplacement,balancedMetrics.MeanFounderDisplacement,1e-6)
        }.Max();
        bool ledgersValid=Math.Abs(referenceMetrics.MatterError)<1e-8&&
            Math.Abs(balancedMetrics.MatterError)<1e-8&&
            Math.Abs(referenceMetrics.OxygenError)<1e-7&&
            Math.Abs(balancedMetrics.OxygenError)<1e-7;
        double deathCohortDifference=Math.Abs(referenceMetrics.Deaths-balancedMetrics.Deaths)/(double)ancestors;
        bool passed=referenceMetrics.AllFinite&&balancedMetrics.AllFinite&&ledgersValid&&
            maximumError<=0.10+1e-12&&timingReduction>=0.09;
        return new(passed,false,ancestors,warmupSteps,measuredSteps,referenceMs,balancedMs,
            timingReduction,maximumError,deathCohortDifference,referenceMetrics,balancedMetrics);
    }

    private static TrialResult Trial(CorePrecisionProfile precision,ulong seed,int ancestors,
        int warmupSteps,int measuredSteps)
    {
        SimulationConfig config=new()
        {
            MaxPopulation=5000,
            ResourceBudgetReferenceAncestors=24,
            RandomizeFounders=true,
            PrecisionProfile=precision
        };
        // Founder morphology remains randomized, while mutation is frozen so both
        // profiles measure numerical precision rather than divergent mutation events.
        SimulationWorld world=new(config,seed,ancestors,mutationsEnabled:false);
        for(int step=0;step<warmupSteps;step++)world.Step();
        Dictionary<ulong,Vector2> founderPositions=world.Organisms
            .Where(organism=>organism.Generation==0)
            .ToDictionary(organism=>organism.Id,organism=>organism.Position);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Stopwatch stopwatch=Stopwatch.StartNew();
        for(int step=0;step<measuredSteps;step++)world.Step();
        stopwatch.Stop();
        SimulationSnapshot snapshot=world.CaptureSnapshot();
        double displacement=0;
        int displacementCount=0;
        foreach(Organism organism in world.Organisms)
        {
            if(!founderPositions.TryGetValue(organism.Id,out Vector2 start))continue;
            displacement+=Vector2.Distance(start,organism.Position);
            displacementCount++;
        }
        WorldPrecisionMetrics metrics=new(snapshot.Population,snapshot.CumulativeBirths,
            snapshot.CumulativeDeaths,snapshot.LivingEnergy,snapshot.OrganismStoredMatter,
            snapshot.OrganismBodyMatter,snapshot.CumulativeOxygenUptake,
            snapshot.CumulativeMovementEnergy,snapshot.AverageSpeed,
            displacementCount>0?displacement/displacementCount:0,
            snapshot.MatterError,snapshot.OxygenError,snapshot.AllFinite);
        return new(stopwatch.Elapsed.TotalMilliseconds,metrics);
    }

    private static double Median(double a,double b,double c)
    {
        if(a>b)(a,b)=(b,a);
        if(b>c)(b,c)=(c,b);
        return Math.Max(a,b);
    }

    private static double Relative(double reference,double observed,double floor) =>
        Math.Abs(reference-observed)/Math.Max(floor,Math.Abs(reference));

    private readonly record struct TrialResult(double ElapsedMilliseconds,WorldPrecisionMetrics Metrics);
}
