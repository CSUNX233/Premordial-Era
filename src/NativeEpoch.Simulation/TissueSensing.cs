using System.Numerics;

namespace NativeEpoch.Simulation;

public readonly record struct TissueSensingResult(
    double EnergySpent,
    int ActiveSensorCount,
    double ChemicalSignal,
    double ContactSignal,
    double ChemicalAccess,
    double AmbientLightSignal,
    int ActiveVisualSensorCount,
    double VisionSignal,
    double TemperatureSignal,
    double PressureSignal,
    double ChemicalRange,
    double VisualForwardSignal,
    double VisualLateralSignal,
    double VisualAccess);

/// <summary>
/// Converts bounded, local environment queries into signals only when a developed,
/// exposed region pays the sensing cost and has a signal path. It does not query
/// distant world state and has no camera/vision semantics.
/// </summary>
public static class TissueSensing
{
    public static TissueSensingResult Evaluate(
        DevelopingBody body, Genome genome, IEnvironmentField environment,
        Vector2 position, double headingRadians, float depth, double contactPressure,
        SimulationConfig config, double deltaSeconds,
        ref double[] sensorState, ref double[] controllerNodeInputs)
    {
        int sensorCount=genome.Sensors.Count;
        int nodeCount=genome.ControllerNodes.Count;
        if(sensorState.Length!=sensorCount)sensorState=new double[sensorCount];
        if(controllerNodeInputs.Length!=nodeCount)controllerNodeInputs=new double[nodeCount];
        else Array.Clear(controllerNodeInputs);

        double energySpent=0,chemical=0,contact=0,chemicalAccess=0,chemicalRangeWeighted=0,
            light=0,vision=0,visualForward=0,visualLateral=0,visualAccess=0,
            temperature=0,pressure=0;
        double chemicalAccessWeight=0;
        int active=0,activeVisual=0,chemicalCount=0,contactCount=0,lightCount=0,visionCount=0,temperatureCount=0,pressureCount=0;
        double c=Math.Cos(headingRadians),s=Math.Sin(headingRadians);
        Span<Vector2> positions=stackalloc Vector2[GenomeValidator.MaximumRegions];
        Span<EnvironmentSample> samples=stackalloc EnvironmentSample[GenomeValidator.MaximumRegions];
        Span<bool> sampled=stackalloc bool[GenomeValidator.MaximumRegions];
        for(int index=0;index<sensorCount;index++)
        {
            SensorGene sensor=genome.Sensors[index];
            if(sensor.Channel==SensorChannel.OrganismContrast){sensorState[index]=0;continue;}
            int bodyIndex=body.IndexOfRegion(sensor.SourceRegionId);
            if(bodyIndex<0){sensorState[index]=0;continue;}
            BodyRegion region=body.Regions[bodyIndex];
            RegionGene sourceGene=genome.GetRegion(region.RegionId);
            BodyFunctionalGeometry geometry=body.FunctionalGeometry[bodyIndex];
            if(!sampled[bodyIndex])
            {
                double x=geometry.LocalCenter.X*c-geometry.LocalCenter.Y*s;
                double y=geometry.LocalCenter.X*s+geometry.LocalCenter.Y*c;
                positions[bodyIndex]=SphericalWorld.OffsetPosition(
                    position,new Vector2((float)x,(float)y),config.WorldSize);
            }
            Vector2 samplePosition=positions[bodyIndex];
            bool needsEnvironment=sensor.Channel is SensorChannel.ChemicalResource or
                SensorChannel.AmbientLight or SensorChannel.Temperature or SensorChannel.Pressure or
                SensorChannel.DirectionalLight;
            if(needsEnvironment&&!sampled[bodyIndex])
            {samples[bodyIndex]=environment.Sample(samplePosition,depth);sampled[bodyIndex]=true;}
            EnvironmentSample sample=samples[bodyIndex];
            double raw=sensor.Channel switch
            {
                SensorChannel.ChemicalResource=>Math.Clamp((sample.Minerals*region.PhotosyntheticExpression+
                    (sample.ProducerBiomass+sample.EdibleOrganics*genome.Metabolism.AnimalFoodAffinity)*region.FeedingExpression*region.DigestiveExpression+
                    sample.Detritus*region.DecomposerExpression)/2.0,0.0,1.0),
                SensorChannel.ContactPressure=>Math.Clamp(contactPressure,0.0,1.0),
                SensorChannel.InternalEnergy=>Math.Clamp(region.Energy/Math.Max(0.1,config.MaximumEnergy),0.0,1.0),
                SensorChannel.Hydration=>Math.Clamp(region.Water/Math.Max(0.1,
                    RegionalPhysiology.WaterCapacity(region,genome.Metabolism)),0.0,1.0),
                SensorChannel.AmbientLight=>Math.Clamp(sample.Light,0.0,1.0),
                SensorChannel.Temperature=>Math.Clamp((sample.Temperature-5.0)/35.0,-1.0,1.0),
                SensorChannel.Pressure=>Math.Clamp((sample.Pressure-1.0)/10.0,0.0,1.0),
                SensorChannel.DirectionalLight=>DirectionalLight(body,geometry,environment,sample,samplePosition,depth,
                    headingRadians,sensor,config),
                _=>0.0
            };
            double availability=Math.Clamp(region.SensoryExpression*geometry.ExposureFraction*
                geometry.SignalTransportEfficiency,0.0,1.0);
            if(sensor.Channel==SensorChannel.DirectionalLight)
                availability*=Math.Clamp(sourceGene.LightReactivity*(0.25+0.75*sensor.DirectionalSelectivity),0.0,1.0);
            // Four directional probes are represented by one bounded world query here.
            // Longer reach retains a higher tissue/processing cost instead of being free.
            double probeCount=sensor.Channel is SensorChannel.ChemicalResource or SensorChannel.DirectionalLight
                ?3.0+Math.Clamp(sensor.Range/12.0,0.0,1.0):1.0;
            double requested=config.SensorEnergyPerSlotPerSecond*deltaSeconds*availability*probeCount*
                (0.30+0.70*Math.Abs(raw));
            double paid=body.ConsumeRegionEnergy(region.RegionId,requested);
            energySpent+=paid;
            double paidFraction=requested>1e-12?Math.Clamp(paid/requested,0.0,1.0):0.0;
            double effective=raw*sensor.Gain*availability*paidFraction;
            // An unpaid receptor keeps only a brief physical response tail; it cannot
            // provide a persistent signal without continuing to spend energy.
            double responseRate=paidFraction>1e-6?sensor.ResponseRate:Math.Max(4.0,sensor.ResponseRate);
            double blend=1.0-Math.Exp(-responseRate*deltaSeconds);
            sensorState[index]=Math.Clamp(sensorState[index]+((effective-sensorState[index])*blend),-1.0,1.0);
            controllerNodeInputs[sensor.TargetControllerNodeIndex]+=sensorState[index];
            if(availability*paidFraction>0.05)
            {active++;if(sensor.Channel==SensorChannel.DirectionalLight)activeVisual++;}
            switch(sensor.Channel)
            {
                case SensorChannel.ChemicalResource:
                    ControllerNodeGene target=genome.ControllerNodes[sensor.TargetControllerNodeIndex];
                    double controlReach=Math.Clamp((Math.Abs(target.ContractionOutputWeight)+
                        Math.Abs(target.LateralContractionOutputWeight)+0.5*Math.Abs(target.VerticalContractionOutputWeight))/1.5,0.0,1.0);
                    double chemicalMotorAccess=availability*paidFraction*Math.Min(1.0,Math.Abs(sensor.Gain))*controlReach;
                    chemical+=sensorState[index];
                    chemicalAccess+=chemicalMotorAccess;
                    chemicalRangeWeighted+=sensor.Range*chemicalMotorAccess;
                    chemicalAccessWeight+=chemicalMotorAccess;
                    chemicalCount++;break;
                case SensorChannel.ContactPressure:contact+=sensorState[index];contactCount++;break;
                case SensorChannel.AmbientLight:light+=sensorState[index];lightCount++;break;
                case SensorChannel.DirectionalLight:
                    ControllerNodeGene visualTarget=genome.ControllerNodes[sensor.TargetControllerNodeIndex];
                    double visualMotorReach=Math.Clamp((Math.Abs(visualTarget.ContractionOutputWeight)+
                        Math.Abs(visualTarget.LateralContractionOutputWeight)+
                        0.5*Math.Abs(visualTarget.VerticalContractionOutputWeight))/1.5,0.0,1.0);
                    double localAngle=Math.Atan2(geometry.Direction.Y,geometry.Direction.X)+
                        sensor.DirectionOffsetRadians;
                    double effectiveVisual=sensorState[index]*visualMotorReach;
                    vision+=sensorState[index];
                    visualForward+=effectiveVisual*Math.Cos(localAngle);
                    visualLateral+=effectiveVisual*Math.Sin(localAngle);
                    visualAccess+=availability*paidFraction*Math.Min(1.0,Math.Abs(sensor.Gain))*visualMotorReach;
                    visionCount++;break;
                case SensorChannel.Temperature:temperature+=sensorState[index];temperatureCount++;break;
                case SensorChannel.Pressure:pressure+=sensorState[index];pressureCount++;break;
            }
        }
        return new TissueSensingResult(energySpent,active,
            Mean(chemical,chemicalCount),Mean(contact,contactCount),Mean(chemicalAccess,chemicalCount),
            Mean(light,lightCount),activeVisual,Mean(vision,visionCount),
            Mean(temperature,temperatureCount),Mean(pressure,pressureCount),
            chemicalAccessWeight>1e-12?chemicalRangeWeighted/chemicalAccessWeight:0.0,
            Mean(visualForward,visionCount),Mean(visualLateral,visionCount),
            Mean(visualAccess,visionCount));
    }

