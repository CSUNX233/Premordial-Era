using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct BodyPoseRegion(
    int RegionId, Vector2 LocalCenter, double Angle, double Length,
    double Width, double Thickness, double Activation);

public readonly record struct BodyMechanicsResult(
    Vector2 MediumVelocity, double AngularVelocity, Vector2 NetExternalForce,
    double NetExternalTorque, double EnergySpent, double ShapeWork,
    double InternalForceResidual, double ForceBalanceResidual,
    double ConnectionLoad, int GroundContactRegions);

/// <summary>Region pose, paired connection forces, and force/torque-free local-drag solve.</summary>
public sealed class BodyPose
{
    private readonly Dictionary<int, BodyPoseRegion> _regions = [];
    private readonly Dictionary<int, Vector2> _regionVelocities = [];
    private readonly Dictionary<int, double> _regionAngularVelocities = [];
    private readonly Dictionary<int, RegionGene> _genes = [];
    private readonly Dictionary<int, BodyGeometryRegion> _rest = [];
    private readonly Dictionary<int, BodyRegion> _inventories = [];
    private readonly Dictionary<int, BodyPoseRegion> _old = [];
    private readonly Dictionary<int, BodyPoseRegion> _next = [];
    private readonly Dictionary<int, Vector2> _forces = [];
    private readonly Dictionary<int, double> _torques = [];
    private readonly Dictionary<int, double> _targetLengths = [];
    private readonly Dictionary<int, double> _targetAngles = [];
    private readonly Dictionary<int, double> _activation = [];
    private readonly Dictionary<int, Vector2> _surfaceSlip = [];

    public BodyPose(Genome genome, DevelopingBody body) => Reset(genome, body);
    public IReadOnlyCollection<BodyPoseRegion> Regions => _regions.Values;
    public bool TryGetRegion(int regionId,out BodyPoseRegion region)=>_regions.TryGetValue(regionId,out region);

    public void Reset(Genome genome, DevelopingBody body)
    {
        _regions.Clear(); _regionVelocities.Clear(); _regionAngularVelocities.Clear();
        foreach (BodyGeometryRegion region in body.Geometry.Regions)
        {
            _regions.Add(region.RegionId, ToPose(region, 0));
            _regionVelocities.Add(region.RegionId, Vector2.Zero);
            _regionAngularVelocities.Add(region.RegionId, 0);
        }
        CenterPose(_regions, body);
    }

    public void Synchronize(Genome genome, DevelopingBody body)
    {
        BodyGeometry geometry = body.Geometry;
        HashSet<int> valid = geometry.Regions.Select(region => region.RegionId).ToHashSet();
        foreach (int removed in _regions.Keys.Where(id => !valid.Contains(id)).ToArray())
        {
            _regions.Remove(removed);
            _regionVelocities.Remove(removed);
            _regionAngularVelocities.Remove(removed);
        }

        Dictionary<int, BodyGeometryRegion> rest = geometry.Regions.ToDictionary(region => region.RegionId);
        Dictionary<int, BodyRegion> inventories = body.Regions.ToDictionary(region => region.RegionId);
        foreach (BodyGeometryRegion region in geometry.Regions)
        {
            if (_regions.ContainsKey(region.RegionId))
                continue;
            BodyPoseRegion pose = ToPose(region, inventories[region.RegionId].Activation);
            if (region.ParentRegionId >= 0 && _regions.TryGetValue(region.ParentRegionId, out BodyPoseRegion parent))
            {
                double relativeAngle = region.Angle - rest[region.ParentRegionId].Angle;
                double angle = parent.Angle + relativeAngle;
                pose = pose with
                {
                    Angle = angle,
                    LocalCenter = EndOf(parent) + Direction(angle) * (float)(pose.Length * 0.5)
                };
            }
            _regions.Add(region.RegionId, pose);
            _regionVelocities.Add(region.RegionId, Vector2.Zero);
            _regionAngularVelocities.Add(region.RegionId, 0.0);
        }
        CenterPose(_regions, body);
    }

    public BodyMechanicsResult Step(Genome genome, DevelopingBody body, ControllerOutputs outputs,
        double ageSeconds, double immersion, double hydration, SimulationConfig config, double dt)
        => Step(genome, body, outputs, ageSeconds, immersion, hydration, config, dt, true, false);

