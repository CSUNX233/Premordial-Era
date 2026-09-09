using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct ControllerInputs(
    double Light,
    double Temperature,
    double Pressure,
    double Energy,
    double Matter,
    double Hydration,
    double Contact,
    double ResourceGradient,
    double ResourceLateral,
    double ResourceTrend,
    double Novelty,
    double Danger,
    double ExplorationSignal)
{
    public bool AllFinite =>
        double.IsFinite(Light) && double.IsFinite(Temperature) &&
        double.IsFinite(Pressure) && double.IsFinite(Energy) &&
        double.IsFinite(Matter) && double.IsFinite(Hydration) &&
        double.IsFinite(Contact) && double.IsFinite(ResourceGradient) &&
        double.IsFinite(ResourceLateral) && double.IsFinite(ResourceTrend) &&
        double.IsFinite(Novelty) && double.IsFinite(Danger) &&
        double.IsFinite(ExplorationSignal);
}

public readonly record struct ControllerOutputs(
    double ContractionActivation,
    double PermeabilityGate,
    double SecretionActivation,
    double VerticalContraction,
    double LateralContraction)
{
    public static ControllerOutputs Basal => new(0.5, 0.5, 0.0, 0.0, 0.0);

    public bool AllFinite =>
        double.IsFinite(ContractionActivation) &&
        double.IsFinite(PermeabilityGate) &&
        double.IsFinite(SecretionActivation) &&
        double.IsFinite(VerticalContraction) &&
        double.IsFinite(LateralContraction);
}

public readonly record struct ControllerEvaluation(
    double[] State,
    ControllerOutputs Outputs);

public readonly record struct ForagingObservation(
    Vector2 Position,
    Vector2 AheadPosition,
    Vector2 LeftPosition,
    Vector2 RightPosition,
    double CenterCue,
    double AheadCue,
    double LeftCue,
    double RightCue,
    double CenterDanger,
    double AheadDanger,
    double LeftDanger,
    double RightDanger);

public readonly record struct ForagingDecision(
    double ResourceGradient,
    double ResourceLateral,
    double ResourceTrend,
    double Novelty,
    double Danger,
    double ExplorationSignal,
    double Activity,
    double Steering);

public sealed class ForagingMemory
{
    private Vector2 _recent0;
    private Vector2 _recent1;
    private Vector2 _recent2;
    private Vector2 _recent3;
    private int _recentCount;
    private int _recentCursor;
    private double _anchorElapsed;
    private Vector2 _lastPosition;
    private double _stalledSeconds;
    private double _reorientationCooldown;

    public bool Initialized { get; private set; }
    public double SmoothedCue { get; private set; }
    public double SmoothedTrend { get; private set; }
    public double ExplorationPhase { get; private set; }
    public Vector2 PreferredDirection { get; private set; }
    public double Steering { get; private set; }

    public bool AllFinite => double.IsFinite(SmoothedCue) &&
        double.IsFinite(SmoothedTrend) && double.IsFinite(ExplorationPhase) &&
        IsFinite(PreferredDirection) && double.IsFinite(Steering) &&
        IsFinite(_recent0) && IsFinite(_recent1) && IsFinite(_recent2) && IsFinite(_recent3);

    public void Initialize(Vector2 position,double cue,double phase)
    {
        Initialized=true;SmoothedCue=cue;SmoothedTrend=0;ExplorationPhase=phase;
        _recent0=position;_recent1=position;_recent2=position;_recent3=position;
        _recentCount=1;_recentCursor=1;_anchorElapsed=0;
        _lastPosition=position;_stalledSeconds=0;_reorientationCooldown=0;
    }

    public void UpdateCue(double cue,double deltaSeconds,double memorySeconds)
    {
        double previous=SmoothedCue;
        double blend=1.0-Math.Exp(-deltaSeconds/Math.Max(0.05,memorySeconds));
        SmoothedCue+=((cue-SmoothedCue)*blend);
        double instantaneous=Math.Clamp((cue-previous)/Math.Max(0.05,deltaSeconds),-1.0,1.0);
        SmoothedTrend+=((instantaneous-SmoothedTrend)*(1.0-Math.Exp(-deltaSeconds*1.4)));
    }

    public void AdvanceExploration(double deltaSeconds,double frequency)
    {
        ExplorationPhase=(ExplorationPhase+deltaSeconds*frequency)%Math.Tau;
        _anchorElapsed+=deltaSeconds;
        _reorientationCooldown=Math.Max(0,_reorientationCooldown-deltaSeconds);
    }

