using System.Numerics;

namespace NativeEpoch.Simulation;

public enum AssayHabitat { Water, Shore, Land }

public readonly record struct FitnessAssay(
    AssayHabitat Habitat,int Replicates,int FinalPopulation,long Births,long Deaths,
    int MatureDescendants,int MatureGrandchildren,int MaximumGeneration,double LivingEnergy,
    double BodyMatter,double MeanHydration,double MovementEnergy)
{
    public double ReproductiveIndex=>(Births+2.0*MatureGrandchildren)/Math.Max(1,Replicates);
    public double DescriptiveScore=>(FinalPopulation+0.35*Births-0.20*Deaths+0.5*MatureDescendants+0.02*LivingEnergy)/Math.Max(1,Replicates);
}

public sealed record LineageExperimentResult(
    ulong Seed,bool CandidateFound,ulong CandidateId,ulong CandidateParentId,string LineagePath,
    int CandidateGeneration,ulong AncestorFingerprint,ulong CandidateFingerprint,string ReversionKind,
    bool CommonGardenGenerationCompleted,
    FitnessAssay AncestorWater,FitnessAssay CandidateWater,FitnessAssay RevertedWater,
    FitnessAssay AncestorLand,FitnessAssay CandidateLand,FitnessAssay RevertedLand,
    bool SourceHabitatAdvantage,bool ReversionSupportsChange,bool LandAdaptationObserved,string Conclusion);

public static class AdaptationExperiment
{
    public static IReadOnlyList<LineageExperimentResult> RunThreeSeeds(int sourceSteps=3000,int assaySteps=6500)
    {ulong[] seeds=[20260908,20260929,20261011];return seeds.Select(seed=>Run(seed,sourceSteps,assaySteps,false)).ToArray();}

    public static LineageExperimentResult Run(ulong seed,int sourceSteps,int assaySteps,bool resourcePulse=false)
    {
        SimulationConfig sourceConfig=new(){EnvironmentGridSize=64,MaxPopulation=1800};
        Genome ancestor=Genome.CreateAncestor();SimulationWorld source=new(sourceConfig,seed,12);source.Run(sourceSteps);
        Organism[] candidates=source.Organisms.Where(o=>o.Generation>=1&&o.Maturity>=0.95&&
            source.Genomes.Get(o.GenomeId).Fingerprint!=ancestor.Fingerprint)
            .OrderByDescending(o=>o.Generation).ThenByDescending(o=>o.Body.TotalEnergy).ToArray();
        FitnessAssay aw=Assay(ancestor,seed,AssayHabitat.Water,assaySteps,resourcePulse);
        FitnessAssay al=Assay(ancestor,seed,AssayHabitat.Land,assaySteps,resourcePulse);
        if(candidates.Length==0)
            return new(seed,false,0,0,"none",0,ancestor.Fingerprint,0,"none",CommonGarden(ancestor,seed),
                aw,default,default,al,default,default,false,false,false,
                "no living mature mutated descendant in the bounded natural run; no adaptation claim");

        Organism selected=candidates[0];Genome candidate=source.Genomes.Get(selected.GenomeId);
        (Genome reverted,string reversion)=RevertOneChange(candidate,ancestor);
        bool commonGarden=CommonGarden(candidate,seed);
        FitnessAssay cw=Assay(candidate,seed,AssayHabitat.Water,assaySteps,resourcePulse);
        FitnessAssay rw=Assay(reverted,seed,AssayHabitat.Water,assaySteps,resourcePulse);
        FitnessAssay cl=Assay(candidate,seed,AssayHabitat.Land,assaySteps,resourcePulse);
        FitnessAssay rl=Assay(reverted,seed,AssayHabitat.Land,assaySteps,resourcePulse);
        bool waterAdvantage=cw.ReproductiveIndex>aw.ReproductiveIndex*1.05&&cw.MatureGrandchildren>=aw.MatureGrandchildren;
        bool singleReversion=!reversion.StartsWith("full_",StringComparison.Ordinal);
        bool reversionSupport=singleReversion&&cw.ReproductiveIndex>rw.ReproductiveIndex*1.03;
        double ancestorCross=al.ReproductiveIndex/Math.Max(0.25,aw.ReproductiveIndex);
        double candidateCross=cl.ReproductiveIndex/Math.Max(0.25,cw.ReproductiveIndex);
        bool landAdaptation=cl.MatureGrandchildren>0&&cl.MaximumGeneration>=2&&
            candidateCross>ancestorCross*1.10&&cl.ReproductiveIndex>al.ReproductiveIndex*1.05;
        string conclusion=waterAdvantage&&reversionSupport
            ?"paired fresh-state assays support a source-water advantage; one-change reversion reduces reproduction"
            :"a mature natural descendant was recovered, but paired reproductive assays do not establish adaptation";
        conclusion+=landAdaptation?"; cross-environment reproduction supports a land advantage":"; no land adaptation was established";
        return new(seed,true,selected.Id,selected.ParentId,BuildPath(source,selected),selected.Generation,
            ancestor.Fingerprint,candidate.Fingerprint,reversion,commonGarden,aw,cw,rw,al,cl,rl,
            waterAdvantage,reversionSupport,landAdaptation,conclusion);
    }

