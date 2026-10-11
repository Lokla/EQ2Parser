using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.App.ViewModels;

/// <summary>Mob header row in the curation list.</summary>
public sealed record CurationMobRow(string Mob, int AbilityCount)
{
    public string AbilityCountLabel =>
        Loc.Format(AbilityCount == 1 ? "Curation_AbilitiesOne" : "Curation_AbilitiesMany", AbilityCount);
}

/// <summary>One mined ability row with its measured stats. The labels are
/// interface text and come from the dictionaries; what the LOG named stays
/// as the log spelled it — the ability, the damage schools and the control
/// word, which is also the value a timer stores.</summary>
public sealed partial class CurationAbilityRow(MinedAbility mined) : ObservableObject
{
    /// <summary>Between the parts of a label. Each part is a whole
    /// dictionary string, so no language has to fit another's word order.</summary>
    private const string PartSeparator = " · ";

    public MinedAbility Mined { get; } = mined;
    public string Ability => Mined.Ability;

    public string CastsLabel => string.Join(
        " / ",
        Loc.Format(Mined.Casts == 1 ? "Curation_CastsOne" : "Curation_CastsMany", Mined.Casts),
        Loc.Format(Mined.Fights == 1 ? "Curation_FightsOne" : "Curation_FightsMany", Mined.Fights));

    /// <summary>Swipe-adjusted base recast leads when recast debuffs were
    /// in play; the raw observed median stays visible beside it.</summary>
    public string RecastLabel
    {
        get
        {
            if (Mined.MedianIntervalSeconds is not { } median)
                return Loc.Get("Curation_RecastSingleCast");
            if (Mined.SwipeCoverage > 0.02 && Mined.BaseIntervalSeconds is { } baseline)
                return Loc.Format("Curation_RecastBase", baseline, median, Mined.MinIntervalSeconds);
            return Loc.Format("Curation_RecastPlain", median, Mined.MinIntervalSeconds);
        }
    }

    public string RecastToolTip =>
        Mined.SwipeCoverage > 0.02
            ? Loc.Format("Curation_RecastTipSwiped", Mined.SwipeCoverage, Mined.BaseIntervalSeconds)
            : Loc.Get("Curation_RecastTipPlain");

    public string ProfileLabel
    {
        get
        {
            List<string> parts = [];
            if (Mined.IsDetriment)
            {
                parts.Add(Loc.Get("Curation_ProfileDetriment"));
            }
            else
            {
                var perCast = Mined.Casts > 0 ? Mined.TotalDamage / Mined.Casts : 0;
                parts.Add(Loc.Format("Curation_ProfilePerCast", CombatantRow.Compact(perCast)));
                // The schools are the log's own words ("heat", "crushing").
                parts.Add(Mined.DamageTypes is { Length: > 0 } types
                    ? types
                    : Mined.IsMelee ? Loc.Get("Curation_ProfileMelee") : Loc.Get("Curation_ProfileSpell"));
            }
            // Correlated control kind, with measured duration when the
            // release lines paired: "stun ~4s". Canonical tooltip word
            // when the log word maps; the raw flavour adjective otherwise
            // (informational here — only canonical words get STORED).
            if ((Vocabulary.CanonicalControlEffect(Mined.EffectKind) ?? Mined.EffectKind) is { } kind)
            {
                parts.Add(Mined.EffectDurationSeconds is { } dur
                    ? Loc.Format("Curation_ProfileEffectTimed", kind, dur)
                    : kind);
            }
            if (Mined.AvgTargets >= 1.8)
                parts.Add(Loc.Format("Curation_ProfileAoe", Mined.AvgTargets));
            if (Mined.TicksPerCast >= 1.8)
                parts.Add(Loc.Format("Curation_ProfileReapplies", Mined.TicksPerCast));
            // The log named no caster for these — attributed to the fight's
            // subject, so the curator can sanity-check the assignment.
            if (Mined.SourceInferred)
                parts.Add(Loc.Get("Curation_ProfileSourceInferred"));
            return string.Join(PartSeparator, parts);
        }
    }

