using System.Collections.Concurrent;
using Aion2DpsMeter.Core.Data;

namespace Aion2DpsMeter.Core.Combat;

public sealed class CombatOptions
{
    /// <summary>A fight ends after this long without damage from you or your party.</summary>
    public int IdleTimeoutMs { get; set; } = 30_000;
    /// <summary>After every boss of a fight dies, wait this long for the last DoT ticks, then end it.</summary>
    public int BossKillGraceMs { get; set; } = 3_000;
    /// <summary>After every target of a trash fight dies, end it unless a new target is hit within this time.</summary>
    public int TrashClearGraceMs { get; set; } = 5_000;
    /// <summary>In a boss fight, count only damage dealt to bosses (adds are ignored).</summary>
    public bool BossDamageOnly { get; set; } = true;
    /// <summary>When a party roster is known, show only party members.</summary>
    public bool PartyOnly { get; set; }
}

public sealed record LocalProfile(int? EntityId, string? Name, JobClass Job, int? Level, ushort ServerId);

/// <summary>
/// Everything the parser learns: who is who (names, players, summons, mobs, bosses) and the
/// damage and healing of the current fight. Splits combat into encounters automatically:
/// a boss pull starts a fresh fight, a boss kill or a cleared pack ends it, as do an idle gap,
/// a zone change and a manual reset. Thread-safe; ended fights are raised through
/// <see cref="EncounterEnded"/> outside the lock.
/// </summary>
public sealed class CombatStore
{
    private const int MaxEntityId = 9_999_999;
    private const int ZoneResetLullMs = 1_500;
    private const int ZoneResetDebounceMs = 4_000;
    private const int RosterBindEvery = 64;

    // Records a Spiritmaster's spirit and its owner send each other: links, not damage.
    private static bool IsSpiritToOwner(int skill) => skill >= 16_990_000 && skill <= 16_999_999;
    private static bool IsOwnerToSpirit(int skill) => skill >= 16_770_000 && skill <= 16_779_999;

    private readonly object _lock = new();
    private readonly GameData _data;
    private readonly ConcurrentQueue<EncounterRecord> _ended = new();

    private readonly Dictionary<int, string> _nicknames = new();
    private readonly HashSet<int> _authoritativeNames = new();
    private readonly HashSet<int> _knownPlayers = new();
    private readonly Dictionary<int, int> _summonOwner = new();
    private readonly HashSet<int> _confirmedSummons = new();
    private readonly HashSet<int> _summonSpawns = new();
    private readonly Dictionary<int, int> _mobCode = new();
    private readonly HashSet<int> _bosses = new();
    private readonly HashSet<int> _dummies = new();
    private readonly Dictionary<int, int> _mobMaxHp = new();
    private readonly Dictionary<int, int> _mobCurHp = new();
    private readonly HashSet<int> _dead = new();
    private readonly HashSet<int> _lowIds = new();
    private readonly Dictionary<int, JobClass> _jobs = new();
    private readonly Dictionary<string, PartyMember> _party = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ushort> _playerServers = new(StringComparer.Ordinal);

    private int? _localId;
    private string? _localName;
    private LocalProfile? _localProfile;
    private int _dungeonId;
    /// <summary>Loot owners named so far: name → (entity id, mobs they were named for).</summary>
    private readonly Dictionary<string, (int Id, HashSet<int> Kills)> _lootOwners = new(StringComparer.Ordinal);
    /// <summary>Entities the server sent a <c>06 38</c> record about: you and your party, never strangers.</summary>
    private readonly HashSet<int> _partyScope = new();
    private readonly Dictionary<int, int> _partyScopeCounts = new();
    /// <summary>Every entity damaged by you or your party this session (bounded).</summary>
    private readonly HashSet<int> _foughtTargets = new();

    private Encounter? _current;
    private EncounterRecord? _lastEnded;
    // Null = has not happened. (long.MinValue overflowed in "now - then" and broke the checks.)
    private long? _lastDamageMs;
    private long? _lastZoneResetMs;
    private int _damageSinceRosterBind;

    public CombatOptions Options { get; }

    /// <summary>Raised when a fight ends (on the thread that ended it, outside the store's lock).</summary>
    public event Action<EncounterRecord>? EncounterEnded;

    /// <summary>Raised when the meter learns (or changes its mind about) who you are: id, name, source.</summary>
    public event Action<int, string?, string>? LocalIdentityChanged;
    private readonly ConcurrentQueue<(int, string?, string)> _identityEvents = new();

