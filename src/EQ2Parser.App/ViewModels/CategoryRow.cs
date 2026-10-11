using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EQ2Parser.App.Localization;

namespace EQ2Parser.App.ViewModels;

/// <summary>Anything a category drag-drop can land on: headers accept into
/// themselves, rows accept into their own category. Rows are also the drag
/// sources; IsDropTarget doubles as the hover highlight and landing flash.</summary>
public interface ICategoryDropTarget
{
    string CategoryName { get; }

    /// <summary>Zone/section discriminator: the same category name exists
    /// per zone (and per Custom/Lexicon section), so drop identity is the
    /// (scope, name) PAIR — comparing names alone wrongly rejected drops
    /// onto a same-named category in a different zone.</summary>
    string CategoryScope { get; }

    bool IsDropTarget { get; set; }
}

/// <summary>Master section divider in the trigger/timer trees — the
/// user's Custom set vs the read-only synced Lexicon set.</summary>
public sealed record SectionRow(string Title, string Subtitle = "");

/// <summary>
/// The Section → Zone → Category → row tree shared by the Triggers and
/// Timers pages: rebuild (filter-aware, with remembered collapse state),
/// zone/category toggles, the drag-landing/save reveal, and in-place
/// enabled-count patching. The two pages used to duplicate (and had
/// already drifted on) ~200 lines of this machinery.
/// </summary>
public sealed class CategoryTree<TItem>
{
    public required Func<TItem, string> ZoneOf { get; init; }
    public required Func<TItem, string> CategoryOf { get; init; }
    public required Func<TItem, bool> IsLexicon { get; init; }
    public required Func<TItem, bool> EnabledOf { get; init; }
    public required Func<TItem, string> SortKeyOf { get; init; }
    public required Func<TItem, object> MakeRow { get; init; }

    /// <summary>Host name shown in the Lexicon section subtitle.</summary>
    public required Func<string> LexiconHost { get; init; }

    public ObservableCollection<object> Rows { get; } = [];

    private readonly HashSet<string> _collapsedZones = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expandedCategories = new(StringComparer.OrdinalIgnoreCase);

    public string ZoneKeyOf(TItem item)
    {
        var zone = ZoneOf(item);
        return zone.Length > 0 ? zone : "General";
    }

    /// <summary>Collapse/expand + header-count key: lexicon rows live in
    /// their own section, so the same zone name never shares state with a
    /// custom zone of the same name.</summary>
    public string TagFor(TItem item) =>
        IsLexicon(item) ? $"lex|{ZoneKeyOf(item)}" : ZoneKeyOf(item);

    public void Rebuild(IReadOnlyCollection<TItem> all, Func<TItem, bool> matchesFilter, bool filtering)
    {
        Rows.Clear();
        List<TItem> custom = [.. all.Where(i => !IsLexicon(i))];
        List<TItem> lexicon = [.. all.Where(IsLexicon)];
        if (lexicon.Count > 0)
            Rows.Add(new SectionRow(Loc.Get("CategoryTree_SectionCustom"), Loc.Get("CategoryTree_SectionCustomHint")));
        BuildSection(custom, matchesFilter, filtering, keyPrefix: "");
        if (lexicon.Count > 0)
        {
            // "Lexicon" is the site's name and stays as it is.
            Rows.Add(new SectionRow("Lexicon", Loc.Format("CategoryTree_SectionLexiconHint", LexiconHost())));
            BuildSection(lexicon, matchesFilter, filtering, keyPrefix: "lex|");
        }
    }

    /// <summary>What a zone header shows. The key of the no-zone group is
    /// the literal "General" everywhere it is compared, stored or used as
    /// collapse state; only the header's text is translated.</summary>
    private static string ZoneTitle(string zoneKey) =>
        string.Equals(zoneKey, GeneralZoneKey, StringComparison.OrdinalIgnoreCase)
            ? Loc.Get("CategoryTree_ZoneGeneral")
            : zoneKey;

    private const string GeneralZoneKey = "General";

