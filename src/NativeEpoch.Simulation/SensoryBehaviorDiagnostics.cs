using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct SensoryBehaviorDiagnosticResult(
    bool Passed,double ChemicalSteering,double NoChemicalSteering,
    double VisualSteering,double NoVisualSteering,double ChemicalRange,
    double ChemicalAccess,double NoEnergyAccess,double DisconnectedAccess,
    double VisualLateralSignal,double SymmetricVisualLateral,
    double SensingEnergySpent,double WorldChemotacticMinimumDistance,
    double WorldNoSensorMinimumDistance,double WorldChemotacticLateralTravel,
    double WorldNoSensorLateralTravel);

/// <summary>Small causal checks from paid local receptor to persistent steering.</summary>
public static class SensoryBehaviorDiagnostics
{
    public static SensoryBehaviorDiagnosticResult Run()
    {
        SimulationConfig config=new();
        TissueSensingResult chemical=Sense(config,SensorChannel.ChemicalResource,6.0,
            energy:2.0,motorConnected:true,direction:0);
        TissueSensingResult noEnergy=Sense(config,SensorChannel.ChemicalResource,6.0,
            energy:0.0,motorConnected:true,direction:0);
        TissueSensingResult disconnected=Sense(config,SensorChannel.ChemicalResource,6.0,
            energy:2.0,motorConnected:false,direction:0);
        TissueSensingResult vision=Sense(config,SensorChannel.DirectionalLight,4.0,
            energy:2.0,motorConnected:true,direction:Math.PI/2.0);
        double symmetricVisualLateral=SenseSymmetricVision(config);
        double chemicalSteering=Steering(config,chemical.ChemicalAccess,visualLateral:0);
        double noChemicalSteering=Steering(config,chemicalAccess:0,visualLateral:0);
        double visualSteering=Steering(config,chemicalAccess:0,vision.VisualLateralSignal);
        double noVisualSteering=Steering(config,chemicalAccess:0,visualLateral:0);
        WorldChemotaxisTrack sensed=TrackWorldChemotaxis(withSensor:true);
        WorldChemotaxisTrack blind=TrackWorldChemotaxis(withSensor:false);

        bool passed=chemicalSteering>noChemicalSteering+0.05&&
            visualSteering>noVisualSteering+0.005&&
            chemical.ChemicalAccess>0.01&&chemical.ChemicalRange>5.9&&
            noEnergy.ChemicalAccess<1e-12&&disconnected.ChemicalAccess<1e-12&&
            vision.VisualLateralSignal>0.001&&Math.Abs(symmetricVisualLateral)<1e-9&&
            chemical.EnergySpent>0&&sensed.MinimumPatchDistance+0.10<blind.MinimumPatchDistance&&
            sensed.LateralTravel>blind.LateralTravel+0.5;
        return new(passed,chemicalSteering,noChemicalSteering,visualSteering,noVisualSteering,
            chemical.ChemicalRange,chemical.ChemicalAccess,noEnergy.ChemicalAccess,
            disconnected.ChemicalAccess,vision.VisualLateralSignal,symmetricVisualLateral,
            chemical.EnergySpent+vision.EnergySpent,sensed.MinimumPatchDistance,
            blind.MinimumPatchDistance,sensed.LateralTravel,blind.LateralTravel);
    }

    private static double Steering(SimulationConfig config,double chemicalAccess,double visualLateral)
    {
        ForagingMemory memory=new();
        ForagingObservation observation=new(
            Vector2.Zero,Vector2.UnitX,-Vector2.UnitY,Vector2.UnitY,
            0.20,0.55,0.10,0.90,0,0,0,0)
        {
            ChemicalAccess=chemicalAccess,
            VisualLateralSignal=visualLateral
        };
        ForagingDecision decision=default;
        for(int step=0;step<25;step++)
            decision=BehaviorController.UpdateForaging(memory,observation,0.8,0.7,
                config,config.FixedDeltaSeconds);
        return decision.Steering;
    }

    private static TissueSensingResult Sense(SimulationConfig config,SensorChannel channel,
        double range,double energy,bool motorConnected,double direction)
    {
        RegionGene core=Genome.CreateAncestor().Regions[0] with
        {
            SensoryExpression=0.9,
            LightReactivity=0.9
        };
        ControllerNodeGene controller=motorConnected
            ?new ControllerNodeGene(0,0,0,0,0,0,0,0,0,-1,0,1,0,0,0,1)
            :new ControllerNodeGene(0,0,0,0,0,0,0,0,0,-1,0,0,0,0,0,0);
        SensorGene sensor=new(channel,core.RegionId,0,1.0,8.0,direction,range,0.9);
        Genome genome=new([core],0,controllerNodes:[controller],sensors:[sensor]);
        DevelopingBody body=new(genome,Math.Min(config.CoreInitialMatter,
            BodyCalculator.TargetMatter(core)),0,energy,0.1,config.CoreInitialMatter);
        for(int step=0;step<20;step++)
            body.UpdateFunctionalState(genome,ControllerOutputs.Basal,0.5,1,
                new ForagingDecision(0,0,0,1,0,0,0.5,0),config.FixedDeltaSeconds);
        double[] state=[],nodes=[];
        TissueSensingResult result=default;
        for(int step=0;step<12;step++)
            result=TissueSensing.Evaluate(body,genome,new GradientEnvironment(),new Vector2(8,8),
                0,1,0,config,config.FixedDeltaSeconds,ref state,ref nodes);
        return result;
    }