    public void Remember(Vector2 position,double interval,double minimumDistance)
    {
        Vector2 latest=Recent(_recentCursor==0?Math.Min(3,_recentCount-1):_recentCursor-1);
        if(_anchorElapsed<interval&&Vector2.DistanceSquared(position,latest)<minimumDistance*minimumDistance)return;
        switch(_recentCursor){case 0:_recent0=position;break;case 1:_recent1=position;break;case 2:_recent2=position;break;default:_recent3=position;break;}
        _recentCursor=(_recentCursor+1)%4;_recentCount=Math.Min(4,_recentCount+1);_anchorElapsed=0;
    }

    public double Novelty(Vector2 position,double radius)
    {
        if(_recentCount==0)return 1;
        float minimum=float.PositiveInfinity;
        for(int index=0;index<_recentCount;index++)minimum=Math.Min(minimum,Vector2.Distance(position,Recent(index)));
        return Math.Clamp(minimum/Math.Max(0.1,radius),0.0,1.0);
    }

    public double UpdateDirection(Vector2 position,Vector2 forward,double forwardEvidence,
        double lateralEvidence,double deltaSeconds)
    {
        if(PreferredDirection.LengthSquared()<0.5f)PreferredDirection=forward;
        double moved=Vector2.Distance(position,_lastPosition);
        _stalledSeconds=moved<0.002? _stalledSeconds+deltaSeconds:Math.Max(0,_stalledSeconds-deltaSeconds*0.7);
        _lastPosition=position;
        Vector2 right=new(-forward.Y,forward.X);
        // A better cue straight ahead confirms the current persistent course;
        // it must not continuously replace the world-space target with the
        // already-rotating body heading. Only lateral evidence bends the course.
        double lateralMagnitude=Math.Abs(lateralEvidence);
        if(lateralMagnitude>0.06)
        {
            Vector2 sensed=Vector2.Normalize(forward+right*(float)(2.2*lateralEvidence));
            float blend=(float)(1.0-Math.Exp(-deltaSeconds*(0.18+(0.55*Math.Min(1,lateralMagnitude)))));
            PreferredDirection=Vector2.Normalize(Vector2.Lerp(PreferredDirection,sensed,blend));
        }
        if(_reorientationCooldown<=0&&(_stalledSeconds>4.0||forwardEvidence<-0.20))
        {
            double sign=Math.Sin(ExplorationPhase)>=0?1.0:-1.0;
            double angle=sign*(0.55+(0.35*Math.Min(1,_stalledSeconds/8.0)));
            double c=Math.Cos(angle),s=Math.Sin(angle);
            PreferredDirection=Vector2.Normalize(new Vector2(
                (float)(forward.X*c-forward.Y*s),(float)(forward.X*s+forward.Y*c)));
            _reorientationCooldown=5.0+(2.0*Math.Abs(Math.Sin(ExplorationPhase*0.618)));
            _stalledSeconds=0;
        }
        double cross=(forward.X*PreferredDirection.Y)-(forward.Y*PreferredDirection.X);
        double dot=Math.Clamp(Vector2.Dot(forward,PreferredDirection),-1,1);
        double error=Math.Atan2(cross,dot);
        double correctedError=Math.Abs(error)<0.035?0.0:error-(Math.Sign(error)*0.035);
        double target=Math.Clamp(0.72*correctedError,-0.45,0.45);
        Steering+=((target-Steering)*(1.0-Math.Exp(-deltaSeconds*1.4)));
        return Steering;
    }

    private Vector2 Recent(int index)=>index switch{0=>_recent0,1=>_recent1,2=>_recent2,_=>_recent3};
    private static bool IsFinite(Vector2 value)=>float.IsFinite(value.X)&&float.IsFinite(value.Y);
}