    private static double DirectionalLight(DevelopingBody body,BodyFunctionalGeometry sourceRegion,
        IEnvironmentField environment,EnvironmentSample sourceSample,Vector2 source,float depth,
        double heading,SensorGene sensor,SimulationConfig config)
    {
        double localAngle=Math.Atan2(sourceRegion.Direction.Y,sourceRegion.Direction.X)+sensor.DirectionOffsetRadians;
        Vector2 localDirection=new((float)Math.Cos(localAngle),(float)Math.Sin(localAngle));
        foreach(BodyFunctionalGeometry other in body.FunctionalGeometry)
        {
            if(other.RegionId==sourceRegion.RegionId)continue;
            Vector2 delta=other.LocalCenter-sourceRegion.LocalCenter;
            double along=Vector2.Dot(delta,localDirection);
            if(along<=0.03||along>=sensor.Range)continue;
            double perpendicular=Math.Abs(delta.X*localDirection.Y-delta.Y*localDirection.X);
            if(perpendicular<Math.Max(0.04,other.ExchangeDistance))return 0.0;
        }
        double baseAngle=localAngle+heading;
        Vector2 direction=new((float)Math.Cos(baseAngle),(float)Math.Sin(baseAngle));
        Vector2 probe=SphericalWorld.OffsetPosition(
            source,direction*(float)sensor.Range,config.WorldSize);
        double eyeElevation=sourceSample.WaterDepth>0
            ?sourceSample.WaterSurface-depth:sourceSample.TerrainHeight+0.05;
        for(int step=1;step<=3;step++)
        {
            Vector2 rayPoint=SphericalWorld.OffsetPosition(
                source,direction*(float)(sensor.Range*step/4.0),config.WorldSize);
            EnvironmentSample raySample=environment.Sample(rayPoint,depth);
            if(raySample.TerrainHeight>eyeElevation+0.015)return 0.0;
        }
        double near=sourceSample.Light;
        double far=environment.Sample(probe,depth).Light;
        // Positive luminance plus signed local contrast: one bounded ray, no camera/image.
        return Math.Clamp(0.65*far+0.35*(far-near),-1.0,1.0);
    }

    private static double Mean(double sum,int count)=>count>0?Math.Clamp(sum/count,-1.0,1.0):0.0;
}