    public CombatStore(GameData data, CombatOptions? options = null)
    {
        _data = data;
        Options = options ?? new CombatOptions();
    }

    // ───── encounter lifecycle ─────

    /// <summary>Ends fights whose idle timeout or kill grace has passed. Call regularly with the current clock.</summary>
    public void Tick(long nowMs)
    {
        lock (_lock)
            CheckEnd(nowMs);
        FlushEnded();
    }

    /// <summary>Ends the current fight now (it is saved like any other) and clears the meter.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            EndCurrent();
            _lastEnded = null;
        }
        FlushEnded();
    }

    /// <summary>The fight in progress, or the last one that ended; null before any fight.</summary>
    public EncounterRecord? GetDisplayRecord()
    {
        lock (_lock)
            return _current is { IsEmpty: false } c ? BuildRecord(c, inProgress: true) : _lastEnded;
    }

    public bool InCombat
    {
        get { lock (_lock) return _current is { IsEmpty: false }; }
    }

    private void CheckEnd(long now)
    {
        if (_current is not { } c)
            return;
        if (c.PendingEndMs is long end && now >= end)
            EndCurrent();
        else if (now - c.LastMs > Options.IdleTimeoutMs)
            EndCurrent();
    }

    private void EndCurrent()
    {
        if (_current is not { } c)
            return;
        _current = null;
        if (c.IsEmpty)
            return;
        var record = BuildRecord(c, inProgress: false);
        if (record.TotalDamage <= 0)
            return;
        _lastEnded = record;
        _ended.Enqueue(record);
    }

    private void FlushEnded()
    {
        while (_ended.TryDequeue(out var r))
            EncounterEnded?.Invoke(r);
        while (_identityEvents.TryDequeue(out var id))
            LocalIdentityChanged?.Invoke(id.Item1, id.Item2, id.Item3);
    }

    public void NoteZoneChange(long nowMs)
    {
        lock (_lock)
        {
            // A teleport mid-fight (a boss pull) must not end it; real zone loads follow a lull.
            if ((_lastDamageMs is long dmg && nowMs - dmg < ZoneResetLullMs)
                || (_lastZoneResetMs is long reset && nowMs - reset < ZoneResetDebounceMs))
                return;
            if (_current is null)
                return;
            _lastZoneResetMs = nowMs;
            EndCurrent();
        }
        FlushEnded();
    }

    public void MarkDead(int entityId, long nowMs)
    {
        lock (_lock)
        {
            _dead.Add(entityId);
            if (_current is not { } c || !c.Targets.TryGetValue(entityId, out var t))
                return;
            t.Killed = true;
            if (c.HasBoss)
            {
                bool allBossesDead = c.Targets.Values.Where(x => _bosses.Contains(x.Id)).All(x => x.Killed);
                if (allBossesDead)
                {
                    c.PendingEndMs = nowMs + Options.BossKillGraceMs;
                    c.PendingIsBossKill = true;
                }
            }
            else if (c.Targets.Values.All(x => x.Killed))
            {
                c.PendingEndMs = nowMs + Options.TrashClearGraceMs;
            }
        }
    }

    // ───── damage & healing ─────

    public void AppendDamage(DamageEvent e)
    {
        lock (_lock)
        {
            AppendDamageLocked(e);
        }
        FlushEnded();
    }

    private void AppendDamageLocked(DamageEvent e)
    {
        int skill = e.SkillCode, actor = e.ActorId, target = e.TargetId;

        if (IsSpiritToOwner(skill))
        {
            LinkSummonLocked(actor, target);
            return;
        }
        if (IsOwnerToSpirit(skill))
        {
            LinkSummonLocked(target, actor);
            return;
        }

        // An NPC using an NPC skill is the enemy acting; not counted.
        if (_mobCode.ContainsKey(actor) && !_summonOwner.ContainsKey(actor) && skill >= 1_000_000 && skill <= 9_999_999)
            return;

        // Class-band skills mean a player, unless the entity spawned as a summon or has an owner.
        if (JobClasses.IsPlayerSkill(skill)
            && !_confirmedSummons.Contains(actor)
            && !_summonSpawns.Contains(actor)
            && !_summonOwner.ContainsKey(actor)
            && _knownPlayers.Add(actor))
        {
            GuessLocalFromScope();
        }

        int resolvedActor = Resolve(actor);
        int resolvedTarget = Resolve(target);

        if (_knownPlayers.Contains(resolvedTarget))
        {
            // Player on player is healing or a buff, not damage.
            if (_knownPlayers.Contains(resolvedActor) && _current is { IsEmpty: false } c && e.TotalDamage > 0)
                c.AddHeal(actor, skill, false, e.TotalDamage);
            // Otherwise it is damage a player took, which the meter does not count.
            return;
        }

        var job = JobClasses.FromSkill(skill);
        if (job != JobClass.Unknown)
            _jobs.TryAdd(actor, job);

        long now = e.TimestampMs;
        CheckEnd(now);

        bool ours = IsOurs(resolvedActor);
        bool bossTarget = _bosses.Contains(target);

        if (_current is { } cur && bossTarget && !cur.HasBoss && !cur.IsEmpty && ours)
        {
            // The boss was pulled: what came before is a separate (trash) fight.
            EndCurrent();
        }

        if (_current is null)
        {
            if (!ours)
                return; // strangers fighting nearby do not start a fight on your meter
            _current = new Encounter(now) { DungeonId = _dungeonId };
        }

        var enc = _current;
        if (bossTarget)
            enc.HasBoss = true;

        if (!enc.Targets.TryGetValue(target, out var t))
        {
            enc.Targets[target] = t = new TargetAgg(target, now);
            // A new target while a cleared trash pack is about to end: the pull goes on.
            if (enc.PendingEndMs is not null && !enc.PendingIsBossKill && ours)
                enc.PendingEndMs = null;
        }
        if (_dead.Contains(target) && !t.Killed && t.Actors.Count == 0)
            _dead.Remove(target); // the id was reused by a new entity

        t.LastMs = Math.Max(t.LastMs, now);
        t.Total += e.TotalDamage;
        if (!t.Actors.TryGetValue(actor, out var a))
            t.Actors[actor] = a = new ActorAgg();
        a.Add(e);

        if (ours)
        {
            enc.LastMs = Math.Max(enc.LastMs, now);
            _lastDamageMs = now;
            if (_foughtTargets.Count >= 10_000)
                _foughtTargets.Clear();
            _foughtTargets.Add(target);
        }

        if (++_damageSinceRosterBind >= RosterBindEvery)
        {
            _damageSinceRosterBind = 0;
            BindRosterNamesByClass();
        }
    }

    public void AppendHeal(int actorId, int skill, long amount, bool isHot, long nowMs)
    {
        lock (_lock)
        {
            if (_current is { IsEmpty: false } c && amount > 0)
                c.AddHeal(actorId, skill, isHot, amount);
        }
    }

    /// <summary>Whether this actor belongs to you: you, your party, or anyone while you are not identified yet.</summary>
    private bool IsOurs(int resolvedActor)
    {
        if (_localId is null)
            return _knownPlayers.Contains(resolvedActor);
        if (resolvedActor == _localId)
            return true;
        if (_party.Count >= 2 && _nicknames.TryGetValue(resolvedActor, out var name))
            return _party.ContainsKey(name);
        // No party: you are solo; another player's damage is theirs.
        return false;
    }

    // ───── entities ─────

    public void AppendMob(int id, int npcCode)
    {
        lock (_lock)
        {
            _mobCode[id] = npcCode;
            if (_data.IsBoss(npcCode))
                _bosses.Add(id);
            else
                _bosses.Remove(id);
            if (_data.IsTrainingDummy(npcCode))
                _dummies.Add(id);
            // A spawn arriving after player-band damage: it was never a player.
            if (!_authoritativeNames.Contains(id))
                _knownPlayers.Remove(id);
            _dead.Remove(id);
        }
    }

    public void SetMobMaxHp(int id, int hp)
    {
        if (hp <= 0)
            return;
        lock (_lock)
            _mobMaxHp[id] = hp;
    }

    public void SetMobCurrentHp(int id, int hp)
    {
        lock (_lock)
        {
            _mobCurHp[id] = hp;
            if (!_mobMaxHp.TryGetValue(id, out int max) || hp > max)
                _mobMaxHp[id] = hp;
        }
    }

    public bool IsMob(int id) { lock (_lock) return _mobCode.ContainsKey(id); }
    public bool IsSummon(int id) { lock (_lock) return _summonOwner.ContainsKey(id); }
    public bool IsConfirmedSummon(int id) { lock (_lock) return _confirmedSummons.Contains(id); }
    public bool IsKnownPlayer(int id) { lock (_lock) return _knownPlayers.Contains(id); }
    public bool HasNickname(int id) { lock (_lock) return _nicknames.ContainsKey(id); }
    public bool IsBossEntity(int id) { lock (_lock) return _bosses.Contains(id); }

    /// <summary>Ids of 100+ pass; smaller ones only once a spawn or identity record announced them.</summary>
    public bool IsPlausibleEntityId(int id)
    {
        if (id >= 100)
            return true;
        lock (_lock)
            return id >= 1 && _lowIds.Contains(id);
    }

    public void NoteLowIdEntity(int id)
    {
        if (id < 1 || id >= 100)
            return;
        lock (_lock)
            _lowIds.Add(id);
    }

    /// <summary>A mob/summon spawn: a new entity under this id, never a player.</summary>
    public void NoteSummonSpawn(int id)
    {
        lock (_lock)
        {
            ForgetEntity(id);
            if (!_authoritativeNames.Contains(id))
                _knownPlayers.Remove(id);
            _summonSpawns.Add(id);
        }
    }

    public void NotePlayerSpawn(int id)
    {
        lock (_lock)
            ForgetEntity(id);
    }

    public void RegisterConfirmedSummon(int summon, int owner)
    {
        lock (_lock)
            LinkSummonLocked(summon, owner);
    }

    public void LinkSummon(int summon, int owner)
    {
        lock (_lock)
            LinkSummonLocked(summon, owner);
    }

    private void LinkSummonLocked(int summon, int owner)
    {
        if (summon <= 0 || owner <= 0 || summon == owner)
            return;
        if (Resolve(owner) == summon)
            return; // would make a loop
        if (_summonOwner.TryGetValue(summon, out int old) && old != owner)
            ForgetEntity(summon); // a reused id: what the old summon did stays with its owner
        _confirmedSummons.Add(summon);
        _knownPlayers.Remove(summon);
        _summonOwner[summon] = owner;
    }

    /// <summary>The game reuses ids: drop the old entity's links, moving a summon's damage onto its owner.</summary>
    private void ForgetEntity(int id)
    {
        _confirmedSummons.Remove(id);
        _summonSpawns.Remove(id);
        _jobs.Remove(id);
        if (!_summonOwner.Remove(id, out int owner))
            return;
        owner = Resolve(owner);
        if (owner > 0 && owner != id)
            _current?.MergeActor(id, owner);
    }

    private int Resolve(int id)
    {
        int resolved = id;
        for (int hops = 0; hops < 16 && _summonOwner.TryGetValue(resolved, out int parent) && parent > 0 && parent != resolved; hops++)
            resolved = parent;
        return resolved;
    }

    // ───── names & identity ─────

    public int? FindIdByNickname(string name)
    {
        lock (_lock)
        {
            foreach (var (id, n) in _nicknames)
            {
                if (n == name)
                    return id;
            }
            return null;
        }
    }

    /// <summary>A name from a heuristic scan; only applied to real entities and never over a protocol-stated name.</summary>
    public void AppendNickname(int id, string name)
    {
        lock (_lock)
        {
            bool real = _nicknames.ContainsKey(id) || _knownPlayers.Contains(id) || _authoritativeNames.Contains(id)
                || _summonOwner.ContainsKey(id) || (_current?.Targets.Values.Any(t => t.Actors.ContainsKey(id)) ?? false);
            if (!real)
                return;
            foreach (var (other, n) in _nicknames)
            {
                if (other != id && n == name && _authoritativeNames.Contains(other))
                    return;
            }
            if (_authoritativeNames.Contains(id) && _nicknames.TryGetValue(id, out var existing) && existing != name)
                return;
            SetNickname(id, name, force: false);
        }
    }

    /// <summary>A name the protocol states outright (spawn or identity record).</summary>
    public void AppendNicknameAuthoritative(int id, string name)
    {
        lock (_lock)
        {
            _authoritativeNames.Add(id);
            SetNickname(id, name, force: true);
        }
    }

    private void SetNickname(int id, string name, bool force)
    {
        if (_provisionalNameId == id)
        {
            _provisionalNameId = null;
            force = true;
        }
        if (_nicknames.TryGetValue(id, out var existing))
        {
            if (existing == name)
                return;
            if (!force)
            {
                bool existingCjk = existing.EnumerateRunes().Any(r => Protocol.Names.IsCjk(r.Value));
                bool newCjk = name.EnumerateRunes().Any(r => Protocol.Names.IsCjk(r.Value));
                if (existingCjk && !newCjk && name.Length < existing.Length)
                    return;
                if (!newCjk && System.Text.Encoding.UTF8.GetByteCount(name) <= 5 && existing.Length > name.Length)
                    return;
            }
        }

        // Names are unique per server: the same name on another id means that id is stale.
        foreach (int old in _nicknames.Where(kv => kv.Value == name && kv.Key != id).Select(kv => kv.Key).ToList())
        {
            bool sameCharacter = force && _authoritativeNames.Contains(old);
            _nicknames.Remove(old);
            _knownPlayers.Remove(old);
            _authoritativeNames.Remove(old);
            if (sameCharacter)
            {
                // Back as a new entity (after dying, or a zone load): keep what they did.
                foreach (var key in _summonOwner.Where(kv => kv.Value == old).Select(kv => kv.Key).ToList())
                    _summonOwner[key] = id;
                _current?.MergeActor(old, id);
                if (_jobs.Remove(old, out var j))
                    _jobs.TryAdd(id, j);
            }
            else
            {
                foreach (var key in _summonOwner.Where(kv => kv.Value == old).Select(kv => kv.Key).ToList())
                    _summonOwner.Remove(key);
            }
            if (_localId == old)
                _localId = id;
        }

        _nicknames[id] = name;
        if (!_confirmedSummons.Contains(id))
        {
            _summonOwner.Remove(id);
            _knownPlayers.Add(id);
        }
        if (_localName is not null && _localName == name)
            _localId = id;
        if (_party.ContainsKey(name))
            BindRosterNamesByClass();
    }

    /// <summary>The game's self record: who you are, outright.</summary>
    public void SetLocalIdentity(int id, string? name)
    {
        lock (_lock)
        {
            bool changed = _localId != id || _localName != name || _localSource != LocalSource.SelfRecord;
            _localId = id;
            _localName = name;
            _localSource = LocalSource.SelfRecord;
            _knownPlayers.Add(id);
            if (changed)
                _identityEvents.Enqueue((id, name, "self record"));
        }
        FlushEnded();
    }

    /// <summary>The server sent a <c>06 38</c> record about this entity: you or your party.</summary>
    public void NotePartyScope(int id)
    {
        if (id < 100 || id > MaxEntityId)
            return;
        lock (_lock)
        {
            if (_partyScope.Count < 10_000)
                _partyScope.Add(id);
            _partyScopeCounts[id] = _partyScopeCounts.GetValueOrDefault(id) + 1;
            if (_knownPlayers.Contains(id))
                GuessLocalFromScope();
        }
        FlushEnded();
    }

    /// <summary>
    /// Among players, <c>06 38</c> records come about you alone: in a recorded session (2026-10-05)
    /// the local player had 53 of them within the first fight and over 2,000 later, while every party
    /// member and bystander had none. So the one player with many of them, far ahead of any other,
    /// is you. This identifies you in the first fight instead of after the next zone load.
    /// </summary>
    private void GuessLocalFromScope()
    {
        if (_localSource == LocalSource.SelfRecord)
            return;
        int bestId = 0, best = 0, second = 0;
        foreach (int player in _knownPlayers)
        {
            if (_confirmedSummons.Contains(player))
                continue;
            int n = _partyScopeCounts.GetValueOrDefault(player);
            if (n > best)
            {
                second = best;
                best = n;
                bestId = player;
            }
            else if (n > second)
            {
                second = n;
            }
        }
        if (best < MinScopeRecordsForLocal || second * 5 > best)
            return;
        SetGuessedLocal(bestId, _nicknames.GetValueOrDefault(bestId), LocalSource.Scope, "06 38 records");
    }

    private const int MinScopeRecordsForLocal = 20;

    private enum LocalSource { None, Loot, Scope, SelfRecord }
    private LocalSource _localSource;

    /// <summary>Applies a guess of who you are, unless a better source has already spoken.</summary>
    private void SetGuessedLocal(int id, string? name, LocalSource source, string why)
    {
        if (source < _localSource)
            return;
        if (_localSource == source && _localId == id)
            return;
        _localSource = source;
        _localId = id;
        _knownPlayers.Add(id);
        // No name from the game yet: use the one remembered from an earlier session, until the
        // game names this entity itself (it may be another character of the same account).
        if (name is null && !_nicknames.ContainsKey(id) && RememberedLocalName is { Length: > 0 } remembered
            && !_nicknames.ContainsValue(remembered))
        {
            _nicknames[id] = remembered;
            _provisionalNameId = id;
            name = remembered;
        }
        _localName = name;
        _identityEvents.Enqueue((id, name, why));
    }

    /// <summary>Your character name from an earlier session, shown until the game states it.</summary>
    public string? RememberedLocalName { get; set; }

    /// <summary>The entity showing <see cref="RememberedLocalName"/>; any name the game gives it replaces that.</summary>
    private int? _provisionalNameId;

    private bool IsInPartyScope(int id)
    {
        lock (_lock)
            return _partyScope.Contains(id);
    }

    /// <summary>Diagnostic messages about identity decisions (for the log).</summary>
    public event Action<string>? Trace;

    /// <summary>Whether you or your party damaged this entity this session.</summary>
    public bool IsFoughtTarget(int id)
    {
        lock (_lock)
            return _foughtTargets.Contains(id);
    }

    /// <summary>
    /// A loot record named <paramref name="name"/> as the owner of a mob that was fought. The self
    /// record can take minutes to arrive after the meter starts; loot records fill the gap. Mostly
    /// they name you, but a kill nearby can name someone else, so they are a vote among you and your
    /// party (the <c>06 38</c> scope): the owner named for the most kills, while leading outright.
    /// A tie withdraws the guess. Ported from A2Tools (GPL-3.0).
    /// </summary>
    public void NoteLootOwner(int mobId, int ownerId, string name)
    {
        Trace?.Invoke($"loot record: mob {mobId} owner {ownerId} '{name}' inScope={IsInPartyScope(ownerId)}");
        lock (_lock)
        {
            if (!_lootOwners.TryGetValue(name, out var entry))
                _lootOwners[name] = entry = (ownerId, new HashSet<int>());
            entry.Id = ownerId;
            if (entry.Kills.Count < 10_000)
                entry.Kills.Add(mobId);
            _lootOwners[name] = entry;

            if (_localSource > LocalSource.Loot)
                return; // a stronger source has said who you are

            var ranked = _lootOwners
                .Where(kv => _partyScope.Contains(kv.Value.Id))
                .Select(kv => (Name: kv.Key, kv.Value.Id, Kills: kv.Value.Kills.Count))
                .OrderByDescending(x => x.Kills)
                .ToList();
            if (ranked.Count == 0)
                return;
            if (ranked.Count > 1 && ranked[0].Kills == ranked[1].Kills)
            {
                if (_localSource == LocalSource.Loot)
                {
                    _localSource = LocalSource.None;
                    _localId = null;
                    _localName = null;
                    _identityEvents.Enqueue((0, null, "loot records disagree"));
                }
                return;
            }
            var leader = ranked[0];
            SetGuessedLocal(leader.Id, leader.Name, LocalSource.Loot, "loot record");
        }
        FlushEnded();
    }

    public void NoteSelfProfile(int id, string name, JobClass job, int? level, ushort server)
    {
        lock (_lock)
        {
            _localProfile = new LocalProfile(id, name, job, level ?? _localProfile?.Level, server);
            if (job != JobClass.Unknown)
                _jobs[id] = job;
        }
    }

    public void NotePlayerServer(string name, ushort server)
    {
        lock (_lock)
            _playerServers[name] = server;
    }

    public LocalProfile? GetLocalProfile()
    {
        lock (_lock)
            return _localProfile ?? (_localId is int id ? new LocalProfile(id, _localName, JobClass.Unknown, null, 0) : null);
    }

    // ───── party ─────

    public void SetCurrentDungeon(int dungeonId)
    {
        lock (_lock)
        {
            _dungeonId = dungeonId;
            if (_current is { } c)
                c.DungeonId = dungeonId;
        }
    }

    /// <summary>A complete roster replaces the party; a partial one only updates the members it decoded.</summary>
    public void SetPartyRoster(List<PartyMember> members, bool complete)
    {
        if (members.Count == 0)
            return;
        lock (_lock)
        {
            if (complete)
                _party.Clear();
            foreach (var m in members)
                _party[m.Name] = m;
            BindRosterNamesByClass();
        }
    }

    public IReadOnlyList<PartyMember> GetParty()
    {
        lock (_lock)
            return _party.Values.OrderBy(m => m.Slot).ToList();
    }

    /// <summary>
    /// Names party members whose spawn the meter never saw by class: exactly one roster member of a
    /// class without an entity, and exactly one unnamed player of that class fighting now.
    /// </summary>
    private void BindRosterNamesByClass()
    {
        if (_party.Count < 2 || _current is null)
            return;
        var named = _nicknames.Values.ToHashSet();
        var open = _party.Values.Where(m => m.Job != JobClass.Unknown && !named.Contains(m.Name))
            .GroupBy(m => m.Job)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Name).ToList());
        if (open.Count == 0)
            return;

        var skills = new Dictionary<int, HashSet<int>>();
        foreach (var t in _current.Targets.Values)
        {
            foreach (var (actor, data) in t.Actors)
            {
                if (_knownPlayers.Contains(actor) && !_nicknames.ContainsKey(actor) && !_summonOwner.ContainsKey(actor))
                {
                    if (!skills.TryGetValue(actor, out var set))
                        skills[actor] = set = new();
                    foreach (var key in data.Skills.Keys)
                        set.Add(key.Skill);
                }
            }
        }
        // More unnamed players than open slots means strangers are fighting too.
        if (skills.Count > open.Values.Sum(v => v.Count))
            return;

        var binds = new List<(int Id, string Name)>();
        foreach (var (job, names) in open)
        {
            if (names.Count != 1)
                continue;
            var players = skills.Where(kv => _jobs.GetValueOrDefault(kv.Key) == job)
                .Select(kv => (Id: kv.Key, Count: kv.Value.Count))
                .OrderByDescending(p => p.Count).ThenBy(p => p.Id)
                .ToList();
            if (players.Count == 1 || (players.Count > 1 && players[0].Count >= 3 * players[1].Count))
                binds.Add((players[0].Id, names[0]));
        }
        foreach (var (id, name) in binds)
            SetNickname(id, name, force: false);
    }

    // ───── records ─────

    private string DisplayName(int id)
    {
        if (_nicknames.TryGetValue(id, out var name))
            return name;
        return $"#{id}";
    }

    private string NpcName(int entityId, out int npcCode)
    {
        npcCode = _mobCode.GetValueOrDefault(entityId);
        if (npcCode != 0 && _data.Npc(npcCode) is { Name.Length: > 0 } npc)
            return npc.Name;
        return npcCode != 0 ? $"Unknown NPC {npcCode}" : $"Unknown #{entityId}";
    }

    private bool IsPlayerRow(int resolved, HashSet<int> targetIds)
    {
        // A summon whose owner is not known yet is left out until it is linked.
        return !targetIds.Contains(resolved) && (_knownPlayers.Contains(resolved) || _nicknames.ContainsKey(resolved));
    }

    private EncounterRecord BuildRecord(Encounter e, bool inProgress)
    {
        var record = new EncounterRecord
        {
            Id = e.Id,
            StartedAt = e.StartedAt,
            DurationMs = Math.Max(e.LastMs - e.StartMs, 1000),
            InProgress = inProgress,
            IsBoss = e.HasBoss,
            DungeonId = e.DungeonId,
            DungeonName = _data.DungeonName(e.DungeonId),
        };

        var targetIds = e.Targets.Keys.ToHashSet();
        bool bossOnly = e.HasBoss && Options.BossDamageOnly;
        var counted = e.Targets.Values.Where(t => !bossOnly || _bosses.Contains(t.Id)).ToList();

        // Targets.
        foreach (var t in e.Targets.Values.OrderByDescending(t => t.Total))
        {
            string name = NpcName(t.Id, out int code);
            record.Targets.Add(new TargetRecord
            {
                EntityId = t.Id,
                NpcCode = code,
                Name = name,
                IsBoss = _bosses.Contains(t.Id),
                Killed = t.Killed,
                DamageTaken = t.Total,
                MaxHp = _mobMaxHp.GetValueOrDefault(t.Id),
            });
        }

        var main = record.Targets.FirstOrDefault(t => t.IsBoss) ?? record.Targets.FirstOrDefault();
        if (main is not null)
        {
            int others = record.Targets.Select(t => t.Name).Distinct().Count() - 1;
            record.Name = main.IsBoss || others <= 0 ? main.Name : $"{main.Name} +{others}";
            record.NpcCode = main.NpcCode;
            record.BossMaxHp = main.MaxHp;
            record.BossCurrentHp = main.Killed ? 0 : _mobCurHp.GetValueOrDefault(main.EntityId, (int)main.MaxHp);
            record.IsTrainingDummy = _dummies.Contains(main.EntityId);
            record.Killed = e.HasBoss
                ? record.Targets.Where(t => t.IsBoss).All(t => t.Killed)
                : record.Targets.All(t => t.Killed);
        }

        // Damage per resolved player.
        var players = new Dictionary<int, PlayerRecord>();
        var skillMaps = new Dictionary<int, Dictionary<(int, bool, bool), SkillRecord>>();

        PlayerRecord Player(int id)
        {
            if (!players.TryGetValue(id, out var p))
            {
                p = new PlayerRecord
                {
                    ActorId = id,
                    Name = DisplayName(id),
                    Job = JobFor(id),
                    IsLocal = id == _localId,
                };
                players[id] = p;
                skillMaps[id] = new();
            }
            return p;
        }

        foreach (var t in counted)
        {
            foreach (var (actor, data) in t.Actors)
            {
                int owner = Resolve(actor);
                if (!IsPlayerRow(owner, targetIds))
                    continue;
                // Damage between two players is never counted as damage.
                if (_knownPlayers.Contains(Resolve(t.Id)) && _knownPlayers.Contains(owner))
                    continue;
                bool viaSummon = owner != actor;
                var p = Player(owner);
                p.Damage += data.Damage;
                var map = skillMaps[owner];
                foreach (var ((code, dot), s) in data.Skills)
                {
                    var key = (code, dot, viaSummon);
                    if (!map.TryGetValue(key, out var sr))
                    {
                        sr = new SkillRecord
                        {
                            Code = code,
                            IsDot = dot,
                            IsSummon = viaSummon,
                            Name = SkillDisplayName(code),
                            Min = long.MaxValue,
                        };
                        map[key] = sr;
                    }
                    sr.Damage += s.Damage;
                    sr.Hits += s.Hits;
                    sr.Crits += s.Crits;
                    sr.Back += s.Back;
                    sr.Perfect += s.Perfect;
                    sr.Double += s.Double;
                    sr.Parry += s.Parry;
                    sr.Min = Math.Min(sr.Min, s.Min);
                    sr.Max = Math.Max(sr.Max, s.Max);
                    p.Hits += s.Hits;
                    p.Crits += s.Crits;
                    p.MaxHit = Math.Max(p.MaxHit, s.Max);
                }
            }
        }

        // Healing per resolved healer.
        foreach (var (healer, perSkill) in e.Heals)
        {
            int owner = Resolve(healer);
            if (!IsPlayerRow(owner, targetIds))
                continue;
            var p = Player(owner);
            foreach (var ((code, hot), h) in perSkill)
            {
                p.Heal += h.Amount;
                var existing = p.HealSkills.FirstOrDefault(x => x.Code == code && x.IsHot == hot);
                if (existing is null)
                    p.HealSkills.Add(new HealSkillRecord { Code = code, IsHot = hot, Name = SkillDisplayName(code), Amount = h.Amount, Ticks = h.Ticks });
                else
                {
                    existing.Amount += h.Amount;
                    existing.Ticks += h.Ticks;
                }
            }
        }

        IEnumerable<PlayerRecord> rows = players.Values;
        if (Options.PartyOnly && _party.Count >= 2)
            rows = rows.Where(p => p.IsLocal || _party.ContainsKey(p.Name));
        var list = rows.Where(p => p.Damage > 0 || p.Heal > 0).ToList();

        double seconds = record.DurationSeconds;
        record.TotalDamage = list.Sum(p => p.Damage);
        record.TotalHeal = list.Sum(p => p.Heal);
        foreach (var p in list)
        {
            p.Dps = p.Damage / seconds;
            p.Hps = p.Heal / seconds;
            p.DamageShare = record.TotalDamage > 0 ? (double)p.Damage / record.TotalDamage : 0;
            p.HealShare = record.TotalHeal > 0 ? (double)p.Heal / record.TotalHeal : 0;
            p.Skills = skillMaps[p.ActorId].Values.OrderByDescending(s => s.Damage).ToList();
            foreach (var s in p.Skills)
            {
                if (s.Min == long.MaxValue)
                    s.Min = 0;
                s.Share = p.Damage > 0 ? (double)s.Damage / p.Damage : 0;
            }
            p.HealSkills = p.HealSkills.OrderByDescending(h => h.Amount).ToList();
        }
        record.Players = list.OrderByDescending(p => p.Damage).ThenByDescending(p => p.Heal).ToList();
        return record;
    }

    private JobClass JobFor(int id)
    {
        if (_jobs.TryGetValue(id, out var job))
            return job;
        if (_nicknames.TryGetValue(id, out var name) && _party.TryGetValue(name, out var member))
            return member.Job;
        return JobClass.Unknown;
    }

    private string SkillDisplayName(int code)
    {
        string name = _data.SkillName(code);
        if (name.Length > 0)
            return name;
        int baseCode = code - code % 10000;
        name = _data.SkillName(baseCode);
        return name.Length > 0 ? name : $"Skill {code}";
    }
}
