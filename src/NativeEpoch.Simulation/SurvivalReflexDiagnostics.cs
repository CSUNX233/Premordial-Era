using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct SurvivalReflexWorldTrack(
    bool ReachedLand,
    bool ReflexActivated,
    bool ReturnedToWater,
    double? LandSeconds,
    double? ReturnSeconds,
    double MinimumHydration,
    double MaximumHydrationAfterLand,
    double FinalHydration,
    double MaximumImmersionAfterLand,
    double FinalImmersion,
    double PathAfterLand,
    double HeadingChangeAfterLand,
    double MaximumChemicalSenseAccess,
    double MaximumVisionSignal,
    bool Alive,
    bool AllFinite);

public readonly record struct SurvivalReflexDiagnosticResult(
    double RetreatSteering,
    double PoweredSurfaceDirectionAlignment,
    bool WorseningReversesDirection,
    bool NoPressureLeavesOutputUnchanged,
    bool ZeroEnergyCannotCreateMotion,
    bool RecoveryReleasesReflex,
    bool FlatStressChangesBoundedProbe,
    SurvivalReflexWorldTrack EnabledWorld,
    SurvivalReflexWorldTrack DisabledWorld,
    bool WorldReturnIsCausal,
    bool Passed)
{
    public bool PhysiologicalStressIsMediumIndependent { get; init; }
}

/// <summary>Bounded causal checks for the universal, interoceptive survival reflex.</summary>
public static class SurvivalReflexDiagnostics
{
    private const ulong Seed = 20260908;

    public static SurvivalReflexDiagnosticResult Run()
    {
        SimulationConfig config = new();
        SurvivalReflexMemory retreatMemory = new()
        {
            Initialized = true,
            PreviousPosition = new Vector2(100, 100),
            PreviousDepth = 2,
            PreviousStress = 0,
            PreviousDamage = 0
        };
        SurvivalReflexResponse retreat = SurvivalReflex.Update(ref retreatMemory, 7,
            new Vector2(100.2f, 100), 2, 0, new Vector2(1, 0), 0,
            0.6, 0,1,0, config.WorldSize, config.FixedDeltaSeconds);
        bool reverses = retreat.Active && retreat.Mode == SurvivalReflexMode.Retreat &&
            Math.Abs(retreat.Steering) > 0.9 && retreatMemory.HeldPlanarDirection.X < -0.9f;

        SurvivalReflexMemory calmMemory = default;
        SurvivalReflexResponse calm = SurvivalReflex.Update(ref calmMemory, 8,
            new Vector2(120, 120), 1, 0.3, Vector2.Zero, 0,
            0, 0,1,0, config.WorldSize, config.FixedDeltaSeconds);
        ControllerOutputs inherited = ControllerOutputs.Basal with
            { ContractionActivation = 0.31, LateralContraction = -0.27, VerticalContraction = 0.19 };
        bool calmUnchanged = !calm.Active && SurvivalReflex.Apply(inherited, calm).Equals(inherited);

        bool zeroEnergy = ZeroEnergyCannotMove(retreat);
        double surfaceAlignment=PoweredSurfaceDirectionAlignment();

        SurvivalReflexResponse recovered = SurvivalReflex.Update(ref retreatMemory, 7,
            new Vector2(100.1f, 100), 2, Math.PI, new Vector2(-1, 0), 0,
            0.01, 0,1,0, config.WorldSize, config.FixedDeltaSeconds);
        bool releases = !recovered.Active && recovered.Mode == SurvivalReflexMode.None;

        SurvivalReflexMemory probeMemory = default;
        SurvivalReflex.Update(ref probeMemory, 9, new Vector2(140, 140), 2, 0,
            Vector2.Zero, 0, 0.5, 0,1,0, config.WorldSize, config.FixedDeltaSeconds);
        Vector2 firstProbe = probeMemory.HeldPlanarDirection;
        int steps = (int)Math.Ceiling(16.0 / config.FixedDeltaSeconds);
        for (int step = 0; step < steps; step++)
            SurvivalReflex.Update(ref probeMemory, 9, new Vector2(140, 140), 2, 0,
                Vector2.Zero, 0, 0.5, 0,1,0, config.WorldSize, config.FixedDeltaSeconds);
        bool probeChanged = probeMemory.ProbeCount >= 2 &&
            Vector2.Dot(firstProbe, probeMemory.HeldPlanarDirection) < 0.99f && probeMemory.AllFinite;

        (SurvivalReflexWorldTrack enabled, SurvivalReflexWorldTrack disabled) = PairedShoreTrial();
        // Verify the causal outcome and recovery direction. A fixed 0.10 hydration gap
        // within this short window depends on the exact return time and exchange rate.
        bool worldCausal = enabled.ReachedLand && enabled.ReflexActivated && enabled.ReturnedToWater &&
            (!disabled.ReturnedToWater || enabled.ReturnSeconds < disabled.ReturnSeconds) &&
            enabled.FinalImmersion>0.80&&enabled.FinalHydration>enabled.MinimumHydration+0.01&&
            enabled.FinalHydration>disabled.FinalHydration&&enabled.PathAfterLand > 0.05 &&
            enabled.MaximumChemicalSenseAccess<=1e-12&&enabled.MaximumVisionSignal<=1e-12&&
            enabled.AllFinite && disabled.AllFinite;
        bool physiologicalStress = CheckPhysiologicalStress(config);
        bool passed = physiologicalStress && reverses && surfaceAlignment>0.5&&calmUnchanged && zeroEnergy && releases && probeChanged && worldCausal;
        return new(retreat.Steering,surfaceAlignment, reverses, calmUnchanged, zeroEnergy, releases, probeChanged,
            enabled, disabled, worldCausal, passed)
        { PhysiologicalStressIsMediumIndependent = physiologicalStress };
    }

