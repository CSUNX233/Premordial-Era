using System.Numerics;

namespace NativeEpoch.Simulation;

/// <summary>
/// Continuous local expression used by the cavity solver.  CavityFraction and
/// CavityAperture are inherited morphology inputs; the remaining values are
/// shared tissue-expression outputs.  A cavity has no named organ or boolean
/// capability flag.
/// </summary>
public readonly record struct CavityExpression(
    double CavityFraction,
    double CavityAperture,
    double ExchangeExpression,
    double BarrierExpression,
    double ContractileExpression,
    double StructuralExpression)
{
    public bool AllFinite =>
        double.IsFinite(CavityFraction) && double.IsFinite(CavityAperture) &&
        double.IsFinite(ExchangeExpression) && double.IsFinite(BarrierExpression) &&
        double.IsFinite(ContractileExpression) && double.IsFinite(StructuralExpression);

    public CavityExpression Clamped() => new(
        Math.Clamp(CavityFraction, 0.0, 1.0),
        Math.Clamp(CavityAperture, 0.0, 1.0),
        Math.Clamp(ExchangeExpression, 0.0, 1.0),
        Math.Clamp(BarrierExpression, 0.0, 1.0),
        Math.Clamp(ContractileExpression, 0.0, 1.0),
        Math.Clamp(StructuralExpression, 0.0, 1.0));
}

/// <summary>
/// World-space medium contact at the genetically expressed opening.  The
/// caller derives this from one or a few bounded surface samples.  Exposure is
/// zero when that local surface is occluded, even if another part of the body
/// is in air.
/// </summary>
public readonly record struct CavityApertureContact(
    Vector2 Position,
    float Depth,
    bool ExternallyConnected,
    double AirExposure,
    double WaterExposure)
{
    public bool AllFinite =>
        float.IsFinite(Position.X) && float.IsFinite(Position.Y) &&
        float.IsFinite(Depth) && double.IsFinite(AirExposure) &&
        double.IsFinite(WaterExposure) && AirExposure is >= 0.0 and <= 1.0 &&
        WaterExposure is >= 0.0 and <= 1.0 && AirExposure + WaterExposure <= 1.0000001;
}

public readonly record struct CavityRegionInput(
    int RegionId,
    double RegionAnalyticVolume,
    double TissueOxygenCapacity,
    bool TransportConnected,
    double ReleaseImmersion,
    CavityExpression Expression,
    CavityApertureContact Aperture)
{
    public bool AllFinite => RegionId >= 0 &&
        double.IsFinite(RegionAnalyticVolume) && RegionAnalyticVolume > 0.0 &&
        double.IsFinite(TissueOxygenCapacity) && TissueOxygenCapacity >= 0.0 &&
        double.IsFinite(ReleaseImmersion) && ReleaseImmersion is >= 0.0 and <= 1.0 &&
        Expression.AllFinite && Aperture.AllFinite;
}

/// <summary>
/// Constants are deliberately local to the bounded cavity approximation.  The
/// model tracks one well-mixed gas pocket per region, not voxels or fluid cells.
/// </summary>
public readonly record struct CavityPhysiologyParameters(
    double MaximumEnvelopeFraction,
    double OxygenCapacityPerVolume,
    double PassiveVentilationPerAreaPerSecond,
    double ActiveVentilationPerAreaPerSecond,
    double TissueExchangePerAreaPerSecond,
    double FloodingPerSecond,
    double AirDrainagePerSecond,
    double WallMaintenanceEnergyPerAreaPerSecond,
    double VentilationEnergyPerVolumePerSecond)
{
    public static CavityPhysiologyParameters Default => new(
        MaximumEnvelopeFraction: 0.42,
        OxygenCapacityPerVolume: 0.90,
        PassiveVentilationPerAreaPerSecond: 0.055,
        ActiveVentilationPerAreaPerSecond: 0.70,
        TissueExchangePerAreaPerSecond: 0.42,
        FloodingPerSecond: 1.8,
        AirDrainagePerSecond: 1.3,
        WallMaintenanceEnergyPerAreaPerSecond: 0.006,
        VentilationEnergyPerVolumePerSecond: 0.065);

    public bool AllFinitePositive =>
        IsNonNegative(MaximumEnvelopeFraction) && MaximumEnvelopeFraction <= 1.0 &&
        IsNonNegative(OxygenCapacityPerVolume) &&
        IsNonNegative(PassiveVentilationPerAreaPerSecond) &&
        IsNonNegative(ActiveVentilationPerAreaPerSecond) &&
        IsNonNegative(TissueExchangePerAreaPerSecond) &&
        IsNonNegative(FloodingPerSecond) && IsNonNegative(AirDrainagePerSecond) &&
        IsNonNegative(WallMaintenanceEnergyPerAreaPerSecond) &&
        IsNonNegative(VentilationEnergyPerVolumePerSecond);

    private static bool IsNonNegative(double value) => double.IsFinite(value) && value >= 0.0;
}

