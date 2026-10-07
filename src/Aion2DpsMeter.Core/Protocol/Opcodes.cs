using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion2DpsMeter.Core.Protocol;

/// <summary>
/// The two-byte opcodes the parser keys on. Game patches move these (the June 2026 update
/// shifted the <c>xx 36</c> spawn/death family by +1), so every entry is a list: old and new
/// values can be accepted side by side, and an <c>opcodes.json</c> next to the executable
/// overrides the defaults without a new release.
/// </summary>
public sealed class Opcodes
{
    /// <summary>Direct damage records (<c>04 38</c>).</summary>
    public List<string> Damage { get; set; } = ["04 38"];
    /// <summary>Damage-over-time and heal ticks (<c>05 38</c>).</summary>
    public List<string> Dot { get; set; } = ["05 38"];
    /// <summary>NPC / summon spawn (<c>40 36</c> before June 2026, <c>41 36</c> after).</summary>
    public List<string> MobSpawn { get; set; } = ["40 36", "41 36"];
    /// <summary>Another player's spawn and identity (<c>44 36</c> / <c>45 36</c>).</summary>
    public List<string> PlayerSpawn { get; set; } = ["44 36", "45 36"];
    /// <summary>The local player's own identity record (<c>33 36</c>).</summary>
    public List<string> SelfRecord { get; set; } = ["33 36"];
    /// <summary>Entity death (<c>41 36</c> before June 2026, <c>42 36</c> after). Gated by flag 3.</summary>
    public List<string> Death { get; set; } = ["41 36", "42 36"];
    /// <summary>Teleport; with entity id 0 it is the local player entering a zone.</summary>
    public List<string> ZoneChange { get; set; } = ["23 36"];
    /// <summary>Summon ownership / kill record carrying the owner's name (<c>04 8D</c>).</summary>
    public List<string> SummonOwnership { get; set; } = ["04 8D"];
    /// <summary>HP / MP update (<c>1B 92</c>).</summary>
    public List<string> HpUpdate { get; set; } = ["1B 92"];
    /// <summary>Party roster broadcast (<c>02 97</c>).</summary>
    public List<string> PartyRoster { get; set; } = ["02 97"];
    /// <summary>A record the server sends only about you and your party (<c>06 38</c>).</summary>
    public List<string> PartyScope { get; set; } = ["06 38"];
    /// <summary>
    /// Record terminator bytes (<c>?? 00 36</c>) that mark game traffic. Used to find the game
    /// connection before a port is locked, and by the nickname scans.
    /// </summary>
    public List<string> Signatures { get; set; } = ["0E 00 36", "06 00 36"];

    [JsonIgnore] internal byte[][] DamageB { get; private set; } = [];
    [JsonIgnore] internal byte[][] DotB { get; private set; } = [];
    [JsonIgnore] internal byte[][] MobSpawnB { get; private set; } = [];
    [JsonIgnore] internal byte[][] PlayerSpawnB { get; private set; } = [];
    [JsonIgnore] internal byte[][] SelfRecordB { get; private set; } = [];
    [JsonIgnore] internal byte[][] DeathB { get; private set; } = [];
    [JsonIgnore] internal byte[][] ZoneChangeB { get; private set; } = [];
    [JsonIgnore] internal byte[][] SummonOwnershipB { get; private set; } = [];
    [JsonIgnore] internal byte[][] HpUpdateB { get; private set; } = [];
    [JsonIgnore] internal byte[][] PartyRosterB { get; private set; } = [];
    [JsonIgnore] internal byte[][] PartyScopeB { get; private set; } = [];
    [JsonIgnore] public byte[][] SignaturesB { get; private set; } = [];

    /// <summary>First bytes of every spawn opcode (mob and player), used by the embedded scans.</summary>
    [JsonIgnore] internal HashSet<byte> SpawnLeadBytes { get; private set; } = [];

    public static Opcodes Default { get; } = new Opcodes().Compile();

    public Opcodes Compile()
    {
        DamageB = Parse(Damage);
        DotB = Parse(Dot);
        MobSpawnB = Parse(MobSpawn);
        PlayerSpawnB = Parse(PlayerSpawn);
        SelfRecordB = Parse(SelfRecord);
        DeathB = Parse(Death);
        ZoneChangeB = Parse(ZoneChange);
        SummonOwnershipB = Parse(SummonOwnership);
        HpUpdateB = Parse(HpUpdate);
        PartyRosterB = Parse(PartyRoster);
        PartyScopeB = Parse(PartyScope);
        SignaturesB = Parse(Signatures);
        SpawnLeadBytes = MobSpawnB.Concat(PlayerSpawnB).Select(b => b[0]).ToHashSet();
        return this;
    }

    public static Opcodes Load(string path)
    {
        if (!File.Exists(path))
            return Default;
        var loaded = JsonSerializer.Deserialize<Opcodes>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        return (loaded ?? new Opcodes()).Compile();
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>Whether any of <paramref name="codes"/> sits at <paramref name="at"/>.</summary>
    internal static bool At(ReadOnlySpan<byte> data, int at, byte[][] codes)
    {
        foreach (var code in codes)
        {
            if (at >= 0 && at + code.Length <= data.Length && data.Slice(at, code.Length).SequenceEqual(code))
                return true;
        }
        return false;
    }

    public static bool ContainsAny(ReadOnlySpan<byte> data, byte[][] needles)
    {
        foreach (var n in needles)
        {
            if (data.IndexOf(n) >= 0)
                return true;
        }
        return false;
    }

    private static byte[][] Parse(List<string> codes) =>
        codes.Select(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(h => byte.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                .ToArray())
            .Where(b => b.Length > 0)
            .ToArray();
}
