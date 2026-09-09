using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct SpatialCompetitionDiagnosticResult(
    double SmallBodyRadius,double LargeBodyRadius,double ResolvedContactDistance,
    double MaximumContactPressure,double ActiveInteractionEnergy,double LowEnergyInteractionEnergy,
    double EqualRequestDifference,double ReversedOrderDifference,double ReservationConservationError,
    long BlockedBirthAttempts,double BlockedBirthResourceLoss,long ShortRunBirths,
    int MaximumNeighbors,bool Passed)
{
    public bool DepthSeparatesOccupancy { get; init; }
    public int ContestingFramesAfterFirst { get; init; }
    public double BirthTrialMatterError { get; init; }
}

public static class SpatialCompetitionDiagnostics
{
    public static SpatialCompetitionDiagnosticResult Run()
    {
        Genome genome=Genome.CreateAncestor();
        DevelopingBody small=new(genome,0.25,0.0,1.0,0.0,0.1);
        DevelopingBody large=new(genome,genome.Regions.Select(gene=>new BodyRegion(
            gene.RegionId,BodyCalculator.TargetMatter(gene),1.0,Energy:5.0,Water:1.0)));
        double smallRadius=OccupancyShape.FromBody(small).HorizontalRadius;
        double largeRadius=OccupancyShape.FromBody(large).HorizontalRadius;
        OccupancyShape shape=OccupancyShape.FromBody(small);
        SpatialOccupancyIndex index=new(2.8f);
        index.Upsert(new(1,new Vector2(10,10),2,shape));
        bool depthSeparates=!index.CanPlace(new Vector2(10,10),2,shape)&&
            index.CanPlace(new Vector2(10,10),2+shape.VerticalHalfExtent*2.1f,shape);

        ContactTrial active=RunContactTrial(14.0);
        ContactTrial resting=RunContactTrial(0.05);
        AllocationTrial allocation=RunAllocationTrial();
        BirthTrial births=RunBirthTrial();
        bool passed=largeRadius>smallRadius*1.15&&active.ResolvedDistance>=0.97&&
            depthSeparates&&active.MaximumPressure>0.0&&active.EnergySpent>0.0&&active.ContestingFrames>0&&
            resting.EnergySpent<active.EnergySpent*0.05+1e-12&&
            allocation.EqualDifference<1e-12&&allocation.ReverseDifference<1e-12&&
            allocation.ConservationError<1e-12&&births.Births>0&&births.Blocked>0&&
            births.ResourceLoss<1e-12&&Math.Abs(births.MatterError)<1e-9;
        return new(smallRadius,largeRadius,active.ResolvedDistance,active.MaximumPressure,
            active.EnergySpent,resting.EnergySpent,allocation.EqualDifference,
            allocation.ReverseDifference,allocation.ConservationError,births.Blocked,
            births.ResourceLoss,births.Births,active.MaximumNeighbors,passed)
        {
            DepthSeparatesOccupancy=depthSeparates,
            ContestingFramesAfterFirst=active.ContestingFrames,
            BirthTrialMatterError=births.MatterError
        };
    }