/// <summary>
/// Persistent region-local inventory and the latest observable rates.  Oxygen
/// belongs to the global oxygen ledger and must be included in snapshots.
/// </summary>
public readonly record struct CavityRegionState(
    int RegionId,
    double Oxygen,
    double FloodedFraction,
    double EffectiveVolume,
    double OxygenCapacity,
    double WallMatterCommitted,
    double VentilationLastStep,
    double TissueOxygenLastStep,
    double EnergySpentLastStep,
    double MaintenanceShortfallLastStep,
    double LeakageLastStep,
    double OpeningLastStep,
    double AirExposureLastStep,
    double WaterExposureLastStep)
{
    public bool AllFinite => RegionId >= 0 &&
        double.IsFinite(Oxygen) && double.IsFinite(FloodedFraction) &&
        double.IsFinite(EffectiveVolume) && double.IsFinite(OxygenCapacity) &&
        double.IsFinite(WallMatterCommitted) && double.IsFinite(VentilationLastStep) &&
        double.IsFinite(TissueOxygenLastStep) && double.IsFinite(EnergySpentLastStep) &&
        double.IsFinite(MaintenanceShortfallLastStep) &&
        double.IsFinite(LeakageLastStep) && double.IsFinite(OpeningLastStep) &&
        double.IsFinite(AirExposureLastStep) && double.IsFinite(WaterExposureLastStep) &&
        Oxygen >= 0.0 &&
        FloodedFraction is >= 0.0 and <= 1.0 && EffectiveVolume >= 0.0 &&
        OxygenCapacity >= 0.0 && Oxygen <= OxygenCapacity + 1e-9 &&
        WallMatterCommitted >= 0.0 && EnergySpentLastStep >= 0.0 &&
        MaintenanceShortfallLastStep >= 0.0 && LeakageLastStep >= 0.0 &&
        OpeningLastStep is >= 0.0 and <= 1.0;
}

public sealed class CavitySystemState
{
    private readonly List<CavityRegionState> _regions = [];

    public CavitySystemState()
    {
    }

    public CavitySystemState(IEnumerable<CavityRegionState> regions)
    {
        _regions.AddRange(regions.OrderBy(region => region.RegionId));
        if (_regions.Count > GenomeValidator.MaximumRegions ||
            !_regions.All(region => region.AllFinite) ||
            _regions.Select(region => region.RegionId).Distinct().Count() != _regions.Count)
        {
            throw new ArgumentException("Cavity state must be finite, unique, and within the regional cap.", nameof(regions));
        }
    }

    public IReadOnlyList<CavityRegionState> Regions => _regions;
    public double TotalOxygen
    {
        get
        {
            double total = 0.0;
            for (int index = 0; index < _regions.Count; index++)
                total += _regions[index].Oxygen;
            return total;
        }
    }
    public bool AllFinite
    {
        get
        {
            if (_regions.Count > GenomeValidator.MaximumRegions) return false;
            for (int index = 0; index < _regions.Count; index++)
                if (!_regions[index].AllFinite) return false;
            return true;
        }
    }

