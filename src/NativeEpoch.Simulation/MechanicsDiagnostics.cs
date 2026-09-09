using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct MechanicsDiagnosticResult(
    double SingleRegionDisplacement,double ZeroActuationDisplacement,double ReciprocalDisplacement,
    double MultiRegionDisplacement,double GroundSupportedDisplacement,double UnsupportedDisplacement,
    double EnergySpent,double MaximumInternalResidual,double MaximumBalanceResidual,
    double ConnectionLoad,double WeakLinkPoseDifference,double ActiveSurfaceSphereDisplacement,bool Passed)
{
    public double UnpoweredSurfaceDisplacement { get; init; }
    public bool PositiveSphereSections { get; init; }
}

public static class MechanicsDiagnostics
{
    public static MechanicsDiagnosticResult Run()
    {
        SimulationConfig config=new();
        SimulationConfig pureShapeConfig=config with{ActiveSurfaceDriveSpeed=0};
        (string _,Genome single)=MorphologyGenomeFactory.BuildSixDiagnostics()[0];
        Genome multi=Genome.CreateAncestor();
        Genome weak=new(multi.Regions.Select(g=>g.IsCore?g:g with{Rigidity=0,Toughness=0}),
            multi.MutationRate,multi.Metabolism,multi.ControllerNodes);
        ControllerOutputs active=ControllerOutputs.Basal with{ContractionActivation=1,LateralContraction=0.9};
        TrialResult sphere=Trial(single,active,1,1,pureShapeConfig,true,false);
        TrialResult activeSphere=Trial(single,active,1,1,config,true,false);
        TrialResult unpoweredSphere=Trial(single,active,1,1,config,true,false,0.0);
        TrialResult zero=Trial(multi,active with{ContractionActivation=0},1,1,config,true,false);
        TrialResult reciprocal=Trial(multi,active,1,1,pureShapeConfig,true,true);
        TrialResult swimmer=Trial(multi,active,1,1,pureShapeConfig,true,false);
        TrialResult ground=Trial(multi,active,0,1,pureShapeConfig,true,false);
        TrialResult unsupported=Trial(multi,active,0,1,config,false,false);
        TrialResult weakTrial=Trial(weak,active,1,1,pureShapeConfig,true,false);
        double weakDifference=Vector2.Distance(swimmer.PoseSignature,weakTrial.PoseSignature);
        bool passed=sphere.Distance<1e-6&&zero.Distance<1e-5&&
            reciprocal.Distance<swimmer.Distance*0.75&&swimmer.Distance>1e-4&&
            ground.Distance>0&&unsupported.Distance<1e-8&&swimmer.Energy>0&&
            activeSphere.Distance>0.1&&activeSphere.Energy>0&&
            unpoweredSphere.Distance<1e-8&&unpoweredSphere.Energy==0&&
            sphere.PositiveSections&&activeSphere.PositiveSections&&unpoweredSphere.PositiveSections&&
            swimmer.InternalResidual<1e-7&&swimmer.BalanceResidual<1e-7&&
            swimmer.ConnectionLoad>0&&weakDifference>1e-4;
        return new(sphere.Distance,zero.Distance,reciprocal.Distance,swimmer.Distance,
            ground.Distance,unsupported.Distance,swimmer.Energy,swimmer.InternalResidual,
            swimmer.BalanceResidual,swimmer.ConnectionLoad,weakDifference,activeSphere.Distance,passed)
        { UnpoweredSurfaceDisplacement=unpoweredSphere.Distance,
          PositiveSphereSections=sphere.PositiveSections&&activeSphere.PositiveSections&&unpoweredSphere.PositiveSections };
    }

    private static TrialResult Trial(Genome genome,ControllerOutputs outputs,double immersion,
        double hydration,SimulationConfig config,bool groundSupported,bool reciprocal,double initialEnergy=12.0)
    {
        BodyRegion[] state=genome.Regions.Select(g=>new BodyRegion(g.RegionId,
            BodyCalculator.TargetMatter(g),1.0,Energy:initialEnergy,Water:1.0)).ToArray();
        DevelopingBody body=new(genome,state);BodyPose pose=new(genome,body);
        Vector2 position=Vector2.Zero;double heading=0,energy=0,internalResidual=0,balanceResidual=0,connectionLoad=0;
        bool positiveSections=true;
        for(int step=0;step<160;step++)
        {
            body.UpdateFunctionalState(genome,outputs,body.Regions.ToDictionary(r=>r.RegionId,
                r=>reciprocal?0.0:Math.Sin(step*0.11+r.RegionId)),config.FixedDeltaSeconds);
            BodyMechanicsResult result=pose.Step(genome,body,outputs,step*config.FixedDeltaSeconds,
                immersion,hydration,config,config.FixedDeltaSeconds,groundSupported,reciprocal);
            double c=Math.Cos(heading),s=Math.Sin(heading);Vector2 local=result.MediumVelocity;
            if(step>=80)position+=new Vector2((float)(local.X*c-local.Y*s),(float)(local.X*s+local.Y*c))*(float)config.FixedDeltaSeconds;
            heading+=result.AngularVelocity*config.FixedDeltaSeconds;
            positiveSections &= pose.Regions.All(r=>double.IsFinite(r.Width)&&double.IsFinite(r.Thickness)&&r.Width>0&&r.Thickness>0);
            energy+=result.EnergySpent;internalResidual=Math.Max(internalResidual,result.InternalForceResidual);
            balanceResidual=Math.Max(balanceResidual,result.ForceBalanceResidual);connectionLoad+=result.ConnectionLoad;
        }
        Vector2 signature=pose.Regions.Aggregate(Vector2.Zero,(sum,r)=>sum+r.LocalCenter*(r.RegionId+1));
        return new(position.Length(),energy,internalResidual,balanceResidual,connectionLoad,signature,positiveSections);
    }

    private readonly record struct TrialResult(double Distance,double Energy,double InternalResidual,
        double BalanceResidual,double ConnectionLoad,Vector2 PoseSignature,bool PositiveSections);
}