    private static ContactTrial RunContactTrial(double ancestorEnergy)
    {
        SimulationConfig config=new(){AncestorEnergy=ancestorEnergy,ReproductionEnergyThreshold=100.0};
        SimulationWorld world=new(config,90210,2,mutationsEnabled:false);
        for(int step=0;step<(ancestorEnergy>1.0?300:1);step++)world.Step();
        double radius=world.Organisms.Max(organism=>OccupancyShape.FromBody(organism.Body).HorizontalRadius);
        float depth=world.Organisms[0].Depth;
        Vector2 center=new(256,256);
        world.RelocateForMediumDiagnostic(world.Organisms[0].Id,center-new Vector2((float)(radius*0.99),0),depth,0.0);
        world.RelocateForMediumDiagnostic(world.Organisms[1].Id,center+new Vector2((float)(radius*0.99),0),depth,Math.PI);
        double pressure=0.0,energy=0.0;int neighbors=0,contestingFrames=0;
        for(int step=0;step<20;step++)
        {
            world.Step();
            pressure=Math.Max(pressure,world.Organisms.Max(organism=>organism.ContactPressure));
            energy+=world.Organisms.Sum(organism=>organism.InteractionEnergyLastStep);
            neighbors=Math.Max(neighbors,world.Organisms.Max(organism=>organism.ContactNeighborCount));
            if(step>0&&world.Organisms.Any(organism=>organism.InteractionState==InteractionState.Contesting))contestingFrames++;
        }
        Organism first=world.Organisms.Single(organism=>organism.Id==1);
        Organism second=world.Organisms.Single(organism=>organism.Id==2);
        OccupancyShape firstShape=OccupancyShape.FromBody(first.Body);
        OccupancyShape secondShape=OccupancyShape.FromBody(second.Body);
        double normalized=Vector2.Distance(first.Position,second.Position)/
            (firstShape.HorizontalRadius+secondShape.HorizontalRadius);
        return new(normalized,pressure,energy,neighbors,contestingFrames);
    }

    private static AllocationTrial RunAllocationTrial()
    {
        SimulationConfig config=new(){WorldSize=32,EnvironmentGridSize=8};
        Vector2 point=new(16,16);
        MatterUptakeRequest first=new(1,point,10),second=new(2,point,10);
        BilinearEnvironmentField forward=new(config,new DeterministicRandom(41,1));
        double before=forward.TotalMinerals+forward.TotalDetritus;
        IReadOnlyDictionary<ulong,MatterReservation> a=forward.ReserveMatter([first,second]);
        double equal=Math.Abs(a[1].Total-a[2].Total);
        foreach(MatterReservation reservation in a.Values)forward.ReturnMatter(reservation,reservation.Total);
        double conservation=Math.Abs(before-(forward.TotalMinerals+forward.TotalDetritus));

        BilinearEnvironmentField reverse=new(config,new DeterministicRandom(41,1));
        IReadOnlyDictionary<ulong,MatterReservation> b=reverse.ReserveMatter([second,first]);
        double reversed=Math.Max(Math.Abs(a[1].Total-b[1].Total),Math.Abs(a[2].Total-b[2].Total));
        return new(equal,reversed,conservation);
    }

    private static BirthTrial RunBirthTrial()
    {
        SimulationConfig config=new()
        {
            WorldSize=8,EnvironmentGridSize=8,TerrainElevationOffset=-100,
            MaxPopulation=256,MaturityAgeSeconds=1.0,GrowthMatterPerSecond=4.0,
            ReproductionEnergyThreshold=9.0,ReproductionCooldownSeconds=4.0,
            ReproductionBlockedRetrySeconds=0.5,NewbornOffsetRadius=0.1f,
            MaximumAquaticSpawnAttempts=8192,SurfaceLightEnergyPerWorldAreaPerSecond=2
        };
        SimulationWorld world=new(config,77123,32,mutationsEnabled:false);
        // Deliberately crowded, resource-supplied fixture to isolate blocked
        // placement from starvation. This is not an unmodified ecology trial.
        world.QueueEnvironmentBrush(new(new Vector2(4,4),20,20,EnvironmentBrushChannel.Minerals));
        for(int step=0;step<300;step++)world.Step();
        SimulationSnapshot snapshot=world.CaptureSnapshot();
        return new(world.CumulativeBlockedBirthAttempts,world.MaximumBlockedBirthResourceLoss,
            world.CumulativeBirths,snapshot.MatterError);
    }

    private readonly record struct ContactTrial(
        double ResolvedDistance,double MaximumPressure,double EnergySpent,int MaximumNeighbors,int ContestingFrames);
    private readonly record struct AllocationTrial(
        double EqualDifference,double ReverseDifference,double ConservationError);
    private readonly record struct BirthTrial(
        long Blocked,double ResourceLoss,long Births,double MatterError);
}