    private static bool CommonGarden(Genome genome,ulong seed)
    {
        FitnessAssay result=Assay(genome,seed^0xC011AB1EUL,AssayHabitat.Water,6500,true);
        return result.MatureDescendants>0;
    }

    private static FitnessAssay Assay(Genome genome,ulong seed,AssayHabitat habitat,int steps,bool resourcePulse)
    {
        const int replicates=2;int pop=0,mature=0,grand=0,maxGeneration=0;long births=0,deaths=0;
        double energy=0,matter=0,hydration=0,movement=0;
        for(int replicate=0;replicate<replicates;replicate++)
        {
            SimulationConfig config=new(){EnvironmentGridSize=48,MaxPopulation=600,
                SenescenceOnsetSeconds=1000,SenescenceHazardPerSecond=0.000001,
                JuvenileHazardPerSecond=0.002,SurfaceLightEnergyPerWorldAreaPerSecond=0.08};
            SimulationWorld world=new(config,(seed^0xA55A5AA5UL)+(ulong)replicate,1,genome,mutationsEnabled:false);
            IReadOnlyList<Vector2> sites=FindHabitatSites(world.Environment,config.WorldSize,habitat,1);
            for(int index=0;index<world.Organisms.Count;index++)
            {
                Organism organism=world.Organisms[index];Vector2 position=sites[index];EnvironmentSample sample=world.Environment.Sample(position);
                float depth=habitat switch{AssayHabitat.Water=>(float)Math.Clamp(sample.WaterDepth*0.25,0.2,4.0),
                    AssayHabitat.Shore=>(float)Math.Clamp(sample.WaterDepth*0.45,0.02,0.8),_=>0};
                world.RelocateForMediumDiagnostic(organism.Id,position,depth);
                EnvironmentSample placed=world.Environment.Sample(position,depth);
                bool legal=habitat switch{AssayHabitat.Water=>placed.WaterDepth>=4&&depth>0,
                    AssayHabitat.Shore=>placed.WaterDepth>0.1&&placed.WaterDepth<2,_=>placed.WaterDepth<=1e-6};
                if(!legal)throw new InvalidOperationException("Paired assay placement crossed its target medium.");
                if(resourcePulse)
                    world.QueueEnvironmentBrush(new EnvironmentBrushCommand(position,8f,4.0,EnvironmentBrushChannel.Minerals));
            }
            world.ApplyQueuedCommands();
            world.Run(steps);SimulationSnapshot snapshot=world.CaptureSnapshot();
            pop+=snapshot.Population;births+=snapshot.CumulativeBirths;deaths+=snapshot.CumulativeDeaths;
            mature+=world.Organisms.Count(o=>o.Generation>=1&&o.Maturity>=0.95);
            grand+=world.Organisms.Count(o=>o.Generation>=2&&o.Maturity>=0.95);
            maxGeneration=Math.Max(maxGeneration,world.Organisms.Count==0?0:world.Organisms.Max(o=>o.Generation));
            energy+=snapshot.LivingEnergy;matter+=snapshot.OrganismBodyMatter;hydration+=snapshot.AverageHydration;movement+=snapshot.CumulativeMovementEnergy;
        }
        return new(habitat,replicates,pop,births,deaths,mature,grand,maxGeneration,energy,matter,hydration/replicates,movement);
    }

