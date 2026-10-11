using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Grammar;
using EQ2Parser.Core.Logs;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.Core.Engine;

/// <summary>The alert-side stages of a line: everything
/// <see cref="LogLineProcessor.Process"/> does for a line that is not
/// parsing it and recording its combat data. Each runs in its own guard
/// (see <see cref="LogLineProcessor.AlertFaulted"/>).</summary>
public enum AlertStage
{
    /// <summary>Trigger evaluation and every handler of TriggerEngine.Fired
    /// (audio, speech, trigger-started timers), and the trigger engine's
    /// zone change.</summary>
    Triggers,

    /// <summary>Extracting a pasted trigger share and the handlers of
    /// <see cref="LogLineProcessor.TriggerShared"/>.</summary>
    TriggerShare,

    /// <summary>The raid-attendance prefilter and the handlers of
    /// <see cref="LogLineProcessor.RaidLine"/> (roster, DKP progress, loot).</summary>
    RaidLine,

    /// <summary>The handlers of <see cref="LogLineProcessor.StatusApplied"/>
    /// (mass-detriment callouts).</summary>
    StatusCallout,

    /// <summary>The spell-timer notifications a combat action or a death
    /// sends, and everything the timer service raises from them.</summary>
    SpellTimers,
}

/// <summary>
/// The per-source driver: raw <see cref="LogLine"/>s in, engine state out.
/// Resolves the grammar's "YOU" marker to the log owner, applies the ACT
/// lifecycle contract (idle check per line, SetEncounter before every swing),
/// turns death events into Killing-ability swings, and — when composed with
/// a <see cref="TriggerEngine"/>/<see cref="SpellTimerService"/> — evaluates
/// triggers per line and notifies spell timers for every combat action
/// (ACT's NotifySpell semantics), all synchronously in the line path so
/// alerts fire within the tail-poll latency (~10 ms).
///
/// Two kinds of work share that line path, and they fail differently. The
/// COMBAT path (idle rule, grammar, SetEncounter, AddSwing) is the product:
/// a throw there propagates, and the caller decides what a failed line
/// costs (LogSource skips the line). The ALERT side (<see cref="AlertStage"/>)
/// is everything that only reacts to the line — and it runs subscribers'
/// code. Each alert stage is contained: a throw is counted
/// (<see cref="AlertFaults"/>), handed to <see cref="AlertFaulted"/>, and
/// the line carries on, so a bug in a trigger handler, a roster rule or a
/// timer overlay can cost an alert but never the line's damage, heal or
/// death. Before, the alert stages ahead of the grammar took the whole
/// line down with them, and a timer handler that threw on a death line
/// cost the death.
/// </summary>
public sealed class LogLineProcessor
{
    /// <summary>Max lag between a line's log time and its arrival before it
    /// counts as replayed history rather than live play (clock skew between
    /// the game's stamp and ours stays well under this).</summary>
    public static readonly TimeSpan TriggerFreshness = TimeSpan.FromSeconds(30);

    private readonly TriggerEngine? _triggers;
    private readonly SpellTimerService? _timers;

    public LogLineProcessor(ParserEngine engine, TriggerEngine? triggers = null, SpellTimerService? timers = null)
    {
        Engine = engine;
        _triggers = triggers;
        _timers = timers;
        if (_triggers is not null && _timers is not null)
        {
            // A trigger's timer request behaves like ACT: Self forced true.
            _triggers.Fired += fired =>
            {
                if (fired.Timer is { } t)
                    _timers.NotifyLinked(t.TimerName, t.Zone, t.Category, t.Attacker, t.Victim, LastLineTime, Engine.CurrentZone);
            };
        }
    }

    public ParserEngine Engine { get; }

    /// <summary>Timestamp of the most recent processed line.</summary>
    public DateTimeOffset LastLineTime { get; private set; }

    /// <summary>A LIVE status-effect apply (victim, log effect word,
    /// arrival) — feeds the mass-detriment callout monitor. Replayed
    /// history never raises it (same freshness rule as triggers).</summary>
    public event Action<string, string, DateTimeOffset>? StatusApplied;

    /// <summary>Raised (log-pump thread) when a LIVE chat line carries an
    /// ACT trigger-share snippet from ANOTHER player — the app offers to
    /// add it. Never fires during catch-up/replay or for own pastes.</summary>
    public event Action<Triggers.SharedTrigger>? TriggerShared;

    /// <summary>A LIVE line that passed the raid-attendance prefilter
    /// (raid join/leave deltas, Guildmate presence, /who output) — feeds
    /// the raid roster tracker. Live-only: replayed history must never
    /// mutate the current raid session. (message, log timestamp).</summary>
    public event Action<string, DateTimeOffset>? RaidLine;

    /// <summary>Lines seen / lines that produced a grammar event — the golden
    /// harness's coverage counters.</summary>
    public long LinesSeen { get; private set; }
    public long LinesMatched { get; private set; }