    /// <summary>A local spell timer with this name already exists — after
    /// re-importing the old ACT pack this doubles as the "in ACT" column.</summary>
    [ObservableProperty]
    private bool _hasTimer;

    public bool CanTime => Mined.MedianIntervalSeconds is not null;
}

/// <summary>
/// The curation workbench: mine archived fights for what each mob actually
/// casts (measured recast intervals = ground-truth timer durations), see at
/// a glance which abilities already have local timers, and mint the missing
/// ones — locally or as ACT XML for the Lexicon curator pages.
/// </summary>
public sealed partial class CurationViewModel : ObservableObject
{
    private readonly SourceManager _manager;

    public ObservableCollection<string> Zones { get; } = [];
    public ObservableCollection<object> Rows { get; } = [];

    /// <summary>Damage mode ranks by what hurts; detriment mode surfaces
    /// the zero-damage control effects (stifles, stuns, fears) that damage
    /// ranking buries — sorted by how often they land.</summary>
    public IReadOnlyList<string> ModeChoices { get; } =
        [Loc.Get("Curation_ModeDamage"), Loc.Get("Curation_ModeDetriments")];

    [ObservableProperty]
    private int _modeIndex;

    [ObservableProperty]
    private string? _selectedZone;

    [ObservableProperty]
    private string _statusLabel = "";

    private List<MinedMob> _mined = [];

    public CurationViewModel(SourceManager manager)
    {
        _manager = manager;
        foreach (var zone in manager.History.ArchivedZones())
            Zones.Add(zone);
        StatusLabel = Zones.Count == 0
            ? Loc.Get("Curation_StatusNoArchive")
            : Loc.Get("Curation_StatusPickZone");
    }

    partial void OnSelectedZoneChanged(string? value)
    {
        if (value is null)
            return;
        _ = MineAsync(value);
    }

    /// <summary>Off-thread — mining loads full swing lists for up to 500
    /// archived fights from SQLite, which used to freeze the UI for the
    /// whole load. A stale result (user already picked another zone) is
    /// dropped.</summary>
    private async Task MineAsync(string zone)
    {
        StatusLabel = Loc.Format("Curation_StatusMining", zone);
        var mined = await Task.Run(() => AbilityMiner.MineZone(_manager.History.EnumerateArchivedFights(zone), zone));
        if (SelectedZone != zone)
            return;
        _mined = mined;
        RebuildRows();
    }

    partial void OnModeIndexChanged(int value) => RebuildRows();

    private void RebuildRows()
    {
        Rows.Clear();
        var detriments = ModeIndex == 1;
        var abilities = 0;
        var missing = 0;
        var mobsShown = 0;
        foreach (var mob in _mined)
        {
            // Damage mode ranks by pain; detriment mode by how often the
            // control effect lands. Top 10 either way.
            List<MinedAbility> shown = detriments
                ? [.. mob.Abilities.Where(a => a.IsDetriment).OrderByDescending(a => a.Casts).Take(10)]
                : [.. mob.Abilities.Where(a => !a.IsDetriment).Take(10)];
            if (shown.Count == 0)
                continue;
            mobsShown++;
            Rows.Add(new CurationMobRow(mob.Mob, shown.Count));
            foreach (var mined in shown)
            {
                var row = new CurationAbilityRow(mined) { HasTimer = TimerExists(mined) };
                Rows.Add(row);
                abilities++;
                if (!row.HasTimer && row.CanTime)
                    missing++;
            }
        }
        StatusLabel = _mined.Count == 0
            ? Loc.Get("Curation_StatusNoFightsInZone")
            : mobsShown == 0
                ? (detriments ? Loc.Get("Curation_StatusNoDetriments") : Loc.Get("Curation_StatusNothingToShow"))
                : SummaryLabel(mobsShown, abilities, missing);
    }

    /// <summary>The footer after a zone is mined: three counts, each a
    /// whole dictionary string, joined — never one sentence assembled
    /// around English plurals.</summary>
    internal static string SummaryLabel(int mobs, int abilities, int missing) => string.Join(
        " · ",
        Loc.Format(mobs == 1 ? "Curation_MobsOne" : "Curation_MobsMany", mobs),
        Loc.Format(abilities == 1 ? "Curation_AbilitiesOne" : "Curation_AbilitiesMany", abilities),
        Loc.Format("Curation_TimeableWithoutTimer", missing));

