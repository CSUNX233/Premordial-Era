namespace NativeEpoch.Simulation;

public readonly record struct ControllerInputs(
    double Light,
    double Temperature,
    double Pressure,
    double Energy,
    double Matter,
    double Hydration,
    double Contact)
{
    public bool AllFinite =>
        double.IsFinite(Light) && double.IsFinite(Temperature) &&
        double.IsFinite(Pressure) && double.IsFinite(Energy) &&
        double.IsFinite(Matter) && double.IsFinite(Hydration) &&
        double.IsFinite(Contact);
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
            Math.Tanh(lateral * scale));
        return new ControllerEvaluation(nextState, outputs);
    }

    private static double UnitSigmoid(double value) => 0.5 + (0.5 * Math.Tanh(value));
}