/// <summary>
/// Small inherited recurrent signal network. Inputs are local signals and outputs
/// are low-level material actuators; no semantic goals are represented here.
/// </summary>
public static class BehaviorController
{
    public static ControllerEvaluation Evaluate(
        Genome genome,
        IReadOnlyList<double>? previousState,
        ControllerInputs inputs)
    {
        if (!inputs.AllFinite)
            throw new ArgumentOutOfRangeException(nameof(inputs));

        int count = genome.ControllerNodes.Count;
        if (count == 0)
            return new ControllerEvaluation([], ControllerOutputs.Basal);

        double[] oldState = previousState is not null && previousState.Count == count
            ? previousState.ToArray()
            : new double[count];
        double[] nextState = new double[count];
        double contraction = 0.0;
        double permeability = 0.0;
        double secretion = 0.0;
        double vertical = 0.0;
        double lateral = 0.0;

        for (int index = 0; index < count; index++)
        {
            ControllerNodeGene node = genome.ControllerNodes[index];
            double recurrent = node.RecurrentSourceIndex >= 0
                ? oldState[node.RecurrentSourceIndex] * node.RecurrentWeight
                : 0.0;
            double sum = node.Bias +
                (inputs.Light * node.LightWeight) +
                (inputs.Temperature * node.TemperatureWeight) +
                (inputs.Pressure * node.PressureWeight) +
                (inputs.Energy * node.EnergyWeight) +
                (inputs.Matter * node.MatterWeight) +
                (inputs.Hydration * node.HydrationWeight) +
                (inputs.Contact * node.ContactWeight) +
                (inputs.ResourceGradient * ((0.6 * node.MatterWeight) + (0.4 * node.LightWeight))) +
                (inputs.ResourceTrend * node.SelfMemoryWeight) +
                (inputs.Novelty * 0.35 * Math.Abs(node.RecurrentWeight)) -
                (inputs.Danger * 0.45 * Math.Abs(node.PressureWeight)) +
                (inputs.ExplorationSignal * 0.25 * node.LateralContractionOutputWeight) +
                (oldState[index] * node.SelfMemoryWeight) + recurrent;
            double state = Math.Tanh(sum);
            nextState[index] = state;
            contraction += state * node.ContractionOutputWeight;
            permeability += state * node.PermeabilityOutputWeight;
            secretion += state * node.SecretionOutputWeight;
            vertical += state * node.VerticalContractionOutputWeight;
            lateral += state * node.LateralContractionOutputWeight;
        }

        double scale = 1.0 / Math.Sqrt(count);
        ControllerOutputs outputs = new(
            UnitSigmoid(contraction * scale),
            UnitSigmoid(permeability * scale),
            Math.Max(0.0, Math.Tanh(secretion * scale)),
            Math.Tanh(vertical * scale),
            Math.Tanh((lateral * scale * 0.18) + (inputs.ResourceLateral * 0.35) +
                (inputs.ExplorationSignal * 0.70)));
        return new ControllerEvaluation(nextState, outputs);
    }

    public static ForagingDecision UpdateForaging(ForagingMemory memory,ForagingObservation observation,
        double energyFraction,double signalConductivity,SimulationConfig config,double deltaSeconds)
    {
        if(!memory.Initialized)
            memory.Initialize(observation.Position,observation.CenterCue,
                (observation.Position.X*0.754877666)+(observation.Position.Y*0.569840296));
        memory.UpdateCue(observation.CenterCue,deltaSeconds,config.ForagingCueMemorySeconds);
        memory.AdvanceExploration(deltaSeconds,0.55+(0.65*signalConductivity));
        double novelty=memory.Novelty(observation.Position,config.ExplorationMemoryRadius);
        double aheadNovelty=memory.Novelty(observation.AheadPosition,config.ExplorationMemoryRadius);
        double leftNovelty=memory.Novelty(observation.LeftPosition,config.ExplorationMemoryRadius);
        double rightNovelty=memory.Novelty(observation.RightPosition,config.ExplorationMemoryRadius);
        double forward=(observation.AheadCue-observation.CenterCue)+
            config.CuriosityStrength*(aheadNovelty-novelty)-
            (observation.AheadDanger-observation.CenterDanger);
        double lateral=(observation.RightCue-observation.LeftCue)+
            config.CuriosityStrength*(rightNovelty-leftNovelty)+
            (observation.LeftDanger-observation.RightDanger);
        Vector2 forwardDirection=observation.AheadPosition-observation.Position;
        forwardDirection=forwardDirection.LengthSquared()>1e-8f?Vector2.Normalize(forwardDirection):
            (memory.PreferredDirection.LengthSquared()>0.5f?memory.PreferredDirection:Vector2.UnitX);
        double steering=memory.UpdateDirection(observation.Position,forwardDirection,forward,lateral,deltaSeconds);
        double exploration=steering;
        double energyGate=Math.Clamp((energyFraction-config.ForagingRestEnergyFraction)/
            Math.Max(0.05,1.0-config.ForagingRestEnergyFraction),0.0,1.0);
        double activity=energyGate*Math.Clamp(config.ExplorationActivityFloor+
            (0.22*novelty)+(0.18*Math.Abs(forward))+(0.12*Math.Max(0.0,-memory.SmoothedTrend))+
            (0.10*observation.CenterDanger),0.0,1.0);
        memory.Remember(observation.Position,config.ExplorationMemorySeconds,
            config.ExplorationMemoryRadius*0.35);
        return new(Math.Clamp(forward,-1,1),Math.Clamp(lateral,-1,1),memory.SmoothedTrend,
            novelty,Math.Clamp(observation.CenterDanger,0,1),exploration,activity,steering);
    }

    private static double UnitSigmoid(double value) => 0.5 + (0.5 * Math.Tanh(value));
}