    public BodyMechanicsResult Step(Genome genome, DevelopingBody body, ControllerOutputs outputs,
        double ageSeconds, double immersion, double hydration, SimulationConfig config, double dt,
        bool groundSupported, bool reciprocalDiagnostic,Vector2? activeSurfaceDirection=null)
    {
        BodyGeometry geometry = body.Geometry;
        if (geometry.Regions.Any(r => !_regions.ContainsKey(r.RegionId)) ||
            _regions.Keys.Any(id => geometry.Regions.All(r => r.RegionId != id))) Reset(genome, body);
        _genes.Clear(); foreach (RegionGene gene in genome.Regions) _genes[gene.RegionId] = gene;
        _rest.Clear(); foreach (BodyGeometryRegion region in geometry.Regions) _rest[region.RegionId] = region;
        _inventories.Clear(); foreach (BodyRegion region in body.Regions) _inventories[region.RegionId] = region;
        _old.Clear(); _forces.Clear(); _torques.Clear(); _targetLengths.Clear(); _targetAngles.Clear(); _activation.Clear(); _surfaceSlip.Clear();
        foreach ((int id, BodyPoseRegion pose) in _regions)
        {
            _old[id] = pose;
            _forces[id] = Vector2.Zero;
            _torques[id] = 0.0;
        }
        double connectionLoad = 0;

        // Local target strains arise from inherited controller output and region properties.
        foreach (BodyGeometryRegion region in geometry.Regions)
        {
            RegionGene gene = _genes[region.RegionId];
            BodyRegion inventory = _inventories[region.RegionId];
            double phase = reciprocalDiagnostic ? ageSeconds*Math.Tau/4.0 :
                ageSeconds * (1.50 + 0.80 * gene.SignalConductivity) + region.RegionId * 1.618;
            // BodyRegion.Activation already includes the inherited controller output.
            double a = Math.Clamp(inventory.Activation, 0, 1);
            _activation[region.RegionId] = a;
            _targetLengths[region.RegionId] = region.Length * (1.0 - 0.34 * a * (0.5 + 0.5 * Math.Sin(phase + inventory.InternalSignal)));
            _targetAngles[region.RegionId] = region.Angle + (reciprocalDiagnostic ? 0.0 : a *
                (0.62 * outputs.LateralContraction + 0.22 * Math.Sin(phase + Math.PI * 0.5)));
        }

        // Pairwise forces/torques: every contribution is added to one region and
        // subtracted from its connected parent. Disconnecting/reparenting changes the load path.
        foreach (BodyGeometryRegion region in geometry.Regions.Where(r => r.ParentRegionId >= 0))
        {
            int childId = region.RegionId, parentId = region.ParentRegionId;
            BodyPoseRegion child = _old[childId], parent = _old[parentId];
            Vector2 childStart = StartOf(child), parentEnd = EndOf(parent);
            Vector2 gap = parentEnd - childStart;
            RegionGene gene = _genes[childId];
            double structural=_inventories[childId].StructuralExpression;
            double k = 0.05 + 4.95 * gene.Rigidity*structural;
            Vector2 connection = gap * (float)k;
            _forces[childId] += connection; _forces[parentId] -= connection;
            double restRelative = _rest[childId].Angle-_rest[parentId].Angle;
            double angleError = Normalize((parent.Angle + restRelative) - child.Angle);
            double jointTorque = angleError * k * (0.25 + gene.Toughness);
            connectionLoad += connection.Length() + Math.Abs(jointTorque);
            _torques[childId] += jointTorque; _torques[parentId] -= jointTorque;
        }

        bool passiveEquilibrium = _activation.Values.All(value => value <= 1e-12) &&
            _forces.Values.All(force => force.LengthSquared() <= 1e-16f) &&
            _torques.Values.All(torque => Math.Abs(torque) <= 1e-10) &&
            geometry.Regions.All(region => Math.Abs(_old[region.RegionId].Length-region.Length) <= 1e-10 &&
                Math.Abs(Normalize(_old[region.RegionId].Angle-region.Angle)) <= 1e-10);
        if(passiveEquilibrium)
            return new(Vector2.Zero,0,Vector2.Zero,0,0,0,0,0,connectionLoad,
                immersion<=0.05&&groundSupported?_old.Count:0);

        _next.Clear();
        double energySpent = 0, requestedWork = 0;
        foreach (BodyGeometryRegion region in geometry.Regions.OrderBy(r => Depth(r.RegionId, _genes)))
        {
            int id = region.RegionId;
            RegionGene gene = _genes[id];
            BodyPoseRegion before = _old[id];
            double axialError = _targetLengths[id] - before.Length;
            double angularError = Normalize(_targetAngles[id] - before.Angle);
            double expressedRigidity=gene.Rigidity*_inventories[id].StructuralExpression;
            double axialForce = (0.6 + 3.0 * expressedRigidity) * axialError;
            double actuatorTorque = (0.3 + 1.8 * expressedRigidity) * angularError;
            double desiredDl = axialError * (1.0-Math.Exp(-(1.8+(1.2*gene.Contractility))*dt));
            double desiredDa = angularError * (1.0-Math.Exp(-(2.0+(1.4*gene.Contractility))*dt));
            if(reciprocalDiagnostic){desiredDl=_targetLengths[id]-before.Length;desiredDa=0;}
            double work = config.LocalActuationEnergyScale *
                (Math.Abs(axialForce * desiredDl) + Math.Abs(actuatorTorque * desiredDa));
            double paid = body.ConsumeRegionEnergy(id, work);
            double payFraction = work > 1e-12 ? paid / work : 1.0;
            desiredDl *= payFraction; desiredDa *= payFraction;
            requestedWork += work; energySpent += paid;

            BodyFunctionalGeometry functional=body.FunctionalGeometry[body.IndexOfRegion(id)];
            double mediumCoupling=immersion+((1.0-immersion)*(groundSupported?0.20:0.0));
            double requestedSlip=config.ActiveSurfaceDriveSpeed*Math.Sqrt(_activation[id])*
                (0.25+(0.75*gene.Permeability))*Math.Sqrt(functional.ExposureFraction)*mediumCoupling;
            Vector2 localAxis=Direction(before.Angle);
            Vector2 slipDirection=activeSurfaceDirection is Vector2 reflexDirection&&
                reflexDirection.LengthSquared()>1e-8f
                ?Vector2.Normalize(reflexDirection)
                :Vector2.Normalize((Vector2.UnitX*0.95f)+(localAxis*0.05f));
            double turnScale=outputs.LateralContraction*0.80*requestedSlip/
                Math.Max(0.15,body.Cache.BoundingRadius);
            Vector2 rotational=new(-before.LocalCenter.Y,before.LocalCenter.X);
            // Surface flow and body rotation have opposite signs in the
            // force/torque-free solve. Positive steering must yield a positive
            // (counter-clockwise) body heading change.
            Vector2 requestedSurfaceVelocity=-slipDirection*(float)requestedSlip-
                rotational*(float)turnScale;
            double slipWork=config.ActiveSurfaceDriveEnergyScale*requestedSurfaceVelocity.LengthSquared()*
                Math.Max(0.05,functional.ExposedSurface)*dt;
            double slipPaid=body.ConsumeRegionEnergy(id,slipWork);
            double slipFraction=slipWork>1e-12?Math.Sqrt(slipPaid/slipWork):1.0;
            _surfaceSlip[id]=requestedSurfaceVelocity*(float)slipFraction;
            requestedWork+=slipWork;energySpent+=slipPaid;

            Vector2 velocity = (_regionVelocities[id] + _forces[id] * (float)(dt / Math.Max(0.1, _inventories[id].Matter)))
                * (float)Math.Exp(-(2.0 + 2.0 * expressedRigidity) * dt);
            double angularVelocity = (_regionAngularVelocities[id] + _torques[id] * dt /
                Math.Max(0.05, before.Length * before.Length)) * Math.Exp(-(2.2 + expressedRigidity) * dt);
            double length = Math.Clamp(before.Length + desiredDl, region.Length * 0.70, region.Length * 1.05);
            double angle = before.Angle + desiredDa + angularVelocity * dt;
            Vector2 center = before.LocalCenter + velocity * (float)dt;
            // Incompressible first-order actuator: shortening expands cross-section.
            double crossScale = region.Length<=1e-8?1.0:Math.Sqrt(region.Length / Math.Max(1e-8, length));
            double width = Math.Max(1e-6,(region.StartRadius + region.EndRadius) * crossScale);
            _next[id] = new(id, center, angle, length, width, width * region.VerticalScale, _activation[id] * payFraction);
            _regionVelocities[id] = velocity;
            _regionAngularVelocities[id] = angularVelocity;
        }

        // Project only the attachment point, preserving the force-driven angles and lengths.
        foreach (BodyGeometryRegion region in geometry.Regions.Where(r => r.ParentRegionId >= 0)
                     .OrderBy(r => Depth(r.RegionId, _genes)))
        {
            BodyPoseRegion child = _next[region.RegionId];
            Vector2 requiredStart = EndOf(_next[region.ParentRegionId]);
            _next[region.RegionId] = child with { LocalCenter = requiredStart + Direction(child.Angle) * (float)(child.Length * 0.5) };
        }
        CenterPose(_next, _inventories);

        Vector2 forceSum = _forces.Values.Aggregate(Vector2.Zero, (sum, force) => sum + force);
        double torqueSum = _torques.Values.Sum();
        (Vector2 mediumVelocity, double omega, double balanceResidual) =
            SolveMediumReaction(_old, _next,body,_surfaceSlip,immersion, hydration, dt, groundSupported);

        _regions.Clear(); foreach ((int id, BodyPoseRegion pose) in _next) _regions.Add(id, pose);
        return new(mediumVelocity, omega, Vector2.Zero, 0, energySpent, requestedWork,
            forceSum.Length() + Math.Abs(torqueSum), balanceResidual, connectionLoad,
            immersion <= 0.05 && groundSupported ? _next.Count : 0);
    }

