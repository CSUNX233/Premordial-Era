using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct PredationResult(
    double RemovedOrganic, double AssimilatedOrganic, double ReturnedDetritus,
    double EnergySpent, double Damage)
{
    public static PredationResult None => default;
}

/// <summary>One paid, contact-limited attempt. Living structure is not duplicated into food.</summary>
public static class ContactPredation
{
    public static PredationResult Attempt(
        DevelopingBody predator, Genome predatorGenome, Vector2 predatorPosition, float predatorDepth,
        DevelopingBody prey, Genome preyGenome, Vector2 preyPosition, float preyDepth,
        IMutableEnvironmentField environment, double deltaSeconds)
    {
        if (ReferenceEquals(predator, prey) || deltaSeconds <= 0) return PredationResult.None;
        OccupancyShape a = OccupancyShape.FromBody(predator), b = OccupancyShape.FromBody(prey);
        double horizontal = a.HorizontalRadius + b.HorizontalRadius;
        double vertical = a.VerticalHalfExtent + b.VerticalHalfExtent;
        double distance = Vector2.DistanceSquared(predatorPosition, preyPosition) / (horizontal * horizontal) +
            Math.Pow((predatorDepth - preyDepth) / vertical, 2);
        if (distance > 1.04 * 1.04) return PredationResult.None;

        double feeding = RegionalPhysiology.FeedingCapacity(predator, predatorGenome);
        double capacity = 0;
        foreach (BodyRegion region in predator.Regions)
            capacity += RegionalPhysiology.SubstrateCapacity(region, predatorGenome.GetRegion(region.RegionId));
        double appetite = Math.Clamp(1.0 - predator.TotalSubstrate / Math.Max(0.01, capacity * 0.8), 0, 1);
        if (feeding <= 1e-9 || appetite <= 0 || predator.TotalEnergy <= 1e-9) return PredationResult.None;

        // A larger/tougher prey lowers the effective contact handling rate.
        double defense = 0;
        foreach (BodyRegion region in prey.Regions)
        {
            RegionGene gene = preyGenome.GetRegion(region.RegionId);
            defense += region.Matter * gene.Toughness * (0.3 + 0.7 * region.StructuralExpression);
        }
        double handling = feeding * appetite / (1.0 + defense / Math.Max(0.05, predator.Cache.PhysicalMass));
        double requestedCost = handling * deltaSeconds * 0.035;
        double paid = predator.ConsumeEnergy(requestedCost);
        double paidFraction = requestedCost > 1e-12 ? Math.Min(1, paid / requestedCost) : 0;
        if (paidFraction <= 0) return PredationResult.None;
        double requestedFood = Math.Min(Math.Max(0, capacity - predator.TotalSubstrate),
            handling * deltaSeconds * 0.12 * paidFraction);
        double removed = prey.ExtractEdibleSubstrate(requestedFood);
        OrganicDigestionResult digestion = RegionalPhysiology.DigestOrganicToSubstrate(
            predator, predatorGenome, removed, deltaSeconds);
        double returned = Math.Max(0, removed - digestion.AssimilatedSubstrate);
        if (returned > 0) environment.DepositDetritus(preyPosition, returned);

        // Contact injury can produce a carcass through the ordinary death path;
        // no living structural matter is paid out a second time here.
        double damage = Math.Min(0.025, handling * deltaSeconds * 0.006 * paidFraction /
            Math.Max(0.05, prey.Cache.TotalMatter));
        int exposedRegion = -1;
        double exposedSurface = 0;
        foreach (BodyFunctionalGeometry geometry in prey.FunctionalGeometry)
            if (geometry.ExposedSurface > exposedSurface)
            { exposedSurface = geometry.ExposedSurface; exposedRegion = geometry.RegionId; }
        if (exposedRegion >= 0) prey.AddDamage(exposedRegion, damage);
        else damage = 0;
        return new(removed, digestion.AssimilatedSubstrate, returned, paid + digestion.EnergySpent, damage);
    }
}
