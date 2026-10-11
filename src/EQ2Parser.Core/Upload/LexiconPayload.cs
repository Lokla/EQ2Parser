using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQ2Parser.Core.Upload;

/// <summary>
/// Wire DTOs for POST /api/parses/ingest — field names must match the
/// EQ2Lexicon server's IngestRequest pydantic models exactly (the site
/// cannot tell an EQ2Parser upload from an ACT-plugin one). Conventions
/// inherited from the plugin's PayloadBuilder: timestamps as
/// yyyy-MM-dd'T'HH:mm:ss'Z' UTC, booleans as "T"/"F", encid 8 hex chars.
/// </summary>
public sealed record LexiconPayload
{
    [JsonPropertyName("logger_name")] public required string LoggerName { get; init; }
    [JsonPropertyName("logger_server")] public required string LoggerServer { get; init; }
    [JsonPropertyName("encounter")] public required PayloadEncounter Encounter { get; init; }
    [JsonPropertyName("combatants")] public List<PayloadCombatant> Combatants { get; init; } = [];
    [JsonPropertyName("damage_types")] public List<PayloadDamageType> DamageTypes { get; init; } = [];
    [JsonPropertyName("attack_types")] public List<PayloadAttackType> AttackTypes { get; init; } = [];

    /// <summary>Soft provenance signals (the plugin's v0.1.15 field — the
    /// server stores ≤32 entries × 64 chars). Null = omitted from the JSON;
    /// see LogProvenance for the values we stamp.</summary>
    [JsonPropertyName("client_warnings")] public List<string>? ClientWarnings { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);
}

public sealed record PayloadEncounter
{
    [JsonPropertyName("encid")] public required string EncId { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("zone")] public string? Zone { get; init; }
    [JsonPropertyName("starttime")] public required string StartTime { get; init; }
    [JsonPropertyName("endtime")] public required string EndTime { get; init; }
    [JsonPropertyName("duration")] public int Duration { get; init; }
    [JsonPropertyName("damage")] public long Damage { get; init; }
    [JsonPropertyName("encdps")] public double EncDps { get; init; }
    [JsonPropertyName("kills")] public int Kills { get; init; }
    [JsonPropertyName("deaths")] public int Deaths { get; init; }
    [JsonPropertyName("success")] public int Success { get; init; }
}

public sealed record PayloadCombatant
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("ally")] public required string Ally { get; init; } // "T" / "F"
    [JsonPropertyName("starttime")] public string? StartTime { get; init; }
    [JsonPropertyName("endtime")] public string? EndTime { get; init; }
    [JsonPropertyName("duration")] public int Duration { get; init; }
    [JsonPropertyName("damage")] public long Damage { get; init; }
    [JsonPropertyName("kills")] public int Kills { get; init; }
    [JsonPropertyName("healed")] public long Healed { get; init; }
    [JsonPropertyName("critheals")] public int CritHeals { get; init; }
    [JsonPropertyName("heals")] public int Heals { get; init; }
    [JsonPropertyName("curedispels")] public int CureDispels { get; init; }
    [JsonPropertyName("powerdrain")] public long PowerDrain { get; init; }
    [JsonPropertyName("powerreplenish")] public long PowerReplenish { get; init; }
    [JsonPropertyName("dps")] public double Dps { get; init; }
    [JsonPropertyName("encdps")] public double EncDps { get; init; }
    [JsonPropertyName("enchps")] public double EncHps { get; init; }
    [JsonPropertyName("hits")] public int Hits { get; init; }
    [JsonPropertyName("crithits")] public int CritHits { get; init; }
    [JsonPropertyName("blocked")] public int Blocked { get; init; }
    [JsonPropertyName("misses")] public int Misses { get; init; }
    [JsonPropertyName("swings")] public int Swings { get; init; }
    [JsonPropertyName("healstaken")] public long HealsTaken { get; init; }
    [JsonPropertyName("damagetaken")] public long DamageTaken { get; init; }
    [JsonPropertyName("deaths")] public int Deaths { get; init; }
    [JsonPropertyName("tohit")] public double ToHit { get; init; }
}

public sealed record PayloadDamageType
{
    [JsonPropertyName("combatant")] public required string Combatant { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("starttime")] public string? StartTime { get; init; }
    [JsonPropertyName("endtime")] public string? EndTime { get; init; }
    [JsonPropertyName("damage")] public long Damage { get; init; }
    [JsonPropertyName("encdps")] public double EncDps { get; init; }
    [JsonPropertyName("dps")] public double Dps { get; init; }
    [JsonPropertyName("critperc")] public double CritPerc { get; init; }
    [JsonPropertyName("median")] public long Median { get; init; }
    [JsonPropertyName("minhit")] public long MinHit { get; init; }
    [JsonPropertyName("maxhit")] public long MaxHit { get; init; }
    [JsonPropertyName("hits")] public int Hits { get; init; }
    [JsonPropertyName("crithits")] public int CritHits { get; init; }
    [JsonPropertyName("blocked")] public int Blocked { get; init; }
    [JsonPropertyName("misses")] public int Misses { get; init; }
    [JsonPropertyName("swings")] public int Swings { get; init; }
}

/// <summary>One ability's row for one attacker in ONE swing category — the
/// site's uniqueness key is (attacker, swingtype, type), and its
/// per-combatant tabs pick rows by <see cref="SwingType"/>, so an ability
/// that logs in two categories is two rows.</summary>
public sealed record PayloadAttackType
{
    [JsonPropertyName("attacker")] public required string Attacker { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("swingtype")] public int SwingType { get; init; }
    [JsonPropertyName("damage")] public long Damage { get; init; }
    [JsonPropertyName("encdps")] public double EncDps { get; init; }

    /// <summary>Damage over THIS attack type's own active window (first to
    /// last swing) — ACT's per-attacktype DPS, which the site's per-attack
    /// DPS column displays. Absent before v0.3.5, so those uploads rendered
    /// DPS 0 on the parse page (the server now falls back to encdps).</summary>
    [JsonPropertyName("dps")] public double Dps { get; init; }

    /// <summary>Crit percentage (0-100) — the site's CRIT% column. ACT ships
    /// it pre-formatted; we compute 100*crithits/hits.</summary>
    [JsonPropertyName("critperc")] public double CritPerc { get; init; }

    [JsonPropertyName("median")] public long Median { get; init; }
    [JsonPropertyName("minhit")] public long MinHit { get; init; }
    [JsonPropertyName("maxhit")] public long MaxHit { get; init; }

    /// <summary>ACT's type text for the row, which the site stores as sent
    /// (<c>IngestAttackType.resist</c>, nullable) and shows per tab: the
    /// heal type on the Healed tab — "Hitpoints", or "Absorption", the
    /// exact value that marks a ward — and the kind on the Threat tab
    /// ("Increase" / "Decrease"). Power replenish carries "Power" and a
    /// damage row its damage type; the site's pages read neither, its
    /// export API passes the damage type through. Null = omitted from the
    /// JSON, which is what a row gets when its swings do not share one
    /// value (see PayloadBuilder).</summary>
    [JsonPropertyName("resist")] public string? Resist { get; init; }

    [JsonPropertyName("hits")] public int Hits { get; init; }
    [JsonPropertyName("crithits")] public int CritHits { get; init; }
    [JsonPropertyName("blocked")] public int Blocked { get; init; }
    [JsonPropertyName("misses")] public int Misses { get; init; }
    [JsonPropertyName("swings")] public int Swings { get; init; }
}