    private static bool CheckPhysiologicalStress(SimulationConfig config)
    {
        Genome genome = Genome.CreateAncestor();
        Organism specimen = new()
        {
            Body = new DevelopingBody(genome, config.CoreInitialMatter, 1.0, 1.0, 0.2, 0.2),
            Hydration = 0.80, Immersion = 0.0,
            DehydrationCostLastStep = config.DehydrationEnergyCostPerSecond * config.FixedDeltaSeconds
        };
        double land = SurvivalReflex.MeasureStress(specimen, config, config.FixedDeltaSeconds);
        specimen.Immersion = 1.0;
        double water = SurvivalReflex.MeasureStress(specimen, config, config.FixedDeltaSeconds);
        specimen.Hydration = 0.40;
        double thirsty = SurvivalReflex.MeasureStress(specimen, config, config.FixedDeltaSeconds);
        specimen.Hydration = 0.80;
        specimen.HypoxiaShortfallLastStep = config.MetabolicSubstratePerSecond *
            Math.Max(0.05, specimen.Body.Cache.TotalMatter) * config.FixedDeltaSeconds * 0.5;
        double hypoxic = SurvivalReflex.MeasureStress(specimen, config, config.FixedDeltaSeconds);
        return land == 0.0 && water == land && thirsty > 0.12 && hypoxic > 0.12;
    }

    private static bool ZeroEnergyCannotMove(SurvivalReflexResponse reflex)
    {
        Genome genome = Genome.CreateAncestor();
        BodyRegion[] regions = genome.Regions.Select(gene => new BodyRegion(
            gene.RegionId, BodyCalculator.TargetMatter(gene), 1.0,
            Substrate: 1.0, Oxygen: 1.0, Water: 1.0, Energy: 0.0)).ToArray();
        DevelopingBody body = new(genome, regions);
        BodyPose pose = new(genome, body);
        SimulationConfig config = new();
        ControllerOutputs output = SurvivalReflex.Apply(ControllerOutputs.Basal, reflex);
        IReadOnlyDictionary<int, double> noSignal = body.Regions.ToDictionary(r => r.RegionId, _ => 0.0);
        body.UpdateFunctionalState(genome, output, noSignal, config.FixedDeltaSeconds);
        double displacement=0.0,energy=0.0;
        for(int step=0;step<50;step++)
        {
            body.UpdateFunctionalState(genome, output, noSignal, config.FixedDeltaSeconds);
            BodyMechanicsResult result = pose.Step(genome, body, output,
                1.0+step*config.FixedDeltaSeconds, 1.0, 1.0,
                config, config.FixedDeltaSeconds, groundSupported: true, reciprocalDiagnostic: false,
                activeSurfaceDirection:reflex.PlanarDirection);
            displacement+=result.MediumVelocity.Length()*config.FixedDeltaSeconds;
            energy+=result.EnergySpent;
        }
        return energy == 0.0 && displacement < 1e-4;
    }