    private void BuildSection(List<TItem> items, Func<TItem, bool> matchesFilter, bool filtering, string keyPrefix)
    {
        foreach (var zoneGroup in items
                     .GroupBy(ZoneKeyOf, StringComparer.OrdinalIgnoreCase)
                     // "General" (the site's cross-boss categories + local
                     // uncategorised triggers) pins to the top — it was
                     // getting lost alphabetically among the boss zones.
                     .OrderBy(g => string.Equals(g.Key, "General", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                     .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            List<TItem> zoneMembers = [.. zoneGroup.Where(matchesFilter)];
            if (zoneMembers.Count == 0)
                continue;
            var zoneKey = keyPrefix + zoneGroup.Key;
            // Zones default OPEN (there are few); categories default closed.
            var zoneExpanded = filtering || !_collapsedZones.Contains(zoneKey);
            Rows.Add(new ZoneRow(ZoneTitle(zoneGroup.Key), zoneExpanded)
            {
                Key = zoneKey,
                Count = zoneMembers.Count,
                EnabledCount = zoneMembers.Count(EnabledOf),
            });
            if (!zoneExpanded)
                continue;
            foreach (var group in zoneMembers
                         .GroupBy(CategoryOf, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                List<TItem> members = [.. group.OrderBy(SortKeyOf, StringComparer.OrdinalIgnoreCase)];
                var expandKey = $"{zoneKey}|{group.Key}";
                // A filter opens everything it touches; otherwise remembered state.
                var expanded = filtering || _expandedCategories.Contains(expandKey);
                Rows.Add(new CategoryRow(group.Key, expanded)
                {
                    Tag = zoneKey,
                    Count = members.Count,
                    EnabledCount = members.Count(EnabledOf),
                });
                if (!expanded)
                    continue;
                foreach (var item in members)
                    Rows.Add(MakeRow(item));
            }
        }
    }

    /// <summary>Flip a zone's collapse state (caller rebuilds).</summary>
    public void ToggleZone(ZoneRow row)
    {
        if (!_collapsedZones.Add(row.Key))
            _collapsedZones.Remove(row.Key);
    }

    /// <summary>Flip a category's expand state (caller rebuilds).</summary>
    public void ToggleCategory(CategoryRow row)
    {
        var key = $"{row.Tag}|{row.Name}";
        if (!_expandedCategories.Add(key))
            _expandedCategories.Remove(key);
    }

    /// <summary>Make a custom-section category visible — used after a save
    /// or drag-move so the affected row is on screen (caller rebuilds).</summary>
    public void Reveal(string zone, string category)
    {
        var zoneKey = zone.Length > 0 ? zone : "General";
        _collapsedZones.Remove(zoneKey);
        _expandedCategories.Add($"{zoneKey}|{category}");
    }

    /// <summary>In-place header-count patch for an enable flip — a full
    /// rebuild would reset the list's scroll on every checkbox click.</summary>
    public void PatchEnabledCounts(string categoryName, string tag, bool enabled)
    {
        foreach (var item in Rows)
        {
            if (item is CategoryRow header
                && header.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(header.Tag, tag, StringComparison.OrdinalIgnoreCase))
                header.EnabledCount += enabled ? 1 : -1;
            else if (item is ZoneRow zone
                && zone.Key.Equals(tag, StringComparison.OrdinalIgnoreCase))
                zone.EnabledCount += enabled ? 1 : -1;
        }
    }
}

/// <summary>Top-level zone header in the trigger/timer trees — grouping
/// only, deliberately NOT a drop target (drops land on mobs or their rows).</summary>
public sealed partial class ZoneRow(string name, bool expanded) : ObservableObject
{
    public string Name { get; } = name;

    /// <summary>Collapse-state key — distinct from Name when the same zone
    /// exists in both the Custom and Lexicon sections.</summary>
    public string Key { get; init; } = name;

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private int _enabledCount;

    [ObservableProperty]
    private bool _isExpanded = expanded;
}

/// <summary>A collapsible category header in a categorised list (triggers,
/// timers) — also the drop target when dragging an item into the category.</summary>
public sealed partial class CategoryRow(string name, bool expanded) : ObservableObject, ICategoryDropTarget
{
    public string Name { get; } = name;
    public string CategoryName => Name;
    public string CategoryScope => Tag ?? "";

    /// <summary>Optional parent-group marker (the timer tree stores the
    /// zone here) — lets composite trees key expansion and drops.</summary>
    public string? Tag { get; init; }

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private int _enabledCount;

    [ObservableProperty]
    private bool _isExpanded = expanded;

    [ObservableProperty]
    private bool _isDropTarget;
}
