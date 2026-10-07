using System.Text.Json;

namespace Aion2DpsMeter.Core.Data;

public sealed record NpcInfo(string Name, bool IsBoss, bool IsDummy, int DungeonId);

/// <summary>
/// Static lookup tables: skill names, NPCs (with boss / training dummy flags), dungeons and the
/// curated list of damage-over-time skills. Tables come from A2Tools (GPL-3.0).
/// </summary>
public sealed class GameData
{
    private static readonly string[] DummyNames = ["Training Scarecrow", "Punching Bag"];

    /// <summary>Training scarecrows known before the NPC table flagged dummies.</summary>
    private static readonly HashSet<int> KnownDummyCodes =
    [
        2300229, 2300919, 2310229, 2310919, 2320229, 2320919,
        2400032, 2400035, 2400392, 2500075, 2500076, 2701376,
        2090773, 2702605,
    ];

    public Dictionary<int, string> Skills { get; } = new();
    public Dictionary<int, NpcInfo> Npcs { get; } = new();
    public Dictionary<int, string> Dungeons { get; } = new();
    public HashSet<int> DotSkillIds { get; } = new();

    public static GameData Empty { get; } = new();

    public static GameData Load(string dataDir, string language = "en")
    {
        var data = new GameData();
        data.LoadSkills(Path.Combine(dataDir, $"skills.{language}.json"));
        data.LoadNpcs(Path.Combine(dataDir, $"npcs.{language}.json"));
        data.LoadDungeons(Path.Combine(dataDir, $"dungeons.{language}.json"));
        data.LoadDots(Path.Combine(dataDir, "dot_skill_ids.json"));
        return data;
    }

    public string SkillName(int code)
    {
        if (Skills.TryGetValue(code, out var name))
            return name;
        if (code >= 3_000_000 && code <= 3_099_999 && Skills.TryGetValue(code * 10 + 1, out name))
            return name;
        return string.Empty;
    }

    public bool HasSkill(int code) => Skills.ContainsKey(code);

    public NpcInfo? Npc(int code) => Npcs.GetValueOrDefault(code);

    public bool IsBoss(int code) => Npcs.TryGetValue(code, out var n) && n.IsBoss;

    public bool IsTrainingDummy(int code) =>
        KnownDummyCodes.Contains(code) || (Npcs.TryGetValue(code, out var n) && n.IsDummy);

    public string DungeonName(int id) => Dungeons.GetValueOrDefault(id) ?? string.Empty;

    private void LoadSkills(string path)
    {
        if (!File.Exists(path))
            return;
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
        foreach (var (key, value) in map)
        {
            if (int.TryParse(key, out int code))
                Skills[code] = value;
        }
    }

    private void LoadNpcs(string path)
    {
        if (!File.Exists(path))
            return;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!int.TryParse(prop.Name, out int code))
                continue;
            var v = prop.Value;
            if (v.ValueKind == JsonValueKind.Object)
            {
                string name = v.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                bool boss = v.TryGetProperty("isBoss", out var b) && b.ValueKind == JsonValueKind.True;
                bool dummy = (v.TryGetProperty("isDummy", out var d) && d.ValueKind == JsonValueKind.True)
                    || DummyNames.Any(name.Contains);
                int dungeon = v.TryGetProperty("dungeonId", out var dg) && dg.TryGetInt32(out int dgi) ? dgi : 0;
                Npcs[code] = new NpcInfo(name, boss, dummy, dungeon);
            }
            else if (v.ValueKind == JsonValueKind.String)
            {
                string name = v.GetString() ?? "";
                Npcs[code] = new NpcInfo(name, false, DummyNames.Any(name.Contains), 0);
            }
        }
    }

    private void LoadDungeons(string path)
    {
        if (!File.Exists(path))
            return;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (int.TryParse(prop.Name, out int id)
                && prop.Value.ValueKind == JsonValueKind.Object
                && prop.Value.TryGetProperty("name", out var n))
            {
                Dungeons[id] = n.GetString() ?? "";
            }
        }
    }

    private void LoadDots(string path)
    {
        if (!File.Exists(path))
            return;
        foreach (int id in JsonSerializer.Deserialize<int[]>(File.ReadAllText(path)) ?? [])
            DotSkillIds.Add(id);
    }
}