    public CavityRegionState GetRegion(int regionId)
    {
        int index = IndexOf(regionId);
        return index >= 0 ? _regions[index] : Empty(regionId);
    }

    internal CavityRegionState GetOrAdd(int regionId)
    {
        int index = IndexOf(regionId);
        if (index >= 0) return _regions[index];
        if (_regions.Count >= GenomeValidator.MaximumRegions)
            throw new InvalidOperationException("Cavity state exceeded the fixed regional cap.");
        CavityRegionState state = Empty(regionId);
        InsertSorted(state);
        return state;
    }

    internal void Set(CavityRegionState state)
    {
        int index = IndexOf(state.RegionId);
        if (index >= 0) _regions[index] = state;
        else
        {
            if (_regions.Count >= GenomeValidator.MaximumRegions)
                throw new InvalidOperationException("Cavity state exceeded the fixed regional cap.");
            InsertSorted(state);
        }
    }

    private int IndexOf(int regionId)
    {
        for (int index = 0; index < _regions.Count; index++)
            if (_regions[index].RegionId == regionId) return index;
        return -1;
    }

    private void InsertSorted(CavityRegionState state)
    {
        int index = 0;
        while (index < _regions.Count && _regions[index].RegionId < state.RegionId) index++;
        _regions.Insert(index, state);
    }

    private static CavityRegionState Empty(int regionId) => new(
        regionId, 0.0, 0.0, 0.0, 0.0, 0.0,
        0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
}

public readonly record struct CavityStepResult(
    double OxygenTakenFromEnvironment,
    double OxygenReturnedToEnvironment,
    double OxygenDeliveredToTissue,
    double OxygenTakenFromTissue,
    double EnergySpent,
    double MaintenanceShortfall,
    double ConservationResidual)
{
    public static CavityStepResult operator +(CavityStepResult left, CavityStepResult right) => new(
        left.OxygenTakenFromEnvironment + right.OxygenTakenFromEnvironment,
        left.OxygenReturnedToEnvironment + right.OxygenReturnedToEnvironment,
        left.OxygenDeliveredToTissue + right.OxygenDeliveredToTissue,
        left.OxygenTakenFromTissue + right.OxygenTakenFromTissue,
        left.EnergySpent + right.EnergySpent,
        left.MaintenanceShortfall + right.MaintenanceShortfall,
        left.ConservationResidual + right.ConservationResidual);
}

public static class CavityPhysiology
{
    /// <summary>
    /// Formal world integration entry. Inputs are capped by the same fixed
    /// region limit as the genome and require no per-region fluid grid.
    /// </summary>
    public static CavityStepResult StepBody(
        CavitySystemState state,
        DevelopingBody body,
        IMutableEnvironmentField environment,
        ReadOnlySpan<CavityRegionInput> inputs,
        double deltaSeconds,
        CavityPhysiologyParameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(environment);
        if (inputs.Length > GenomeValidator.MaximumRegions)
            throw new ArgumentOutOfRangeException(nameof(inputs));
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0.0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        CavityPhysiologyParameters p = parameters ?? CavityPhysiologyParameters.Default;
        if (!p.AllFinitePositive) throw new ArgumentOutOfRangeException(nameof(parameters));
        CavityStepResult total = default;
        for (int index = 0; index < inputs.Length; index++)
        {
            if (!inputs[index].AllFinite)
                throw new ArgumentOutOfRangeException(nameof(inputs));
            for (int previous = 0; previous < index; previous++)
                if (inputs[previous].RegionId == inputs[index].RegionId)
                    throw new ArgumentException("A cavity region may be stepped only once per body update.", nameof(inputs));
            total += StepRegionCore(state, body, environment, inputs[index], deltaSeconds, p);
        }
        return total;
    }

