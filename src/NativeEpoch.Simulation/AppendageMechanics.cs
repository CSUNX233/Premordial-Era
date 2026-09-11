using System.Numerics;

namespace NativeEpoch.Simulation;

/// <summary>A developed region lifted into body-local 3D for appendage mechanics and rendering.</summary>
public readonly record struct AppendageRegionPose(
    int RegionId,
    int ParentRegionId,
    Vector3 LocalStart,
    Vector3 LocalEnd,
    double JointPitch,
    double ActiveDrive,
    double ActuationEnergy,
    double SupportStrength,
    bool Connected)
{
    public bool AllFinite => RegionId >= 0 &&
        float.IsFinite(LocalStart.X) && float.IsFinite(LocalStart.Y) && float.IsFinite(LocalStart.Z) &&
        float.IsFinite(LocalEnd.X) && float.IsFinite(LocalEnd.Y) && float.IsFinite(LocalEnd.Z) &&
        double.IsFinite(JointPitch) && double.IsFinite(ActiveDrive) &&
        double.IsFinite(ActuationEnergy) && ActuationEnergy >= 0 &&
        double.IsFinite(SupportStrength);
}

/// <summary>One bounded terrain query and its usable normal/friction state.</summary>
public readonly record struct AppendageContact(
    int RegionId,
    Vector2 WorldPosition,
    double TipElevation,
    double TerrainElevation,
    double NormalLoad,
    double Friction,
    bool Planted)
{
    public bool AllFinite => RegionId >= 0 &&
        float.IsFinite(WorldPosition.X) && float.IsFinite(WorldPosition.Y) &&
        double.IsFinite(TipElevation) && double.IsFinite(TerrainElevation) &&
        double.IsFinite(NormalLoad) && NormalLoad >= 0 &&
        double.IsFinite(Friction) && Friction >= 0;
}

public readonly record struct AppendageMechanicsResult(
    Vector2 GroundVelocity,
    double AngularVelocity,
    double EnergySpent,
    double SupportFraction,
    double BodyLift,
    IReadOnlyList<AppendageRegionPose> Regions,
    IReadOnlyList<AppendageContact> Contacts)
{
    public bool AllFinite
    {
        get
        {
            if (!float.IsFinite(GroundVelocity.X) || !float.IsFinite(GroundVelocity.Y) ||
                !double.IsFinite(AngularVelocity) || !double.IsFinite(EnergySpent) || EnergySpent < 0 ||
                !double.IsFinite(SupportFraction) || SupportFraction is < 0 or > 1 ||
                !double.IsFinite(BodyLift) || BodyLift<0.0)
                return false;
            for (int index = 0; index < Regions.Count; index++)
                if (!Regions[index].AllFinite) return false;
            for (int index = 0; index < Contacts.Count; index++)
                if (!Contacts[index].AllFinite) return false;
            return true;
        }
    }
}

/// <summary>
/// A fixed-budget articulated-ground solver. It uses developed regional expression, inherited
/// joint/material traits, local energy, real attachment paths, and terrain sampled at each foot.
/// It owns only small per-organism joint/contact history and creates no physics bodies.
/// </summary>
public sealed class AppendageMechanics
{
    public const int MaximumContacts = 8;
    private const double Gravity = 9.81;
    private const double MinimumConnection = 1e-4;
    private static readonly IComparer<AppendageRegionPose> FootComparer = new DescendingSupportComparer();
    private readonly Dictionary<int, double> _jointPitch = [];
    // Previous body-local planar tips: body translation/heading must not be mistaken for joint slip.
    private readonly Dictionary<int, Vector2> _previousTips = [];
    private readonly HashSet<int> _previouslyPlanted = [];
    private readonly Dictionary<int, RegionGene> _genes = [];
    private readonly Dictionary<int, BodyRegion> _inventories = [];
    private readonly Dictionary<int, BodyFunctionalGeometry> _functional = [];
    private readonly Dictionary<int, BodyPoseRegion> _planar = [];
    private readonly Dictionary<int, AppendageRegionPose> _built = [];
    private readonly Dictionary<int, bool> _connected = [];
    private readonly HashSet<int> _connectedParents = [];
    private readonly List<AppendageRegionPose> _regionPoses = new(GenomeValidator.MaximumRegions - 1);
    private readonly List<AppendageRegionPose> _feet = new(MaximumContacts);
    private readonly List<ContactWork> _activeContacts = new(MaximumContacts);
    private readonly List<AppendageContact> _contacts = new(MaximumContacts);
    private readonly double[,] _normal = new double[3, 3];
    private readonly double[] _rhs = new double[3];
    private readonly double[,] _solveMatrix = new double[3, 4];
    private readonly List<LiftCandidate> _liftCandidates=[];
    private double _bodyLift;