    private static (Vector2 Velocity, double Omega, double Residual) SolveMediumReaction(
        IReadOnlyDictionary<int, BodyPoseRegion> old, IReadOnlyDictionary<int, BodyPoseRegion> next,
        DevelopingBody body,IReadOnlyDictionary<int,Vector2> surfaceSlip,
        double immersion, double hydration, double dt, bool groundSupported)
    {
        if (immersion <= 0.05 && !groundSupported)
            return (Vector2.Zero, 0.0, 0.0);
        double[,] a = new double[3,3]; double[] b = new double[3];
        Vector2 com = WeightedCenter(next.Values, body);
        foreach (BodyPoseRegion pose in next.Values)
        {
            BodyPoseRegion prior = old[pose.RegionId];
            Vector2 d = Direction(pose.Angle);
            Vector2 v = (pose.LocalCenter - prior.LocalCenter) / (float)dt+
                surfaceSlip.GetValueOrDefault(pose.RegionId);
            double slender = Math.Clamp(pose.Length / Math.Max(0.05, pose.Width), 1, 12);
            // Isotropic shapes approach equal coefficients; elongated shapes retain anisotropy.
            double anisotropy = Math.Clamp((slender - 1.0) / 5.0, 0, 1) *
                (immersion > 0.05 ? 1.0 : 0.15);
            double mediumScale = immersion > 0.05 ? 1.0/Math.Max(0.08,immersion) : 3.2;
            double parallel = mediumScale * (0.8 + 0.12 * slender);
            double perpendicular = parallel * (1.0 + 0.72 * anisotropy);
            double zxx = perpendicular + (parallel-perpendicular)*d.X*d.X;
            double zxy = (parallel-perpendicular)*d.X*d.Y;
            double zyy = perpendicular + (parallel-perpendicular)*d.Y*d.Y;
            Vector2 r = pose.LocalCenter-com;
            double qx=-r.Y, qy=r.X;
            a[0,0]+=zxx; a[0,1]+=zxy; a[0,2]+=zxx*qx+zxy*qy;
            a[1,0]+=zxy; a[1,1]+=zyy; a[1,2]+=zxy*qx+zyy*qy;
            a[2,0]=a[0,2]; a[2,1]=a[1,2];
            a[2,2]+=qx*(zxx*qx+zxy*qy)+qy*(zxy*qx+zyy*qy);
            double fx=zxx*v.X+zxy*v.Y, fy=zxy*v.X+zyy*v.Y;
            b[0]-=fx; b[1]-=fy; b[2]-=r.X*fy-r.Y*fx;
            // A finite region resists rotation about its own centre as well as
            // translation of that centre. Point-only drag leaves compact bodies
            // with almost no rotational resistance and amplifies tiny shape motion.
            double spinDrag = (perpendicular * pose.Length * pose.Length +
                parallel * pose.Width * pose.Width) / 12.0;
            double shapeSpin = Normalize(pose.Angle - prior.Angle) / dt;
            a[2,2] += spinDrag;
            b[2] -= spinDrag * shapeSpin;
        }
        a[2,2]+=1e-6;
        double[] solution = Solve3(a,b);
        Vector2 u = new((float)solution[0],(float)solution[1]);
        double omega=solution[2];
        double residual=0;
        for(int row=0;row<3;row++) residual=Math.Max(residual,Math.Abs(a[row,0]*solution[0]+a[row,1]*solution[1]+a[row,2]*solution[2]-b[row]));
        return (u,omega,residual);
    }