    private static double SenseSymmetricVision(SimulationConfig config)
    {
        RegionGene core=Genome.CreateAncestor().Regions[0] with
            {SensoryExpression=0.9,LightReactivity=0.9};
        ControllerNodeGene controller=new(0,0,0,0,0,0,0,0,0,-1,0,1,0,0,0,1);
        SensorGene left=new(SensorChannel.DirectionalLight,core.RegionId,0,1,8,
            -Math.PI/2,4,0.9);
        SensorGene right=left with{DirectionOffsetRadians=Math.PI/2};
        Genome genome=new([core],0,controllerNodes:[controller],sensors:[left,right]);
        DevelopingBody body=new(genome,Math.Min(config.CoreInitialMatter,
            BodyCalculator.TargetMatter(core)),0,2,0.1,config.CoreInitialMatter);
        for(int step=0;step<20;step++)body.UpdateFunctionalState(genome,ControllerOutputs.Basal,
            0.5,1,new ForagingDecision(0,0,0,1,0,0,0.5,0),config.FixedDeltaSeconds);
        double[] state=[],nodes=[];
        TissueSensingResult result=default;
        for(int step=0;step<12;step++)
            result=TissueSensing.Evaluate(body,genome,new UniformEnvironment(),new Vector2(8,8),
                0,1,0,config,config.FixedDeltaSeconds,ref state,ref nodes);
        return result.VisualLateralSignal;
    }

    private static WorldChemotaxisTrack TrackWorldChemotaxis(bool withSensor)
    {
        SimulationConfig config=new()
        {
            WorldSize=96,
            EnvironmentGridSize=25,
            InitialMineralScale=0.02,
            ReproductionEnergyThreshold=100,
            RandomizeFounders=false
        };
        Genome ancestor=Genome.CreateAncestor();
        Genome genome=withSensor?ancestor:new Genome(ancestor.Regions,ancestor.MutationRate,
            ancestor.Metabolism,ancestor.ControllerNodes,
            ancestor.Sensors.Select(sensor=>sensor.Channel==SensorChannel.ChemicalResource
                ?sensor with{Gain=0.0}:sensor));
        SimulationWorld world=new(config,20260910,1,genome,mutationsEnabled:false);
        for(int step=0;step<300;step++)world.Step();
        ulong organismId=world.Organisms[0].Id;
        Organism initial=world.Organisms[0];
        Vector2 start=initial.Position;
        Vector2 forward=new((float)Math.Cos(initial.HeadingRadians),(float)Math.Sin(initial.HeadingRadians));
        Vector2 lateral=new(-forward.Y,forward.X);
        // The animal has a finite turning radius: offer a reachable patch ahead
        // and to one side, rather than requiring an instantaneous sideways turn.
        Vector2 patch=Vector2.Clamp(start+forward*4+lateral*4,Vector2.Zero,new Vector2(config.WorldSize));
        if(world.Environment.Sample(patch).WaterDepth<2)
        {
            lateral=-lateral;
            patch=Vector2.Clamp(start+forward*4+lateral*4,Vector2.Zero,new Vector2(config.WorldSize));
        }
        world.QueueEnvironmentBrush(new(patch,3.2f,20,EnvironmentBrushChannel.Minerals));
        world.ApplyQueuedCommands();
        double minimumDistance=Vector2.Distance(start,patch);
        double maximumLateralTravel=0;
        for(int step=0;step<250;step++)
        {
            world.Step();
            Organism organism=world.Organisms.Single(candidate=>candidate.Id==organismId);
            minimumDistance=Math.Min(minimumDistance,Vector2.Distance(organism.Position,patch));
            maximumLateralTravel=Math.Max(maximumLateralTravel,
                Vector2.Dot(organism.Position-start,lateral));
        }
        return new(minimumDistance,maximumLateralTravel);
    }

    private sealed class GradientEnvironment:IEnvironmentField
    {
        public EnvironmentSample Sample(Vector2 position,float depth=0)
        {
            double light=Math.Clamp(position.Y/16.0,0.0,1.0);
            double minerals=Math.Clamp(position.X/16.0,0.0,1.0);
            return new(-5,0,5,depth,1.1,18,light,0.5,0.2,0,1,0.4,minerals,Vector2.Zero);
        }
    }

    private sealed class UniformEnvironment:IEnvironmentField
    {
        public EnvironmentSample Sample(Vector2 position,float depth=0) =>
            new(-5,0,5,depth,1.1,18,0.5,0.5,0.2,0,1,0.4,0.4,Vector2.Zero);
    }

    private readonly record struct WorldChemotaxisTrack(
        double MinimumPatchDistance,double LateralTravel);
}