    public void Reset()
    {
        _jointPitch.Clear();
        _previousTips.Clear();
        _previouslyPlanted.Clear();
        _bodyLift=0.0;
    }

    /// <param name="bodyCenterElevation">World-space terrain-axis elevation at body local Y=0.</param>
    /// <remarks>GroundVelocity is world-planar. Region positions are body-local: X/Z planar and Y up.</remarks>
    public AppendageMechanicsResult Step(
        Genome genome,
        DevelopingBody body,
        BodyPose bodyPose,
        ControllerOutputs outputs,
        Vector2 worldPosition,
        double headingRadians,
        double bodyCenterElevation,
        IEnvironmentField environment,
        SimulationConfig config,
        double ageSeconds,
        double deltaSeconds,
        bool enableGround)
    {
        ValidateInput(worldPosition, headingRadians, bodyCenterElevation, ageSeconds, deltaSeconds);

        bool hasArticulatedRegion = false;
        for (int index = 0; index < genome.Regions.Count; index++)
        {
            RegionGene gene = genome.Regions[index];
            if (!gene.IsCore && body.IndexOfRegion(gene.RegionId) >= 0 &&
                (gene.JointMobility > 1e-8 || Math.Abs(gene.JointRestPitch) > 1e-8))
            {
                hasArticulatedRegion = true;
                break;
            }
        }
        if (!hasArticulatedRegion)
        {
            _regionPoses.Clear(); _feet.Clear(); _activeContacts.Clear(); _contacts.Clear();
            _previouslyPlanted.Clear(); _previousTips.Clear();
            _bodyLift=0.0;
            return new AppendageMechanicsResult(Vector2.Zero, 0.0, 0.0, 0.0,0.0,
                _regionPoses, _contacts);
        }

        _genes.Clear();
        for (int index = 0; index < genome.Regions.Count; index++)
        {
            RegionGene gene = genome.Regions[index];
            _genes[gene.RegionId] = gene;
        }
        _inventories.Clear();
        for (int index = 0; index < body.Regions.Count; index++)
        {
            BodyRegion region = body.Regions[index];
            _inventories[region.RegionId] = region;
        }
        _functional.Clear();
        for (int index = 0; index < body.FunctionalGeometry.Count; index++)
        {
            BodyFunctionalGeometry region = body.FunctionalGeometry[index];
            _functional[region.RegionId] = region;
        }
        _planar.Clear();
        for (int index = 0; index < body.Geometry.Regions.Count; index++)
        {
            int regionId = body.Geometry.Regions[index].RegionId;
            if (bodyPose.TryGetRegion(regionId, out BodyPoseRegion region)) _planar[regionId] = region;
        }
        _built.Clear(); _connected.Clear(); _regionPoses.Clear();
        double energySpent = 0;

        // BodyGeometryBuilder emits each parent before its children.
        for (int regionIndex = 0; regionIndex < body.Geometry.Regions.Count; regionIndex++)
        {
            BodyGeometryRegion region = body.Geometry.Regions[regionIndex];
            RegionGene gene = _genes[region.RegionId];
            if (gene.IsCore)
            {
                _connected[gene.RegionId] = true;
                continue;
            }

            BodyRegion inventory = _inventories[gene.RegionId];
            BodyPoseRegion pose = _planar[gene.RegionId];
            bool parentConnected = _connected.GetValueOrDefault(gene.ParentRegionId);
            bool edgeConnected = parentConnected && _planar.ContainsKey(gene.ParentRegionId) &&
                inventory.StructuralExpression * (1.0 - inventory.Damage) *
                _functional[gene.RegionId].ConnectionTransmission > MinimumConnection;
            _connected[gene.RegionId] = edgeConnected;

            double restPitch = Math.Clamp(gene.JointRestPitch, -1.0, 1.0) * (Math.PI * 0.5);
            if (!_jointPitch.TryGetValue(gene.RegionId, out double oldPitch) || !double.IsFinite(oldPitch))
                oldPitch = restPitch;

            // There is no universal gait. Frequency and phase are inherited/local; incoherent
            // combinations waste work just as readily as they produce useful foot trajectories.
            double expression = Math.Clamp(inventory.ContractileExpression, 0.0, 1.0);
            double actuationGate = edgeConnected
                ? Math.Clamp(inventory.Activation * inventory.Development * expression *
                    gene.Contractility, 0.0, 1.0)
                : 0.0;
            double drive = actuationGate * gene.JointMobility;
            double frequency = 0.35 + (1.65 * gene.SignalConductivity) + (0.40 * gene.JointMobility);
            double inheritedPhase = gene.RelativeAngle + (gene.Pigment * Math.Tau);
            double localPhase = inheritedPhase + (ageSeconds * frequency) + (inventory.InternalSignal * Math.PI);
            double neuralBias = Math.Clamp(outputs.VerticalContraction, -1.0, 1.0) * 0.38 +
                Math.Clamp(outputs.LateralContraction, -1.0, 1.0) * Math.Sin(gene.RelativeAngle) * 0.24;
            double targetPitch = Math.Clamp(restPitch +
                gene.JointMobility * actuationGate * (0.82 * Math.Sin(localPhase) + neuralBias),
                -Math.PI * 0.48, Math.PI * 0.38);
            double maximumChange = (0.25 + (2.75 * gene.JointMobility * expression)) * deltaSeconds;
            double desiredChange = Math.Clamp(targetPitch - oldPitch, -maximumChange, maximumChange);
            double torqueCapacity = inventory.Matter * expression * gene.Contractility *
                (0.12 + (0.88 * gene.JointMobility));
            double requestedWork = config.LocalActuationEnergyScale * torqueCapacity * Math.Abs(desiredChange);
            double paid = edgeConnected ? body.ConsumeRegionEnergy(gene.RegionId, requestedWork) : 0.0;
            double paidFraction = requestedWork > 1e-12 ? Math.Clamp(paid / requestedWork, 0.0, 1.0) : 0.0;
            double pitch = oldPitch + (desiredChange * paidFraction);
            _jointPitch[gene.RegionId] = pitch;
            energySpent += paid;

            Vector3 localStart;
            if (_built.TryGetValue(gene.ParentRegionId, out AppendageRegionPose parent))
            {
                localStart = parent.LocalEnd;
            }
            else
            {
                Vector2 start = StartOf(pose);
                double rootDrop = _planar.TryGetValue(gene.ParentRegionId, out BodyPoseRegion parentPose)
                    ? parentPose.Thickness * 0.32
                    : pose.Thickness * 0.20;
                localStart = new(start.X, (float)-rootDrop, start.Y);
            }

            Vector2 direction = Direction(pose.Angle);
            double horizontalLength = pose.Length * Math.Cos(pitch);
            Vector3 localEnd = localStart + new Vector3(
                direction.X * (float)horizontalLength,
                (float)(pose.Length * Math.Sin(pitch)),
                direction.Y * (float)horizontalLength);
            double structure = edgeConnected
                ? Math.Clamp(inventory.StructuralExpression * inventory.Development *
                    (1.0 - inventory.Damage) * gene.Rigidity * (0.25 + (0.75 * gene.Toughness)) *
                    _functional[gene.RegionId].ConnectionTransmission, 0.0, 1.0)
                : 0.0;
            AppendageRegionPose lifted = new(gene.RegionId, gene.ParentRegionId, localStart, localEnd,
                pitch, drive * paidFraction, paid, structure, edgeConnected);
            _built[gene.RegionId] = lifted;
            _regionPoses.Add(lifted);
        }

        if (!enableGround)
        {
            _feet.Clear(); _activeContacts.Clear(); _contacts.Clear();
            _previouslyPlanted.Clear(); _previousTips.Clear();
            _bodyLift=0.0;
            AppendageMechanicsResult airborne = new(Vector2.Zero, 0.0, energySpent, 0.0,0.0,
                _regionPoses, _contacts);
            if (!airborne.AllFinite)
                throw new InvalidOperationException("Appendage mechanics produced a non-finite state.");
            return airborne;
        }

        _connectedParents.Clear();
        foreach (AppendageRegionPose region in _regionPoses)
            if (region.Connected) _connectedParents.Add(region.ParentRegionId);
        _feet.Clear();
        foreach (AppendageRegionPose region in _regionPoses)
            if (region.Connected && !_connectedParents.Contains(region.RegionId)) _feet.Add(region);
        if (_feet.Count > MaximumContacts)
        {
            _feet.Sort(FootComparer);
            _feet.RemoveRange(MaximumContacts, _feet.Count - MaximumContacts);
        }

        _activeContacts.Clear();
        double requestedWeight = Math.Max(0.05, body.Cache.PhysicalMass) * Gravity;
        _liftCandidates.Clear();
        foreach (AppendageRegionPose foot in _feet)
        {
            Vector2 localPlanar = new(foot.LocalEnd.X, foot.LocalEnd.Z);
            Vector2 footWorld = SphericalWorld.OffsetPosition(
                worldPosition, Rotate(localPlanar, headingRadians), config.WorldSize);
            EnvironmentSample sample = environment.Sample(footWorld);
            double tipElevation = bodyCenterElevation + foot.LocalEnd.Y;
            double penetration=sample.TerrainHeight-tipElevation;
            double supportCapacity=requestedWeight*foot.SupportStrength;
            if(penetration>0.0&&supportCapacity>1e-9)
                _liftCandidates.Add(new LiftCandidate(penetration,supportCapacity));
        }
        _liftCandidates.Sort(static (left,right)=>right.Penetration.CompareTo(left.Penetration));
        double targetLift=0.0,cumulativeCapacity=0.0;
        foreach(LiftCandidate candidate in _liftCandidates)
        {
            cumulativeCapacity+=candidate.SupportCapacity;
            if(cumulativeCapacity+1e-9<requestedWeight)continue;
            targetLift=candidate.Penetration;
            break;
        }
        if(targetLift>_bodyLift)
        {
            // LocalActuationEnergyScale is the existing conversion from mechanical work to
            // ecosystem energy in BodyMechanics. Use the same conversion for lifting weight.
            double liftWork=requestedWeight*(targetLift-_bodyLift)*config.LocalActuationEnergyScale;
            double paid=body.ConsumeEnergy(liftWork);
            energySpent+=paid;
            _bodyLift+=liftWork>1e-12?(targetLift-_bodyLift)*Math.Clamp(paid/liftWork,0.0,1.0):0.0;
        }
        else _bodyLift=targetLift;

        foreach (AppendageRegionPose foot in _feet)
        {
            Vector2 localPlanar = new(foot.LocalEnd.X, foot.LocalEnd.Z);
            Vector2 footWorld = SphericalWorld.OffsetPosition(
                worldPosition, Rotate(localPlanar, headingRadians), config.WorldSize);
            EnvironmentSample sample = environment.Sample(footWorld);
            double tipElevation = bodyCenterElevation + _bodyLift + foot.LocalEnd.Y;
            BodyPoseRegion sourcePose = _planar[foot.RegionId];
            double tolerance = 0.035 + (0.12 * Math.Max(sourcePose.Width, sourcePose.Thickness));
            bool planted = enableGround && foot.SupportStrength > 1e-5 &&
                tipElevation <= sample.TerrainHeight + tolerance;
            double friction = Math.Clamp(0.10 +
                (0.52 * (1.0 - _genes[foot.RegionId].Roundness)) +
                (0.28 * _genes[foot.RegionId].Toughness) +
                (0.10 * (1.0 - Math.Abs(_genes[foot.RegionId].Taper))), 0.05, 1.0);
            double capacity = requestedWeight * foot.SupportStrength;
            _activeContacts.Add(new ContactWork(foot, localPlanar, footWorld, tipElevation, sample.TerrainHeight,
                friction, planted, capacity));
        }

        int plantedCount = 0;
        foreach (ContactWork contact in _activeContacts) if (contact.Planted) plantedCount++;
        _contacts.Clear();
        Array.Clear(_normal);
        Array.Clear(_rhs);
        double totalTraction = 0;
        double totalNormalLoad = 0;
        double contactEnergySpent = 0;
        double plantedCapacity=0.0;
        foreach(ContactWork contact in _activeContacts)
            if(contact.Planted)plantedCapacity+=contact.Capacity;
        for (int index = 0; index < _activeContacts.Count; index++)
        {
            ContactWork contact = _activeContacts[index];
            double normalLoad = contact.Planted&&plantedCapacity>1e-12
                ?Math.Min(contact.Capacity,requestedWeight*contact.Capacity/plantedCapacity)
                :0.0;
            _contacts.Add(new AppendageContact(contact.Foot.RegionId, contact.WorldPosition,
                contact.TipElevation, contact.TerrainElevation, normalLoad, contact.Friction, contact.Planted));
            totalNormalLoad += normalLoad;
            if (contact.Planted) contactEnergySpent += PathEnergy(contact.Foot);
            if (!contact.Planted || normalLoad <= 1e-9 ||
                !_previouslyPlanted.Contains(contact.Foot.RegionId) ||
                !_previousTips.TryGetValue(contact.Foot.RegionId, out Vector2 previousLocalTip))
                continue;

            Vector2 localFootVelocity = (contact.LocalPosition - previousLocalTip) / (float)deltaSeconds;
            Vector2 footVelocity = Rotate(localFootVelocity, headingRadians);
            Vector2 lever = contact.WorldPosition - worldPosition;
            double weight = normalLoad * contact.Friction;
            AddNoSlipEquation(_normal, _rhs, footVelocity, lever, weight);
            totalTraction += weight;
        }

        Vector2 groundVelocity = Vector2.Zero;
        double angularVelocity = 0.0;
        double supportFraction = requestedWeight > 0
            ? Math.Clamp(totalNormalLoad / requestedWeight, 0.0, 1.0)
            : 0.0;
        contactEnergySpent = Math.Min(contactEnergySpent, energySpent);
        if (totalTraction > 1e-9 && contactEnergySpent > 1e-12)
        {
            _normal[0, 0] += 1e-8;
            _normal[1, 1] += 1e-8;
            _normal[2, 2] += Math.Max(1e-8, body.Cache.RotationalInertia * 1e-7);
            (double vx, double vy, double requestedAngularVelocity) = Solve3(_normal, _rhs, _solveMatrix);
            Vector2 requestedVelocity = new((float)vx, (float)vy);
            double mass = Math.Max(0.05, body.Cache.PhysicalMass);
            // BodyMechanics charges LocalActuationEnergyScale times mechanical work. Inverting
            // that same conversion gives the mechanical energy available to this speed bound.
            double energySpeedLimit = Math.Sqrt(Math.Max(0.0,
                2.0*contactEnergySpent/(config.LocalActuationEnergyScale*mass)));
            double tractionSpeedLimit = totalTraction /
                (mass * Math.Max(1.0, config.LandFrictionMultiplier));
            double speedLimit = Math.Min(config.MaximumMovementSpeed,
                Math.Min(energySpeedLimit, tractionSpeedLimit));
            double requestedSpeed = requestedVelocity.Length();
            double scale = requestedSpeed > speedLimit && requestedSpeed > 1e-12
                ? speedLimit / requestedSpeed
                : 1.0;
            groundVelocity = requestedVelocity * (float)scale;
            double angularLimit = speedLimit / Math.Max(0.10, body.Cache.BoundingRadius);
            angularVelocity = Math.Clamp(requestedAngularVelocity * scale, -angularLimit, angularLimit);
        }

        _previouslyPlanted.Clear();
        foreach (ContactWork contact in _activeContacts)
        {
            _previousTips[contact.Foot.RegionId] = contact.LocalPosition;
            if (contact.Planted) _previouslyPlanted.Add(contact.Foot.RegionId);
        }

        AppendageMechanicsResult result = new(groundVelocity, angularVelocity, energySpent,
            supportFraction,_bodyLift, _regionPoses, _contacts);
        if (!result.AllFinite)
            throw new InvalidOperationException("Appendage mechanics produced a non-finite state.");
        return result;
    }