    private static double[] Solve3(double[,] a,double[] b)
    {
        double[,] m=new double[3,4]; for(int r=0;r<3;r++){for(int c=0;c<3;c++)m[r,c]=a[r,c];m[r,3]=b[r];}
        for(int p=0;p<3;p++)
        {
            int best=p; for(int r=p+1;r<3;r++) if(Math.Abs(m[r,p])>Math.Abs(m[best,p])) best=r;
            if(best!=p) for(int c=p;c<4;c++) (m[p,c],m[best,c])=(m[best,c],m[p,c]);
            double pivot=Math.Abs(m[p,p])<1e-10?1e-10:m[p,p];
            for(int c=p;c<4;c++)m[p,c]/=pivot;
            for(int r=0;r<3;r++) if(r!=p){double f=m[r,p];for(int c=p;c<4;c++)m[r,c]-=f*m[p,c];}
        }
        return [m[0,3],m[1,3],m[2,3]];
    }
    private static void CenterPose(Dictionary<int,BodyPoseRegion> poses,DevelopingBody body)
    { Vector2 c=WeightedCenter(poses.Values,body); foreach(int id in poses.Keys.ToArray()) poses[id]=poses[id] with{LocalCenter=poses[id].LocalCenter-c}; }
    private static void CenterPose(Dictionary<int,BodyPoseRegion> poses,IReadOnlyDictionary<int,BodyRegion> inventories)
    {Vector2 sum=Vector2.Zero;double weight=0;foreach(BodyPoseRegion region in poses.Values){double matter=inventories[region.RegionId].Matter;sum+=region.LocalCenter*(float)matter;weight+=matter;}Vector2 center=weight>0?sum/(float)weight:Vector2.Zero;foreach(int id in poses.Keys.ToArray())poses[id]=poses[id] with{LocalCenter=poses[id].LocalCenter-center};}
    private static int Depth(int id,IReadOnlyDictionary<int,RegionGene> genes){int n=0;while(!genes[id].IsCore){id=genes[id].ParentRegionId;n++;}return n;}
    private static Vector2 Direction(double a)=>new((float)Math.Cos(a),(float)Math.Sin(a));
    private static Vector2 StartOf(BodyPoseRegion r)=>r.LocalCenter-Direction(r.Angle)*(float)(r.Length*0.5);
    private static Vector2 EndOf(BodyPoseRegion r)=>r.LocalCenter+Direction(r.Angle)*(float)(r.Length*0.5);
    private static BodyPoseRegion ToPose(BodyGeometryRegion r,double a)=>new(r.RegionId,r.Center,r.Angle,r.Length,r.StartRadius+r.EndRadius,(r.StartRadius+r.EndRadius)*r.VerticalScale,a);
    private static double Normalize(double a)=>Math.Atan2(Math.Sin(a),Math.Cos(a));
    private static Vector2 WeightedCenter(IEnumerable<BodyPoseRegion> regions,DevelopingBody body)
    {Vector2 sum=Vector2.Zero;double w=0;foreach(BodyPoseRegion r in regions){double m=body.Regions.Single(x=>x.RegionId==r.RegionId).Matter;sum+=r.LocalCenter*(float)m;w+=m;}return w>0?sum/(float)w:Vector2.Zero;}
}
