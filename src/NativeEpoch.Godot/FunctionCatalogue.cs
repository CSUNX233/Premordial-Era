using System.Text.Json;

namespace NativeEpoch.Godot;

public sealed class ObservedFunction
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Guidance { get; set; } = "";
    public bool Favorite { get; set; }
    public ulong FirstSeed { get; set; }
    public double FirstTime { get; set; }
    public int FirstGeneration { get; set; }
    public string FirstGenome { get; set; } = "";
    public string WorldId { get; set; } = "";
    public ulong RepresentativeId { get; set; }
    public string RepresentativeGenome { get; set; } = "";
    public int RepresentativeGeneration { get; set; }
    public double LastTime { get; set; }
    public List<string> ExampleGenomes { get; set; } = [];
}

/// <summary>Observation-only archive. It never mutates a simulation or genome.</summary>
public sealed class FunctionCatalogue
{
    public const string SavePath = "user://observed-functions-v1.json";
    private const int MaximumEntries = 256;
    private readonly Dictionary<string,ObservedFunction> _entries = new(StringComparer.Ordinal);
    private bool _dirty;
    private readonly bool _allowPersistence;
    private readonly string _savePath;
    public IReadOnlyCollection<ObservedFunction> Entries => _entries.Values;
    public string WorldId { get; private set; } = "";
    public string StorageStatus { get; private set; } = "";

    public FunctionCatalogue(bool loadFromDisk=true, bool allowPersistence=true, string savePath=SavePath)
    {
        _allowPersistence=allowPersistence;
        _savePath=savePath;
        if(!loadFromDisk||!global::Godot.FileAccess.FileExists(_savePath))return;
        try
        {
            using var file=global::Godot.FileAccess.Open(_savePath,global::Godot.FileAccess.ModeFlags.Read);
            if(file is null||file.GetLength()>1_048_576)
            { StorageStatus="图鉴存档无法读取或过大，当前使用空图鉴。";return; }
            RestoreJson(file.GetAsText());
        }
        catch(Exception error) when(error is JsonException or System.IO.IOException or ArgumentException)
        { StorageStatus="图鉴存档读取失败："+error.Message; }
    }

    public void BeginWorld()=>WorldId=Guid.NewGuid().ToString("N");

    public void Observe(string key,string name,string evidence,string guidance,
        ulong seed,double time,int generation,ulong organismId,ulong genomeFingerprint)
    {
        if(string.IsNullOrWhiteSpace(key)||!double.IsFinite(time))return;
        string genome=genomeFingerprint.ToString("X16");
        if(!_entries.TryGetValue(key,out ObservedFunction? entry))
        {
            if(_entries.Count>=MaximumEntries)return;
            entry=new ObservedFunction
            {
                Key=key,Name=name,FirstSeed=seed,FirstTime=time,FirstGeneration=generation,
                FirstGenome=genome
            };
            _entries.Add(key,entry);
        }
        entry.Evidence=evidence;entry.Guidance=guidance;entry.WorldId=WorldId;
        entry.RepresentativeId=organismId;entry.RepresentativeGenome=genome;
        entry.RepresentativeGeneration=generation;entry.LastTime=time;
        if(entry.ExampleGenomes.Count<32&&!entry.ExampleGenomes.Contains(genome))
            entry.ExampleGenomes.Add(genome);
        _dirty=true;
    }

    public void ToggleFavorite(string key)
    {
        if(!_entries.TryGetValue(key,out ObservedFunction? entry))return;
        entry.Favorite=!entry.Favorite;_dirty=true;
    }

    public string ExportJson()=>JsonSerializer.Serialize(_entries.Values.ToArray(),
        new JsonSerializerOptions{WriteIndented=true});

    public void RestoreJson(string json)
    {
        ObservedFunction[] records=JsonSerializer.Deserialize<ObservedFunction[]>(json)??[];
        _entries.Clear();
        foreach(ObservedFunction entry in records.Take(MaximumEntries))
        {
            if(entry is null||string.IsNullOrWhiteSpace(entry.Key)||entry.Key.Length>160)continue;
            entry.Name=Trim(entry.Name,160);entry.Evidence=Trim(entry.Evidence,1200);
            entry.Guidance=Trim(entry.Guidance,800);
            entry.ExampleGenomes=(entry.ExampleGenomes??[]).Where(value=>value is not null)
                .Take(32).Select(value=>Trim(value,32)).ToList();
            _entries[entry.Key]=entry;
        }
        _dirty=false;
    }

    public void Save()
    {
        if(!_dirty||!_allowPersistence)return;
        try
        {
            // Use a temporary sibling so interruption cannot truncate favorites.
            string temporary=_savePath+".tmp";
            using(var file=global::Godot.FileAccess.Open(temporary,global::Godot.FileAccess.ModeFlags.Write))
            {
                if(file is null){StorageStatus="图鉴保存失败："+global::Godot.FileAccess.GetOpenError();return;}
                file.StoreString(ExportJson());file.Flush();
            }
            System.IO.File.Move(global::Godot.ProjectSettings.GlobalizePath(temporary),
                global::Godot.ProjectSettings.GlobalizePath(_savePath),overwrite:true);
            _dirty=false;StorageStatus="图鉴与收藏已保存到本机。";
        }
        catch(Exception error) when(error is JsonException or System.IO.IOException)
        { StorageStatus="图鉴保存失败："+error.Message; }
    }

    private static string Trim(string? value,int maximum)=>(value??"").Length>maximum
        ?value![..maximum]:value??"";
}