    private static void AddNoSlipEquation(double[,] a, double[] b, Vector2 footVelocity,
        Vector2 lever, double weight)
    {
        double qx = -lever.Y, qy = lever.X;
        a[0, 0] += weight;
        a[1, 1] += weight;
        a[0, 2] += weight * qx;
        a[2, 0] += weight * qx;
        a[1, 2] += weight * qy;
        a[2, 1] += weight * qy;
        a[2, 2] += weight * ((qx * qx) + (qy * qy));
        b[0] -= weight * footVelocity.X;
        b[1] -= weight * footVelocity.Y;
        b[2] -= weight * ((qx * footVelocity.X) + (qy * footVelocity.Y));
    }

    private readonly record struct LiftCandidate(double Penetration,double SupportCapacity);

    private double PathEnergy(AppendageRegionPose foot)
    {
        double energy = 0.0;
        AppendageRegionPose current = foot;
        for (int depth = 0; depth < GenomeValidator.MaximumRegions; depth++)
        {
            energy += current.ActuationEnergy;
            if (!_built.TryGetValue(current.ParentRegionId, out current)) break;
        }
        return energy;
    }

    private static (double X, double Y, double Angular) Solve3(double[,] a, double[] b, double[,] matrix)
    {
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++) matrix[row, column] = a[row, column];
            matrix[row, 3] = b[row];
        }
        for (int pivot = 0; pivot < 3; pivot++)
        {
            int best = pivot;
            for (int row = pivot + 1; row < 3; row++)
                if (Math.Abs(matrix[row, pivot]) > Math.Abs(matrix[best, pivot])) best = row;
            if (best != pivot)
                for (int column = pivot; column < 4; column++)
                    (matrix[pivot, column], matrix[best, column]) =
                        (matrix[best, column], matrix[pivot, column]);
            double divisor = Math.Abs(matrix[pivot, pivot]) < 1e-12 ? 1e-12 : matrix[pivot, pivot];
            for (int column = pivot; column < 4; column++) matrix[pivot, column] /= divisor;
            for (int row = 0; row < 3; row++)
            {
                if (row == pivot) continue;
                double factor = matrix[row, pivot];
                for (int column = pivot; column < 4; column++)
                    matrix[row, column] -= factor * matrix[pivot, column];
            }
        }
        return (matrix[0, 3], matrix[1, 3], matrix[2, 3]);
    }

    private static int Depth(int regionId, IReadOnlyDictionary<int, RegionGene> genes)
    {
        int depth = 0;
        int current = regionId;
        while (!genes[current].IsCore && depth <= GenomeValidator.MaximumRegions)
        {
            current = genes[current].ParentRegionId;
            depth++;
        }
        return depth;
    }

    private static Vector2 StartOf(BodyPoseRegion region) =>
        region.LocalCenter - Direction(region.Angle) * (float)(region.Length * 0.5);

    private static Vector2 Direction(double angle) =>
        new((float)Math.Cos(angle), (float)Math.Sin(angle));

    private static Vector2 Rotate(Vector2 local, double heading)
    {
        double cosine = Math.Cos(heading), sine = Math.Sin(heading);
        return new Vector2(
            (float)((local.X * cosine) - (local.Y * sine)),
            (float)((local.X * sine) + (local.Y * cosine)));
    }

    private static void ValidateInput(Vector2 position, double heading, double elevation,
        double ageSeconds, double deltaSeconds)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
            !double.IsFinite(heading) || !double.IsFinite(elevation) ||
            !double.IsFinite(ageSeconds) || ageSeconds < 0 ||
            !double.IsFinite(deltaSeconds) || deltaSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
    }

    private readonly record struct ContactWork(
        AppendageRegionPose Foot,
        Vector2 LocalPosition,
        Vector2 WorldPosition,
        double TipElevation,
        double TerrainElevation,
        double Friction,
        bool Planted,
        double Capacity);

    private sealed class DescendingSupportComparer : IComparer<AppendageRegionPose>
    {
        public int Compare(AppendageRegionPose left, AppendageRegionPose right)
        {
            int support = right.SupportStrength.CompareTo(left.SupportStrength);
            return support != 0 ? support : left.RegionId.CompareTo(right.RegionId);
        }
    }
}
