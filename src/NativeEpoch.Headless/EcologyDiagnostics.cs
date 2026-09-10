using NativeEpoch.Simulation;

internal static class EcologyDiagnostics
{
    public static int Run(SimulationWorld world, int steps, int reportEvery)
    {
        int maximumGeneration = 0, maturedDescendants = 0, maturedMutants = 0;
        ulong ancestor = world.Genomes.Get(world.Organisms[0].GenomeId).Fingerprint;
        HashSet<ulong> mature = [];
        long previousBirths = 0, previousDeaths = 0;
        for (int step = 0; step < steps && world.Organisms.Count > 0; step++)
        {
            world.Step();
            foreach (Organism organism in world.Organisms)
            {
                maximumGeneration = Math.Max(maximumGeneration, organism.Generation);
                if (organism.Generation > 0 && organism.Maturity >= 0.95 && mature.Add(organism.Id))
                {
                    maturedDescendants++;
                    if (world.Genomes.Get(organism.GenomeId).Fingerprint != ancestor) maturedMutants++;
                }
            }
            if ((step + 1) % reportEvery != 0 && step + 1 != steps && world.Organisms.Count > 0) continue;
            SimulationSnapshot s = world.CaptureSnapshot();
            int matureNow = 0, lowMatter = 0, lowEnergy = 0;
            foreach (Organism o in world.Organisms)
            {
                if (o.Maturity < 0.95) continue;
                matureNow++;
                if (o.Body.TotalSubstrate < world.Config.CoreInitialMatter + world.Config.NewbornSubstrate) lowMatter++;
                if (o.Body.TotalEnergy < world.Config.ReproductionEnergyThreshold) lowEnergy++;
            }
            Console.WriteLine($"ecology t={s.SimulatedSeconds:F0} alive={s.Population} gen_ever={maximumGeneration} born/died_interval={s.CumulativeBirths-previousBirths}/{s.CumulativeDeaths-previousDeaths} mature_now={matureNow} mature_lacks_matter/energy={lowMatter}/{lowEnergy} matured_descendants/mutants={maturedDescendants}/{maturedMutants} minerals={s.EnvironmentMinerals:F2} detritus={s.EnvironmentDetritus:F2} stored={s.OrganismStoredMatter:F2} energy={s.LivingEnergy:F2} water/shore/land={s.AquaticPopulation}/{s.ShorePopulation}/{s.LandPopulation} algae={((IMutableEnvironmentField)world.Environment).TotalAlgaeBiomass:F2} plants={((IMutableEnvironmentField)world.Environment).TotalLandPlantBiomass:F2} waste={s.EnvironmentMetabolicWaste:F2} feeding={s.CumulativeOrganicFeeding:F2} production={s.CumulativePrimaryProduction:F2} matter_error={s.MatterError:E3}");
            previousBirths = s.CumulativeBirths; previousDeaths = s.CumulativeDeaths;
        }
        foreach (IGrouping<DeathCause, DeathRecord> group in world.RecentDeaths.GroupBy(d => d.Cause))
            Console.WriteLine($"recent_deaths={group.Key} n={group.Count()} development={group.Average(d=>d.DevelopmentCompletion):F3} energy={group.Average(d=>d.Energy):F2} substrate={group.Average(d=>d.Substrate):F3} hydration={group.Average(d=>d.Hydration):F3} age={group.Average(d=>d.AgeSeconds):F1}");
        SimulationSnapshot final = world.CaptureSnapshot();
        bool healthyLedger = final.AllFinite && Math.Abs(final.MatterError) < Math.Max(1e-8, final.InitialMatter*1e-10);
        Console.WriteLine($"ECOLOGY COMPLETE survived={final.Population>0} gen_ever={maximumGeneration} ledger={healthyLedger}");
        return healthyLedger ? 0 : 1;
    }
}