    public static CavityStepResult StepRegion(
        CavitySystemState state,
        DevelopingBody body,
        IMutableEnvironmentField environment,
        CavityRegionInput input,
        double deltaSeconds,
        CavityPhysiologyParameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(environment);
        if (!input.AllFinite) throw new ArgumentOutOfRangeException(nameof(input));
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0.0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        CavityPhysiologyParameters p = parameters ?? CavityPhysiologyParameters.Default;
        if (!p.AllFinitePositive) throw new ArgumentOutOfRangeException(nameof(parameters));

        return StepRegionCore(state, body, environment, input, deltaSeconds, p);
    }

    private static CavityStepResult StepRegionCore(
        CavitySystemState state,
        DevelopingBody body,
        IMutableEnvironmentField environment,
        CavityRegionInput input,
        double deltaSeconds,
        CavityPhysiologyParameters p)
    {

        BodyRegion region = body.GetRegion(input.RegionId);
        CavityRegionState previous = state.GetOrAdd(input.RegionId);
        CavityExpression expression = input.Expression.Clamped();
        CavityApertureContact aperture = input.Aperture;
        double airExposure = aperture.ExternallyConnected && aperture.Depth <= 1e-5f
            ? aperture.AirExposure : 0.0;
        double waterExposure = aperture.ExternallyConnected ? aperture.WaterExposure : 0.0;

        // CavityFraction is a material allocation, not free extra volume.  The
        // well-mixed pocket is bounded inside the already paid regional envelope.
        double development = Math.Clamp(region.Development, 0.0, 1.0);
        double allocation = expression.CavityFraction * development;
        double wallMatter = region.Matter * allocation *
            (0.08 + (0.12 * expression.StructuralExpression) +
             (0.08 * expression.BarrierExpression));
        double supportedFraction = allocation * allocation *
            (0.15 + (0.85 * expression.StructuralExpression));
        double envelopeVolume = input.RegionAnalyticVolume * p.MaximumEnvelopeFraction;
        double nominalVolume = envelopeVolume * supportedFraction;
        double wallArea = nominalVolume > 0.0
            ? Math.Cbrt(36.0 * Math.PI * nominalVolume * nominalVolume)
            : 0.0;

        double wallMaintenanceRequested = wallArea * p.WallMaintenanceEnergyPerAreaPerSecond *
            (0.35 + (0.40 * expression.BarrierExpression) +
             (0.25 * expression.StructuralExpression)) * deltaSeconds;
        double exchangeMaintenanceRequested = wallArea * expression.ExchangeExpression *
            region.TransportAvailability * p.WallMaintenanceEnergyPerAreaPerSecond *
            0.65 * deltaSeconds;
        double maintenanceRequested = wallMaintenanceRequested + exchangeMaintenanceRequested;
        double activeDrive = expression.ContractileExpression * Math.Clamp(region.Activation, 0.0, 1.0);
        double ventilationRequested = nominalVolume * expression.CavityAperture * airExposure *
            activeDrive * p.VentilationEnergyPerVolumePerSecond * deltaSeconds;
        double requestedEnergy = maintenanceRequested + ventilationRequested;
        double energyPaid = body.ConsumeRegionEnergy(input.RegionId, requestedEnergy);
        double maintenancePaid = Math.Min(maintenanceRequested, energyPaid);
        double remainingEnergy = Math.Max(0.0, energyPaid - maintenancePaid);
        double maintenanceRatio = maintenanceRequested > 1e-12
            ? maintenancePaid / maintenanceRequested : 1.0;
        double ventilationRatio = ventilationRequested > 1e-12
            ? Math.Min(1.0, remainingEnergy / ventilationRequested) : 1.0;
        double maintainedBarrier = expression.BarrierExpression *
            (0.20 + (0.80 * maintenanceRatio)) *
            (0.30 + (0.70 * expression.StructuralExpression)) *
            (1.0 - (0.85 * Math.Clamp(region.Damage, 0.0, 1.0)));

        double apertureScale = expression.CavityAperture;
        double ingressRate = waterExposure * apertureScale *
            (0.08 + (0.92 * (1.0 - maintainedBarrier))) * p.FloodingPerSecond;
        double activeVentilation = activeDrive * ventilationRatio;
        double drainageRate = airExposure * apertureScale *
            (0.20 + activeVentilation) * p.AirDrainagePerSecond;
        double flooding = ApproachFraction(previous.FloodedFraction, ingressRate, drainageRate, deltaSeconds);
        double effectiveVolume = nominalVolume * (1.0 - flooding);
        double capacity = effectiveVolume * p.OxygenCapacityPerVolume;

        double cavityOxygen = Math.Max(0.0, previous.Oxygen);
        double tissueBefore = region.Oxygen;
        double tissueOxygen = tissueBefore;
        double environmentTaken = 0.0;
        double environmentReturned = 0.0;
        double airTaken = 0.0;
        double airReturned = 0.0;
        double leakedOrDisplaced = 0.0;
        double delivered = 0.0;
        double takenFromTissue = 0.0;

        // Compression, flooding, shrinkage, or developmental regression may
        // reduce capacity. Excess is transported to tissue first, then returned
        // to the local environment; it never vanishes.
        if (cavityOxygen > capacity)
        {
            double excess = cavityOxygen - capacity;
            if (input.TransportConnected)
            {
                double room = Math.Max(0.0, input.TissueOxygenCapacity - tissueOxygen);
                double moved = Math.Min(excess, room);
                if (moved > 0.0)
                {
                    tissueOxygen += moved;
                    cavityOxygen -= moved;
                    excess -= moved;
                    delivered += moved;
                }
            }
            if (excess > 0.0)
            {
                environment.DepositOxygen(aperture.Position, aperture.Depth,
                    input.ReleaseImmersion, excess);
                cavityOxygen -= excess;
                environmentReturned += excess;
                leakedOrDisplaced += excess;
            }
        }

        // Only an externally connected opening that is locally in air can
        // refresh the pocket. Whole-body immersion is intentionally irrelevant.
        if (capacity > 0.0 && airExposure > 0.0 && apertureScale > 0.0)
        {
            double outsidePotential = Math.Max(0.0,
                environment.Sample(aperture.Position, aperture.Depth).AirOxygenAvailability);
            double cavityConcentration = cavityOxygen / capacity;
            double apertureArea = wallArea * apertureScale * airExposure;
            double ventilationConductance = apertureArea *
                (p.PassiveVentilationPerAreaPerSecond +
                 (p.ActiveVentilationPerAreaPerSecond * activeVentilation)) * deltaSeconds;
            double flux = ventilationConductance * (outsidePotential - cavityConcentration);
            if (flux > 0.0)
            {
                double received = environment.WithdrawOxygen(aperture.Position, aperture.Depth,
                    0.0, Math.Min(capacity - cavityOxygen, flux));
                cavityOxygen += received;
                environmentTaken += received;
                airTaken += received;
            }
            else if (flux < 0.0)
            {
                double released = Math.Min(cavityOxygen, -flux);
                cavityOxygen -= released;
                environment.DepositOxygen(aperture.Position, aperture.Depth, 0.0, released);
                environmentReturned += released;
                airReturned += released;
            }
        }

        // An open, flooded aperture leaks gas into water. A sealed submerged
        // pocket has no environmental source or sink and can only be depleted
        // by connected tissue.
        if (cavityOxygen > 0.0 && waterExposure > 0.0 && apertureScale > 0.0)
        {
            double leakFraction = 1.0 - Math.Exp(-waterExposure * apertureScale *
                (0.05 + (0.95 * (1.0 - maintainedBarrier))) * deltaSeconds);
            double leaked = cavityOxygen * leakFraction;
            cavityOxygen -= leaked;
            environment.DepositOxygen(aperture.Position, aperture.Depth, 1.0, leaked);
            environmentReturned += leaked;
            leakedOrDisplaced += leaked;
        }

        if (capacity > 0.0 && input.TransportConnected &&
            region.TransportAvailability > 0.0 && expression.ExchangeExpression > 0.0)
        {
            double tissueConcentration = input.TissueOxygenCapacity > 0.0
                ? tissueOxygen / input.TissueOxygenCapacity : 0.0;
            double cavityConcentration = cavityOxygen / capacity;
            double conductance = wallArea * expression.ExchangeExpression *
                region.TransportAvailability * (0.20 + (0.80 * maintenanceRatio)) *
                p.TissueExchangePerAreaPerSecond * deltaSeconds;
            double flux = conductance * (cavityConcentration - tissueConcentration);
            if (flux > 0.0)
            {
                double moved = Math.Min(cavityOxygen,
                    Math.Min(Math.Max(0.0, input.TissueOxygenCapacity - tissueOxygen), flux));
                if (moved > 0.0)
                {
                    cavityOxygen -= moved;
                    tissueOxygen += moved;
                    delivered += moved;
                }
            }
            else if (flux < 0.0)
            {
                double moved = Math.Min(tissueOxygen, Math.Min(capacity - cavityOxygen, -flux));
                if (moved > 0.0)
                {
                    cavityOxygen += moved;
                    tissueOxygen -= moved;
                    takenFromTissue += moved;
                }
            }
        }

        cavityOxygen = Math.Clamp(cavityOxygen, 0.0, capacity);
        double tissueAfter = Math.Max(0.0, tissueOxygen);
        double tissueDelta = tissueAfter - tissueBefore;
        if (Math.Abs(tissueDelta) > 0.0)
            body.ApplyInventoryDelta(input.RegionId,
                new RegionalInventoryDelta(0.0, tissueDelta, 0.0, 0.0));
        double conservationResidual = (cavityOxygen - previous.Oxygen) +
            (tissueAfter - tissueBefore) + environmentReturned - environmentTaken;
        double ventilationNet = airTaken - airReturned;
        state.Set(new CavityRegionState(input.RegionId, cavityOxygen, flooding,
            effectiveVolume, capacity, wallMatter, ventilationNet,
            delivered - takenFromTissue, energyPaid,
            Math.Max(0.0, maintenanceRequested - maintenancePaid),
            leakedOrDisplaced, apertureScale * (airExposure + waterExposure),
            airExposure, waterExposure));

        return new CavityStepResult(environmentTaken, environmentReturned, delivered,
            takenFromTissue, energyPaid, Math.Max(0.0, maintenanceRequested - maintenancePaid),
            conservationResidual);
    }