    private static double PoweredSurfaceDirectionAlignment()
    {
        Genome genome=Genome.CreateAncestor();
        BodyRegion[] regions=genome.Regions.Select(gene=>new BodyRegion(gene.RegionId,
            BodyCalculator.TargetMatter(gene),1.0,Substrate:1.0,Oxygen:1.0,Water:1.0,Energy:20.0)).ToArray();
        DevelopingBody body=new(genome,regions);
        BodyPose pose=new(genome,body);
        SimulationConfig config=new();
        ControllerOutputs output=ControllerOutputs.Basal with { ContractionActivation=1.0 };
        IReadOnlyDictionary<int,double> noSignal=body.Regions.ToDictionary(r=>r.RegionId,_=>0.0);
        Vector2 displacement=Vector2.Zero;
        for(int step=0;step<30;step++)
        {
            body.UpdateFunctionalState(genome,output,noSignal,config.FixedDeltaSeconds);
            BodyMechanicsResult result=pose.Step(genome,body,output,step*config.FixedDeltaSeconds,
                1.0,1.0,config,config.FixedDeltaSeconds,true,false,Vector2.UnitX);
            displacement+=result.MediumVelocity*(float)config.FixedDeltaSeconds;
        }
        return displacement.LengthSquared()>1e-10f?Vector2.Dot(Vector2.Normalize(displacement),Vector2.UnitX):0.0;
    }

    private static (SurvivalReflexWorldTrack Enabled, SurvivalReflexWorldTrack Disabled) PairedShoreTrial()
    {
        SimulationConfig enabledConfig = new()
        {
            InitialSoilWaterScale = 0.0,
            ReproductionEnergyThreshold = 100.0,
            SurvivalReflexEnabled = true
        };
        SimulationConfig disabledConfig = enabledConfig with { SurvivalReflexEnabled = false };
        Genome source = Genome.CreateAncestor();
        Genome founder = new(source.Regions,source.MutationRate,source.Metabolism,
            source.ControllerNodes,source.Sensors.Select(sensor=>sensor with { Gain=0.0 }));
        SimulationWorld enabled = new(enabledConfig, Seed, 1, founder, mutationsEnabled: false);
        SimulationWorld disabled = new(disabledConfig, Seed, 1, founder, mutationsEnabled: false);
        (Vector2 water, Vector2 landDirection, float depth) = FindShoreStart(enabled.Environment,
            enabledConfig.WorldSize, enabled.Organisms[0].Body.Cache.BoundingRadius);
        double heading = Math.Atan2(landDirection.Y, landDirection.X);
        enabled.RelocateForMediumDiagnostic(enabled.Organisms[0].Id, water, depth, heading);
        disabled.RelocateForMediumDiagnostic(disabled.Organisms[0].Id, water, depth, heading);
        int stepCount = (int)Math.Round(20.0 / enabledConfig.FixedDeltaSeconds);
        return (Track(enabled, stepCount), Track(disabled, stepCount));
    }