    private static IReadOnlyList<Vector2> FindHabitatSites(IEnvironmentField environment,float size,AssayHabitat habitat,int count)
    {
        List<(Vector2 Position,double Error)> candidates=[];double target=habitat switch{AssayHabitat.Water=>10,AssayHabitat.Shore=>0.8,_=>0};
        for(int y=0;y<=64;y++)for(int x=0;x<=64;x++)
        {Vector2 p=new(size*x/64f,size*y/64f);double depth=environment.Sample(p).WaterDepth;
            bool legal=habitat switch{AssayHabitat.Water=>depth>=4&&depth<=18,AssayHabitat.Shore=>depth>0.1&&depth<2,_=>depth<=1e-6};
            if(legal){EnvironmentSample sample=environment.Sample(p);
                candidates.Add((p,Math.Abs(depth-target)-(0.7*sample.Light)-(0.35*sample.Minerals)));}}
        List<Vector2> result=[];
        foreach(var candidate in candidates.OrderBy(c=>c.Error).ThenBy(c=>c.Position.X).ThenBy(c=>c.Position.Y))
        {if(result.All(p=>Vector2.DistanceSquared(p,candidate.Position)>=9)){result.Add(candidate.Position);if(result.Count==count)break;}}
        if(result.Count<count)throw new InvalidOperationException($"Only {result.Count} validated {habitat} assay sites exist.");
        return result;
    }

    private static string BuildPath(SimulationWorld world,Organism selected)
    {
        Dictionary<ulong,ulong> parents=world.RecentBirths.ToDictionary(r=>r.ChildId,r=>r.ParentId);
        List<ulong> path=[selected.Id];ulong current=selected.ParentId;int guard=0;
        while(current!=0&&guard++<64){path.Add(current);if(!parents.TryGetValue(current,out current))break;}
        path.Reverse();return string.Join('>',path);
    }

    private static (Genome Genome,string Kind) RevertOneChange(Genome candidate,Genome ancestor)
    {
        if(!candidate.Metabolism.Equals(ancestor.Metabolism))return(new Genome(candidate.Regions,candidate.MutationRate,ancestor.Metabolism,candidate.ControllerNodes,candidate.Sensors),"metabolism");
        if(!candidate.ControllerNodes.SequenceEqual(ancestor.ControllerNodes))return(new Genome(candidate.Regions,candidate.MutationRate,candidate.Metabolism,ancestor.ControllerNodes,candidate.Sensors),"controller");
        if(!candidate.Sensors.SequenceEqual(ancestor.Sensors))return(new Genome(candidate.Regions,candidate.MutationRate,candidate.Metabolism,candidate.ControllerNodes,ancestor.Sensors),"sensors");
        if(candidate.Regions.Count==ancestor.Regions.Count&&candidate.Regions.Select(r=>r.RegionId).SequenceEqual(ancestor.Regions.Select(r=>r.RegionId)))
        {RegionGene[] regions=candidate.Regions.ToArray();for(int i=0;i<regions.Length;i++)if(!regions[i].Equals(ancestor.Regions[i]))
            {int id=regions[i].RegionId;regions[i]=ancestor.Regions[i];return(new Genome(regions,candidate.MutationRate,candidate.Metabolism,candidate.ControllerNodes,candidate.Sensors),$"region_{id}");}}
        return(ancestor,"full_topology_ancestor_control");
    }
}