    /// <summary>An alert stage threw while a line was being processed, and
    /// was contained: the rest of the line (the other alert stages, the
    /// grammar, the combat path) still ran. Raised on the thread that
    /// called <see cref="Process"/>, inside it — so under whatever lock the
    /// caller holds; a subscriber that wants to write a file should note
    /// the fault and write after the call returns. Core cannot reach the
    /// app's crash log; this is how the failure gets there. A subscriber
    /// that itself throws is ignored: reporting a fault must not become
    /// the thing that costs the line.</summary>
    public event Action<AlertStage, Exception>? AlertFaulted;

    /// <summary>Alert-stage faults contained so far — counted whether or
    /// not anyone listens to <see cref="AlertFaulted"/>.</summary>
    public long AlertFaults { get; private set; }

    private void OnAlertFault(AlertStage stage, Exception error)
    {
        AlertFaults++;
        try
        {
            AlertFaulted?.Invoke(stage, error);
        }
        catch (Exception)
        {
            // See AlertFaulted: the count above still has it.
        }
    }

    public void Process(in LogLine line)
    {
        LinesSeen++;
        // The alert/timer anchor: arrival stamp when live, log time on import.
        // Stat math below stays on line.Timestamp (the compatibility clock).
        var anchor = line.ObservedAt ?? line.Timestamp;
        LastLineTime = anchor;
        Engine.OnLineTime(line.Timestamp);
        // Triggers and timers are live alerts: a line only fires them when
        // it was written moments before we read it. Replaying an old file
        // (parse-from-start) must never spam beeps/TTS/timer bars for
        // history. No arrival stamp (direct feeds, tests) means live.
        var live = line.ObservedAt is null || line.ObservedAt.Value - line.Timestamp < TriggerFreshness;

        // The three alert stages below run BEFORE the grammar so an alert
        // fires as early as it can. Each has its own guard for that very
        // reason: unguarded, a subscriber that threw here skipped the
        // grammar, and the line's combat data went with it. One guard per
        // stage, not one around all three — a broken trigger must not cost
        // the raid line either.
        if (live && _triggers is not null)
        {
            try
            {
                _triggers.Process(line.Message, anchor);
            }
            catch (Exception ex)
            {
                OnAlertFault(AlertStage.Triggers, ex);
            }
        }

        // Trigger shares pasted into chat (the ACT community convention).
        // Live-only for the same reason as alerts: replaying an old log
        // must not re-offer every share ever seen. Own pastes are skipped —
        // the sharer obviously has the trigger.
        if (live && TriggerShared is not null && line.Message.Contains("<Trigger", StringComparison.Ordinal))
        {
            try
            {
                if (Triggers.ChatTriggerShare.TryExtract(line.Message) is { Self: false } share)
                    TriggerShared.Invoke(share);
            }
            catch (Exception ex)
            {
                OnAlertFault(AlertStage.TriggerShare, ex);
            }
        }

        // Raid attendance signals — the prefilter is a handful of cheap
        // Contains/StartsWith checks, so cold lines cost almost nothing.
        if (live && RaidLine is not null)
        {
            try
            {
                if (Raid.RaidRosterTracker.LooksRelevant(line.Message))
                    RaidLine.Invoke(line.Message, line.Timestamp);
            }
            catch (Exception ex)
            {
                OnAlertFault(AlertStage.RaidLine, ex);
            }
        }

        // Scripted-win say lines (bosses that end by script, not death) —
        // only consulted while a fight is live; StartsWith early-out keeps
        // the per-line cost at a single character compare.
        if (Engine.ActiveEncounter is { } scriptedActive && ScriptedWins.Default.TryMatch(line.Message))
            scriptedActive.ScriptedWin = true;

        var parsed = EnglishGrammar.TryParse(line.Message);
        if (parsed is null)
            return;
        LinesMatched++;

        switch (parsed)
        {
            case ZoneEvent zone:
                Engine.ChangeZone(zone.ZoneName, line.Timestamp);
                if (_triggers is not null)
                {
                    try
                    {
                        _triggers.SetZone(zone.ZoneName);
                    }
                    catch (Exception ex)
                    {
                        OnAlertFault(AlertStage.Triggers, ex);
                    }
                }
                break;

            case InstanceLockoutEvent lockout:
                Engine.ApplyInstanceLockout(lockout.Remaining, line.Timestamp);
                break;

            case SwingEvent swing:
                {
                    var attacker = Resolve(swing.Attacker);
                    var victim = Resolve(swing.Victim);
                    // Mass-detriment callouts must see status lines even when no
                    // fight is running (pre-pull stuns, no-damage script phases) —
                    // fire BEFORE the encounter gate below can break. Guarded:
                    // the callout is an alert, and the swing below is not.
                    if (live && StatusApplied is not null
                        && swing.Category == SwingCategory.StatusEffect && swing.DamageType == "applied")
                    {
                        try
                        {
                            StatusApplied.Invoke(victim, swing.Ability, anchor);
                        }
                        catch (Exception ex)
                        {
                            OnAlertFault(AlertStage.StatusCallout, ex);
                        }
                    }
                    // Self-damage procs are not hostile action — they must not
                    // start a fight or keep one alive.
                    var hostile = swing.Category is SwingCategory.Melee or SwingCategory.NonMelee
                        && !string.Equals(attacker, victim, StringComparison.OrdinalIgnoreCase);
                    if (!Engine.SetEncounter(line.Timestamp, attacker, victim, hostile))
                        break;
                    // The avoid-actor rides in Extra ("by=YOU") and needs the
                    // same owner resolution as attacker/victim.
                    var extra = swing.Extra;
                    if (extra is not null && extra.StartsWith("by=", StringComparison.Ordinal))
                        extra = "by=" + Resolve(extra[3..]);
                    Engine.AddSwing(
                        swing.Category, swing.Critical, swing.Special,
                        attacker, swing.Ability, swing.Damage,
                        line.Timestamp, victim, swing.DamageType, extra, line.ObservedAt);
                    // Every combat action notifies the spell timers by ability
                    // name (ACT semantics) — how cast-driven timers start. Timers
                    // anchor to the arrival stamp so bars start the instant the
                    // line lands, not on the next whole second. Replayed history
                    // never starts bars (same freshness rule as triggers).
                    // Guarded: the swing is already recorded, and a timer
                    // fault must not turn a line that parsed into a "failed"
                    // one that counts toward stopping the source.
                    if (live && _timers is not null)
                    {
                        try
                        {
                            _timers.Notify(
                                attacker, swing.Ability,
                                self: attacker == Engine.OwnerName || victim == Engine.OwnerName,
                                victim, anchor, Engine.CurrentZone);
                            // Recast debuffs (Traumatic Swipe): every hit refreshes
                            // the victim's timer mod, ACT-style; a cure stripping a
                            // known debuff drops the mod again.
                            _timers.NotifyRecastDebuff(attacker, victim, swing.Ability, anchor);
                            if (swing.Category == SwingCategory.CureDispel)
                                _timers.NotifyDispel(victim, swing.DamageType, anchor);
                        }
                        catch (Exception ex)
                        {
                            OnAlertFault(AlertStage.SpellTimers, ex);
                        }
                    }
                    break;
                }

            case DeathEvent death:
                {
                    var killer = Resolve(death.Killer);
                    var victim = Resolve(death.Victim);
                    // In the owner's OWN log a real self-death is always second
                    // person ("Avatar of Growth has killed you.") — the game
                    // never writes the log owner's death in third person. So a
                    // third-person death naming the OWNER can only be an entity
                    // acting under their name: the Templar hammer temp pet et
                    // al. Verified empirically (2026-08, 685MB of raid logs):
                    // 82 own-name deaths were all hammer expiries on the summon
                    // cadence; the 16 real deaths were all "killed you", with
                    // zero overlap between the shapes. ACT counts them all as
                    // the player — a deliberate, documented deviation.
                    var petDeath = death.Victim.Equals(Engine.OwnerName, StringComparison.OrdinalIgnoreCase);
                    // Death drops the timer mods ON the combatant AND the ones
                    // they applied — a dead swiper's debuff dies with them, and
                    // running timers it stretched rescale pro-rata. A pet death
                    // must NOT strip the owner's timers. Guarded: this runs
                    // BEFORE the death is recorded, so a timer fault here
                    // used to cost the death itself.
                    if (live && !petDeath && _timers is not null)
                    {
                        try
                        {
                            _timers.NotifyDeath(victim, anchor);
                        }
                        catch (Exception ex)
                        {
                            OnAlertFault(AlertStage.SpellTimers, ex);
                        }
                    }
                    // Deaths are recorded into a live fight but never start one —
                    // an out-of-combat "Alas, X has died" is not an encounter.
                    if (!Engine.SetEncounter(line.Timestamp, killer, victim, hostile: false))
                        break;
                    Engine.AddSwing(
                        SwingCategory.Melee, false, "None",
                        killer, Combatant.KillingAbility, DamageValue.Death,
                        line.Timestamp, victim, "death",
                        extra: petDeath ? Combatant.PetDeathExtra : null,
                        observedAt: line.ObservedAt);
                    break;
                }
        }
    }

    // "YOURSELF" appears as the victim of self-targeted lines ("…absorbs 720
    // points of damage from being done to YOURSELF."), and kill lines use
    // lowercase second person ("Avatar of Growth has killed you.") — the
    // case-sensitive match sent the owner's REAL deaths to a stray
    // combatant literally named "you". All you-forms are the log owner.
    private string Resolve(string name) =>
        name.Equals("you", StringComparison.OrdinalIgnoreCase)
        || name.Equals("yourself", StringComparison.OrdinalIgnoreCase)
            ? Engine.OwnerName
            : name;
}
