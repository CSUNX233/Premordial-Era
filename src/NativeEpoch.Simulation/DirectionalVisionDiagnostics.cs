using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct DirectionalVisionDiagnosticResult(
    bool Passed,double TowardLight,double AwayFromLight,double NoExpression,double NoEnergy,
    double TerrainOccluded,double BodyOccluded,int ActiveVisualSensors,double EnergySpent);

public static class DirectionalVisionDiagnostics
{
    public static DirectionalVisionDiagnosticResult Run()
    {
        SimulationConfig config=new();
        RegionGene core=Genome.CreateAncestor().Regions[0] with{SensoryExpression=0.9,LightReactivity=0.9};
        ControllerNodeGene node=new(0,0,0,0,0,0,0,0,0,-1,0,1,0,0,0,0);
        double Run(RegionGene region,double direction,double energy,out TissueSensingResult result,
            IEnvironmentField? environment=null)
        {
            SensorGene sensor=new(SensorChannel.DirectionalLight,region.RegionId,0,1.0,8.0,
                direction,4.0,0.9);
            Genome genome=new([region],0,controllerNodes:[node],sensors:[sensor]);
            DevelopingBody body=new(genome,Math.Min(config.CoreInitialMatter,BodyCalculator.TargetMatter(region)),
                0,energy,0.1,config.CoreInitialMatter);
            for(int i=0;i<20;i++)body.UpdateFunctionalState(genome,ControllerOutputs.Basal,
                0.5,1,new ForagingDecision(0,0,0,1,0,0,0.5,0),config.FixedDeltaSeconds);
            double[] state=[],inputs=[];
            result=TissueSensing.Evaluate(body,genome,environment??new LightGradientEnvironment(),
                new Vector2(8,8),0,1,0,config,config.FixedDeltaSeconds,ref state,ref inputs);
            return result.VisionSignal;
        }
        double toward=Run(core,0,2,out TissueSensingResult active);
        double away=Run(core,Math.PI,2,out _);
        double silent=Run(core with{SensoryExpression=0},0,2,out _);
        double noEnergy=Run(core,0,0,out _);
        double terrainOccluded=Run(core,0,2,out _,new RidgeEnvironment());
        RegionGene blocker=Genome.CreateAncestor().Regions[1] with
        {AppearanceMaturity=0,RelativeAngle=0,TargetLength=1.4,TargetWidth=0.8};
        SensorGene visual=new(SensorChannel.DirectionalLight,core.RegionId,0,1,8,0,4,0.9);
        Genome blockedGenome=new([core,blocker],0,controllerNodes:[node],sensors:[visual]);
        BodyRegion[] blockedRegions=blockedGenome.Regions.Select(gene=>new BodyRegion(gene.RegionId,
            BodyCalculator.TargetMatter(gene),1,Energy:2,TransportAvailability:1,
            SensoryExpression:gene.SensoryExpression)).ToArray();
        DevelopingBody blockedBody=new(blockedGenome,blockedRegions);
        double[] blockedState=[],blockedNodes=[];
        TissueSensingResult bodyBlocked=TissueSensing.Evaluate(blockedBody,blockedGenome,
            new LightGradientEnvironment(),new Vector2(8,8),0,1,0,config,config.FixedDeltaSeconds,
            ref blockedState,ref blockedNodes);
        bool passed=toward>away+0.01&&silent<1e-12&&noEnergy<1e-12&&
            terrainOccluded<1e-12&&bodyBlocked.VisionSignal<1e-12&&
            active.ActiveVisualSensorCount==1&&active.EnergySpent>0;
        return new(passed,toward,away,silent,noEnergy,terrainOccluded,bodyBlocked.VisionSignal,
            active.ActiveVisualSensorCount,active.EnergySpent);
    }

    private sealed class LightGradientEnvironment:IEnvironmentField
    {
        public EnvironmentSample Sample(Vector2 position,float depth=0)
        {
            double light=Math.Clamp(position.X/16.0,0,1);
            return new(-5,0,5,depth,1.1,18,light,0.5,0.2,0,1,0.4,0.2,Vector2.Zero);
        }
    }

    private sealed class RidgeEnvironment:IEnvironmentField
    {
        public EnvironmentSample Sample(Vector2 position,float depth=0)
        {
            double terrain=position.X>8.5?-0.2:-5.0;
            return new(terrain,0,Math.Max(0,-terrain),depth,1.1,18,
                Math.Clamp(position.X/16.0,0,1),0.5,0.2,0,1,0.4,0.2,Vector2.Zero);
        }
    }
}
