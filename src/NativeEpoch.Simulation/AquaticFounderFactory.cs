namespace NativeEpoch.Simulation;

/// <summary>Seeded primitive diversity, not a catalogue of species or advanced organs.</summary>
public static class AquaticFounderFactory
{
    private static readonly Genome Basis = Genome.CreateAncestor();

    public static Genome Create(DeterministicRandom random)
    {
        double Range(double minimum, double maximum) => minimum + (maximum-minimum)*random.NextUnitDouble();
        int count=1+random.NextInt(4);
        RegionGene[] regions=new RegionGene[count];
        for(int index=0;index<count;index++)
        {
            bool core=index==0;
            int parent=core?-1:random.NextInt(index);
            RegionGene source=Basis.Regions[core?0:1];
            regions[index]=source with
            {
                RegionId=index,ParentRegionId=parent,MatterSourceRegionId=parent,SignalSourceRegionId=parent,
                IsCore=core,AppearanceMaturity=core?0:0.10+0.11*index+Range(0,0.06),
                TargetLength=core?Range(0.12,1.25):Range(0.30,0.95),
                TargetWidth=core?Range(0.38,0.85):Range(0.20,0.43),
                RelativeAngle=core?Range(-0.4,0.4):Range(-Math.PI,Math.PI),
                CrossSectionAspect=Range(0.35,1.0),Taper=Range(0,0.42),
                Curvature=Range(-0.38,0.38),Roundness=Range(0.70,1.0),
                Density=Range(0.40,0.70),Rigidity=Range(0.20,0.48),Toughness=Range(0.35,0.65),
                Permeability=Range(0.40,0.78),LightReactivity=Range(0.50,0.85),
                CatalyticActivity=Range(0.30,0.62),Contractility=Range(0.15,0.36),
                SignalConductivity=Range(0.45,0.75),StorageFraction=Range(0.35,0.62),Pigment=Range(0.12,0.88),
                ExchangeExpression=Range(0.60,0.85),BarrierExpression=Range(0.15,0.32),
                ContractileExpression=Range(0.38,0.65),StructuralExpression=Range(0.25,0.48),
                SensoryExpression=Range(0.48,0.72),
                CavityFraction=0,CavityAperture=0,JointRestPitch=0,JointMobility=0,
                PhotosyntheticExpression=Range(0.68,0.94),
                FeedingExpression=Range(0.05,0.18),DigestiveExpression=Range(0.12,0.28),
                DecomposerExpression=0.0,AirExchangeAffinity=0.0
            };
        }

        // Keep a real juvenile reserve above the initial 0.25 matter without
        // changing aspect ratio, forcing a three-region body, or adding organs.
        double coreTarget=BodyCalculator.TargetMatter(regions[0]);
        if(coreTarget<0.40)
        {
            double scale=Math.Sqrt((0.40-0.12)/(coreTarget-0.12));
            regions[0]=regions[0] with
            {
                TargetLength=regions[0].TargetLength*scale,
                TargetWidth=regions[0].TargetWidth*scale
            };
        }

        ControllerNodeGene[] controller=Basis.ControllerNodes.Select(node=>node with
        {
            Bias=node.Bias+Range(-0.12,0.12),
            SelfMemoryWeight=node.SelfMemoryWeight+Range(-0.12,0.12),
            RecurrentWeight=node.RecurrentWeight+Range(-0.12,0.12),
            ContractionOutputWeight=node.ContractionOutputWeight+Range(-0.12,0.12),
            LateralContractionOutputWeight=node.LateralContractionOutputWeight+Range(-0.12,0.12),
            VerticalContractionOutputWeight=node.VerticalContractionOutputWeight+Range(-0.10,0.10)
        }).ToArray();
        SensorGene[] sensors=
        [
            new(SensorChannel.ChemicalResource,0,0,Range(0.8,1.2),Range(1.5,3.0)),
            new(SensorChannel.ContactPressure,random.NextInt(count),1,Range(0.6,1.0),Range(2.5,4.5)),
            new(SensorChannel.InternalEnergy,0,2,Range(0.55,0.85),Range(1.0,2.0)),
            new(SensorChannel.Hydration,random.NextInt(count),1,Range(0.4,0.7),Range(1.2,2.4))
        ];
        return new Genome(regions,Range(0.22,0.32),
            new MetabolicGene(Range(0.25,0.42),Range(0.40,0.58),Range(0.10,0.28),Range(0.60,0.85),
                AnimalFoodAffinity:0.0, AttackAffinity:0.0, RetaliationAffinity:0.0),
            controller,sensors);
    }
}
