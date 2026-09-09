using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct TissueExpressionDiagnosticResult(
    bool Passed,
    double ExpressedSignal,
    double NoExpressionSignal,
    double NoEnergySignal,
    double UndevelopedSourceSignal,
    double ExpressedControllerOutput,
    double DisconnectedControllerOutput,
    double ExpressedConstructionMatter,
    double BareConstructionMatter,
    bool ExactInheritance,
    bool SensorPointValid,
    bool SensorReconnectValid);

public static class TissueExpressionDiagnostics
{
    public static TissueExpressionDiagnosticResult Run()
    {
        SimulationConfig config=new();
        RegionGene baseCore=Genome.CreateAncestor().Regions[0] with
        {
            ParentRegionId=-1,MatterSourceRegionId=-1,SignalSourceRegionId=-1,IsCore=true,
            AppearanceMaturity=0,ExchangeExpression=0.65,BarrierExpression=0.2,
            ContractileExpression=0.45,StructuralExpression=0.45,SensoryExpression=0.85
        };
        ControllerNodeGene controller=new(0,0,0,0,0,0,0,0,0,-1,0,1,0,0,0,0);
        SensorGene sensor=new(SensorChannel.ChemicalResource,baseCore.RegionId,0,1.0,8.0);
        Genome expressed=new([baseCore],0,controllerNodes:[controller],sensors:[sensor]);
        Genome silent=new([baseCore with{SensoryExpression=0}],0,controllerNodes:[controller],sensors:[sensor]);
        double expressedSignal=Sense(expressed,2.0,config,out double expressedOutput);
        double silentSignal=Sense(silent,2.0,config,out double silentOutput);
        double noEnergy=Sense(expressed,0.0,config,out _);

        RegionGene branch=baseCore with{RegionId=1,ParentRegionId=0,MatterSourceRegionId=0,
            SignalSourceRegionId=0,IsCore=false,AppearanceMaturity=0.95};
        Genome undeveloped=new([baseCore,branch],0,controllerNodes:[controller],
            sensors:[sensor with{SourceRegionId=1}]);
        double disconnected=Sense(undeveloped,2.0,config,out double disconnectedOutput);

        Genome bare=new([baseCore with{ExchangeExpression=0,BarrierExpression=0,
            ContractileExpression=0,StructuralExpression=0,SensoryExpression=0}],0,
            controllerNodes:[controller],sensors:[sensor]);
        GenomeMutator mutator=new();
        MutationResult exact=mutator.Inherit(expressed,new DeterministicRandom(1,11),new DeterministicRandom(2,12));
        MutationResult point=mutator.MutateSensorForced(expressed,new DeterministicRandom(3,13),MutationKind.SensorPoint);
        MutationResult reconnect=mutator.MutateSensorForced(expressed,new DeterministicRandom(4,14),MutationKind.SensorReconnect);
        bool passed=expressedSignal>0.02&&silentSignal<1e-12&&noEnergy<1e-12&&disconnected<1e-12&&
            Math.Abs(expressedOutput-silentOutput)>1e-4&&BodyCalculator.TargetMatter(baseCore)>
            BodyCalculator.TargetMatter(bare.Regions[0])&&ReferenceEquals(exact.Genome,expressed)&&
            point.Genome.Sensors.Count==1&&reconnect.Genome.Sensors.Count==1;
        return new(passed,expressedSignal,silentSignal,noEnergy,disconnected,expressedOutput,
            disconnectedOutput,BodyCalculator.TargetMatter(baseCore),BodyCalculator.TargetMatter(bare.Regions[0]),
            ReferenceEquals(exact.Genome,expressed),point.Genome.Sensors.Count==1,reconnect.Genome.Sensors.Count==1);
    }

    private static double Sense(Genome genome,double energy,SimulationConfig config,out double output)
    {
        double coreMatter=Math.Min(config.CoreInitialMatter,BodyCalculator.TargetMatter(genome.Regions[0]));
        DevelopingBody body=new(genome,coreMatter,0,energy,0.1,coreMatter);
        // Let the expressed phenotype track the paid developmental state.
        for(int i=0;i<20;i++)body.UpdateFunctionalState(genome,ControllerOutputs.Basal,
            0.5,1.0,new ForagingDecision(0,0,0,1,0,0,0.5,0),config.FixedDeltaSeconds);
        double[] sensorState=[],nodes=[];
        TissueSensingResult result=TissueSensing.Evaluate(body,genome,new SensorEnvironment(),
            new Vector2(8,8),0,1,0,config,config.FixedDeltaSeconds,ref sensorState,ref nodes);
        ControllerEvaluation evaluation=BehaviorController.Evaluate(genome,null,default,nodes);
        output=evaluation.Outputs.ContractionActivation;
        return result.ChemicalSignal;
    }

    private sealed class SensorEnvironment:IEnvironmentField
    {
        public EnvironmentSample Sample(Vector2 position,float depth=0)=>new(
            -5,0,5,depth,1.1,18,0.4,1.0,0.5,0,1,0.4,0.2,Vector2.Zero);
    }
}
