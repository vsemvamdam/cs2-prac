using System.Text.Json;
using System.Text.Json.Serialization;

namespace CS2Prac;

/// <summary>Одна раскидка: где стоять, куда смотреть, куда должна упасть граната.</summary>
public sealed class Lineup
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Desc { get; set; } = "";
    /// <summary>smoke | molotov | flash | he | decoy</summary>
    public string Type { get; set; } = "smoke";
    /// <summary>T | CT | ""</summary>
    public string Side { get; set; } = "";
    public bool JumpThrow { get; set; }
    public float[] Stand { get; set; } = new float[3];
    /// <summary>Углы взгляда игрока (pitch, yaw, roll) для броска.</summary>
    public float[] Aim { get; set; } = new float[3];
    public float[]? AimPoint { get; set; }
    public float[]? Dest { get; set; }
    public float Threshold { get; set; } = 80f;
    /// <summary>true — раскидка, которую игрок записал сам.</summary>
    public bool Mine { get; set; }
}

/// <summary>Запомненный бросок: откуда и с какой скоростью вылетела граната.</summary>
public sealed class ThrowRef
{
    public string Type { get; set; } = "smoke";
    public float[] Pos { get; set; } = new float[3];
    public float[] Ang { get; set; } = new float[3];
    public float[] Vel { get; set; } = new float[3];
    public float[] AngVel { get; set; } = new float[3];
    public ushort ItemIndex { get; set; }
    public float[] PlayerPos { get; set; } = new float[3];
    public float[] PlayerAng { get; set; } = new float[3];
    public bool Jumped { get; set; }
    public string Place { get; set; } = "";
    // Физика снаряда в момент броска: у гранаты, созданной заново, по умолчанию другие значения.
    public float Gravity { get; set; }
    public float ActualGravity { get; set; }
    public float Friction { get; set; }
    public float Elasticity { get; set; }
    public float[]? Landed { get; set; }
}

public sealed class LineupCheck
{
    /// <summary>ok | miss | untested</summary>
    public string Status { get; set; } = "untested";
    public float LastDistance { get; set; } = -1;
    public string CheckedBuild { get; set; } = "";
    public DateTime CheckedAt { get; set; }
    public ThrowRef? Ref { get; set; }
}

public sealed class MapChecks
{
    public Dictionary<string, LineupCheck> Items { get; set; } = new();
}

public sealed class SkinEntry
{
    public int Paint { get; set; }
    public string En { get; set; } = "";
    public string Ru { get; set; } = "";
    public bool Legacy { get; set; }
    public bool Popular { get; set; }
}

public sealed class WeaponEntry
{
    public int Def { get; set; }
    public string Class { get; set; } = "";
    public string Name { get; set; } = "";
    public string NameRu { get; set; } = "";
    public List<SkinEntry> Skins { get; set; } = new();
}

public sealed class SkinsDb
{
    public Dictionary<string, List<WeaponEntry>> Categories { get; set; } = new();
    public List<WeaponEntry> Knives { get; set; } = new();

    [JsonIgnore] public Dictionary<int, WeaponEntry> ByDef { get; } = new();

    public void Index()
    {
        foreach (var w in Categories.Values.SelectMany(x => x).Concat(Knives))
            ByDef[w.Def] = w;
    }
}

public sealed class AgentEntry
{
    public int Team { get; set; }
    public string Model { get; set; } = "";
    public string En { get; set; } = "";
    public string Ru { get; set; } = "";
}

/// <summary>Выбор игрока: оружие, скины, нож, агент. Хранится по SteamID.</summary>
public sealed class Loadout
{
    public int Primary { get; set; }
    public int Secondary { get; set; }
    public Dictionary<int, int> Paints { get; set; } = new();
    public int KnifeDef { get; set; }
    public string AgentT { get; set; } = "";
    public string AgentCT { get; set; } = "";
    public bool ShowAllLabels { get; set; } = true;
    /// <summary>Пункт меню хоть раз выбран клавишей-цифрой (не «!N» в чате и не выстрелом).</summary>
    public bool DigitKeysOk { get; set; }
}

public static class Json
{
    public static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Opts) : null;
        }
        catch (Exception e)
        {
            PLog.Info($"Не удалось прочитать {path}: {e.Message}");
            return null;
        }
    }

    public static void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Opts));
            File.Move(tmp, path, true);
        }
        catch (Exception e)
        {
            PLog.Info($"Не удалось записать {path}: {e.Message}");
        }
    }
}

/// <summary>Журнал плагина: пишет в лог CounterStrikeSharp (addons/counterstrikesharp/logs).</summary>
public static class PLog
{
    public static Microsoft.Extensions.Logging.ILogger? L;

    public static void Info(string s)
    {
        if (L != null) Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(L, "{Msg}", s);
        else Console.WriteLine("[CS2Prac] " + s);
    }
}