    /// <summary>
    /// Death/removal hook. The caller supplies the body's current location and
    /// medium so every remaining cavity molecule is returned to the world ledger.
    /// </summary>
    public static double ReleaseAll(
        CavitySystemState state,
        IMutableEnvironmentField environment,
        Vector2 position,
        float depth,
        double immersion)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(environment);
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
            !float.IsFinite(depth) || !double.IsFinite(immersion) || immersion is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(immersion));
        }
        double released = 0.0;
        for (int index = 0; index < state.Regions.Count; index++)
        {
            CavityRegionState region = state.Regions[index];
            if (region.Oxygen > 0.0)
            {
                environment.DepositOxygen(position, depth, immersion, region.Oxygen);
                released += region.Oxygen;
            }
            state.Set(region with
            {
                Oxygen = 0.0,
                VentilationLastStep = -region.Oxygen,
                TissueOxygenLastStep = 0.0,
                    EnergySpentLastStep = 0.0,
                    MaintenanceShortfallLastStep = 0.0,
                    LeakageLastStep = 0.0
                });
        }
        return released;
    }

    private static double ApproachFraction(double current, double fillingRate,
        double drainingRate, double deltaSeconds)
    {
        double total = fillingRate + drainingRate;
        if (total <= 0.0 || deltaSeconds <= 0.0) return Math.Clamp(current, 0.0, 1.0);
        double equilibrium = fillingRate / total;
        return Math.Clamp(equilibrium +
            ((Math.Clamp(current, 0.0, 1.0) - equilibrium) * Math.Exp(-total * deltaSeconds)),
            0.0, 1.0);
    }
}