    /// <summary>Exact identity — zone + mob + ability. A timer for MMIS's
    /// Mayong never marks ToNT's Vampire Lord version as covered. A
    /// multi-name category ("a|b|c" split mobs) covers each of its mobs.</summary>
    private bool TimerExists(MinedAbility mined) =>
        _manager.SpellTimers.Definitions.Any(d =>
            string.Equals(d.Name, mined.Ability, StringComparison.OrdinalIgnoreCase)
            && d.Category.Split('|').Any(c => string.Equals(c.Trim(), mined.Mob, StringComparison.OrdinalIgnoreCase))
            && string.Equals(d.Zone, mined.Zone, StringComparison.OrdinalIgnoreCase));

    private static TimerDefinition BuildDefinition(MinedAbility mined)
    {
        // Base (swipe-adjusted) duration: the live engine re-applies the
        // 1.5× stretch whenever the mob is actually swiped.
        var duration = (int)Math.Round(mined.BaseIntervalSeconds ?? mined.MedianIntervalSeconds ?? 30);
        return new TimerDefinition
        {
            Name = mined.Ability,
            // Filed Zone → Mob, and category-locked to the mob: the timer
            // only starts when this mob casts the ability, so same-named
            // abilities on other mobs never cross-trigger. EXCEPT when the
            // source was inferred — those lines name no caster, so at
            // runtime the attacker is "Unknown" and a category lock could
            // never match. Zone-qualified identity still keeps twins apart.
            Category = mined.Mob,
            Zone = mined.Zone,
            DamageType = mined.DamageTypes,
            ControlEffect = Vocabulary.CanonicalControlEffect(mined.EffectKind) ?? "",
            RestrictToCategory = !mined.SourceInferred,
            // Recast timers: every hit restarts the ONE bar — no
            // per-victim sub-bars from AoE stragglers.
            OnlyMasterTicks = true,
            DurationSeconds = Math.Max(5, duration),
            WarningSeconds = Math.Clamp(duration / 4, 3, 10),
            // The "tts" convention: bare "tts" speaks the timer name at start
            // ("it just landed") and "<name> soon" at the warning mark.
            StartSoundData = "tts",
            WarningSoundData = "tts",
        };
    }

    [RelayCommand]
    private void CreateTimer(CurationAbilityRow? row)
    {
        if (row is null || row.HasTimer || !row.CanTime)
            return;
        var definition = BuildDefinition(row.Mined);
        _manager.SpellTimers.AddOrUpdate(definition);
        row.HasTimer = true;
        StatusLabel = Loc.Format("Curation_StatusTimerCreated", row.Ability, definition.DurationSeconds);
    }

    [RelayCommand]
    private void CopyXml(CurationAbilityRow? row)
    {
        if (row is null)
            return;
        Clipboard.SetText(ActShareFormat.Export(BuildDefinition(row.Mined)));
        StatusLabel = Loc.Format("Curation_StatusXmlCopied", row.Ability);
    }

    /// <summary>Every timeable, timer-less ability in the zone as one XML
    /// block — the bulk hand-off to Lexicon curation.</summary>
    [RelayCommand]
    private void CopyMissingXml()
    {
        List<string> lines = [];
        foreach (var item in Rows)
        {
            if (item is CurationAbilityRow { HasTimer: false, CanTime: true } row)
                lines.Add(ActShareFormat.Export(BuildDefinition(row.Mined)));
        }
        if (lines.Count == 0)
        {
            StatusLabel = Loc.Get("Curation_StatusNothingMissing");
            return;
        }
        Clipboard.SetText(string.Join(Environment.NewLine, lines));
        StatusLabel = Loc.Format(
            lines.Count == 1 ? "Curation_StatusMissingCopiedOne" : "Curation_StatusMissingCopiedMany", lines.Count);
    }
}