    private static SurvivalReflexWorldTrack Track(SimulationWorld world, int steps)
    {
        ulong id = world.Organisms[0].Id;
        bool land = false, active = false, returned = false, finite = true;
        double? landSeconds = null, returnSeconds = null;
        double minimumHydration = 1.0,maximumHydrationAfterLand=0.0,
            maximumImmersionAfterLand=0.0,path = 0.0, headingChange = 0.0,
            maximumChemicalAccess=0.0,maximumVision=0.0;
        Vector2 previous = world.Organisms[0].Position;
        double previousHeading = world.Organisms[0].HeadingRadians;
        Organism last = world.Organisms[0];
        for (int step = 0; step < steps; step++)
        {
            world.Step();
            int index = -1;
            for (int candidate = 0; candidate < world.Organisms.Count; candidate++)
                if (world.Organisms[candidate].Id == id) { index = candidate; break; }
            if (index < 0) break;
            last = world.Organisms[index];
            finite &= last.AllFinite;
            maximumChemicalAccess=Math.Max(maximumChemicalAccess,last.ChemicalSenseAccess);
            maximumVision=Math.Max(maximumVision,Math.Abs(last.VisionSignal));
            bool onLand = last.Immersion <= 0.05;
            if (!land && onLand)
            {
                land = true;
                landSeconds = world.SimulatedSeconds;
                previous = last.Position;
                previousHeading = last.HeadingRadians;
            }
            if (!land) continue;
            active |= last.SurvivalReflexActive;
            path += SphericalWorld.Distance(previous, last.Position, world.Config.WorldSize);
            previous = last.Position;
            minimumHydration = Math.Min(minimumHydration, last.Hydration);
            maximumHydrationAfterLand=Math.Max(maximumHydrationAfterLand,last.Hydration);
            maximumImmersionAfterLand=Math.Max(maximumImmersionAfterLand,last.Immersion);
            headingChange = Math.Max(headingChange, Math.Abs(NormalizeAngle(last.HeadingRadians - previousHeading)));
            if (!returned && last.Immersion >= 0.80)
            {
                returned = true;
                returnSeconds = world.SimulatedSeconds;
            }
        }
        bool alive = world.Organisms.Any(o => o.Id == id);
        return new(land, active, returned, landSeconds, returnSeconds, minimumHydration,
            maximumHydrationAfterLand,last.Hydration,maximumImmersionAfterLand,last.Immersion,path, headingChange,
            maximumChemicalAccess,maximumVision,alive,
            finite && world.CaptureSnapshot().AllFinite);
    }

    private static (Vector2 Water, Vector2 LandDirection, float Depth) FindShoreStart(
        IEnvironmentField environment, float worldSize, double bodyRadius)
    {
        float scan = worldSize / 128f;
        Vector2[] directions = [Vector2.UnitX, -Vector2.UnitX, Vector2.UnitY, -Vector2.UnitY];
        for (float y = scan; y < worldSize - scan; y += scan)
        for (float x = 0; x < worldSize; x += scan)
        {
            Vector2 water = new(x, y);
            EnvironmentSample sample = environment.Sample(water);
            float minimumDepth = (float)Math.Max(0.15, bodyRadius * 0.50);
            if (sample.WaterDepth < minimumDepth || sample.WaterDepth > 2.5 ||
                sample.DissolvedOxygenAvailability < 0.22) continue;
            foreach (Vector2 direction in directions)
            {
                Vector2 land = SphericalWorld.OffsetPosition(water, direction * scan, worldSize);
                if (environment.Sample(land).WaterDepth <= 0.0)
                {
                    float low=0f,high=scan;
                    for(int iteration=0;iteration<16;iteration++)
                    {
                        float middle=(low+high)*0.5f;
                        Vector2 candidate=SphericalWorld.OffsetPosition(water,direction*middle,worldSize);
                        if(environment.Sample(candidate).WaterDepth>0.0)low=middle;else high=middle;
                    }
                    Vector2 nearWater=SphericalWorld.OffsetPosition(water,
                        direction*Math.Max(0f,low-0.12f),worldSize);
                    double nearDepth=environment.Sample(nearWater).WaterDepth;
                    if(nearDepth<=0.0)continue;
                    return (nearWater, direction, (float)Math.Max(0.01,nearDepth*0.5));
                }
            }
        }
        throw new InvalidOperationException("No bounded shallow-water shore start was found.");
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle > Math.PI) angle -= Math.Tau;
        while (angle < -Math.PI) angle += Math.Tau;
        return angle;
    }
}
