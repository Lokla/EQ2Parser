using System.Reflection.Metadata;
using System.Text.Json;
using System.Text.RegularExpressions;
using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// Interface-localization integrity. The dictionaries live in the App
/// project (Localization/strings.{lang}.json); these tests reach them via
/// the repo tree so a missing key or a typo'd {loc:Tr …} reference fails
/// CI instead of silently rendering the raw key at runtime. That holds for
/// keys built at run time too: each such family has its own test, and a
/// tripwire fails on a dynamic Loc call that no family test covers.
/// </summary>
public class LocalizationTests
{
    private static readonly string[] LanguageCodes = ["en", "de", "fr", "ru"];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EQ2Parser.slnx")))
            dir = dir.Parent!;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    private static string AppDir() => Path.Combine(RepoRoot(), "src", "EQ2Parser.App");

    private static Dictionary<string, string> LoadLanguage(string code)
    {
        var path = Path.Combine(AppDir(), "Localization", $"strings.{code}.json");
        Assert.True(File.Exists(path), $"missing dictionary: {path}");
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        Assert.NotNull(parsed);
        return parsed;
    }

    [Fact]
    public void Every_Language_Has_Exactly_The_English_Key_Set()
    {
        var english = LoadLanguage("en");
        Assert.NotEmpty(english);
        foreach (var code in LanguageCodes.Skip(1))
        {
            var lang = LoadLanguage(code);
            var missing = english.Keys.Except(lang.Keys).ToList();
            var extra = lang.Keys.Except(english.Keys).ToList();
            Assert.True(missing.Count == 0, $"{code} missing keys: {string.Join(", ", missing.Take(10))}");
            Assert.True(extra.Count == 0, $"{code} extra keys: {string.Join(", ", extra.Take(10))}");
        }
    }

    [Fact]
    public void Placeholders_Match_English_In_Every_Language()
    {
        var english = LoadLanguage("en");
        foreach (var code in LanguageCodes.Skip(1))
        {
            var lang = LoadLanguage(code);
            foreach (var (key, en) in english)
            {
                if (!lang.TryGetValue(key, out var translated))
                    continue; // covered by the key-set test
                var enSlots = Placeholders(en);
                var trSlots = Placeholders(translated);
                Assert.True(enSlots.SetEquals(trSlots),
                    $"{code}:{key} placeholders {{{string.Join(",", trSlots)}}} != english {{{string.Join(",", enSlots)}}}");
            }
        }
    }

    [Fact]
    public void Every_Referenced_Key_Exists_In_English()
    {
        var english = LoadLanguage("en");
        var referenced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var xaml in Directory.EnumerateFiles(AppDir(), "*.xaml", SearchOption.AllDirectories))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(xaml), @"\{loc:Tr\s+([A-Za-z0-9_]+)\}"))
                referenced.Add(m.Groups[1].Value);
        }
        foreach (var cs in Directory.EnumerateFiles(AppDir(), "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(cs), @"Loc\.(?:Get|Format)\(\s*""([^""]+)"""))
                referenced.Add(m.Groups[1].Value);
        }

        Assert.NotEmpty(referenced);
        var unknown = referenced.Except(english.Keys).OrderBy(k => k).ToList();
        Assert.True(unknown.Count == 0,
            $"referenced but not in strings.en.json: {string.Join(", ", unknown.Take(15))}");
    }

    private static HashSet<string> Placeholders(string s) =>
        [.. Regex.Matches(s, @"\{(\d+)[^}]*\}").Select(m => m.Groups[1].Value)];

    /// <summary>Loc.Format swallows a FormatException and shows English
    /// instead, so a translation with a stray brace or a broken
    /// <c>{0:0.#}</c> never fails anything — it just quietly stops being
    /// translated. Every string that carries a placeholder is run through
    /// string.Format here, with arguments that accept any format spec.</summary>
    [Fact]
    public void Every_Placeholder_String_Is_A_Valid_Format_In_Every_Language()
    {
        var english = LoadLanguage("en");
        List<string> broken = [];
        foreach (var code in LanguageCodes)
        {
            var lang = LoadLanguage(code);
            foreach (var (key, en) in english)
            {
                var slots = Placeholders(en);
                if (slots.Count == 0 || !lang.TryGetValue(key, out var text))
                    continue;
                var args = new object[slots.Select(int.Parse).Max() + 1];
                Array.Fill(args, new AnyValue());
                try
                {
                    _ = string.Format(text, args);
                }
                catch (FormatException ex)
                {
                    broken.Add($"{code}:{key} ({ex.Message})");
                }
            }
        }
        Assert.True(broken.Count == 0, $"not a valid format string: {string.Join("; ", broken)}");
    }

    private sealed class AnyValue : IFormattable
    {
        public string ToString(string? format, IFormatProvider? formatProvider) => "x";
    }

    /// <summary>A file-dialog filter (a key ending in FileFilter) is
    /// "label|pattern|label|pattern", and WPF throws on one that is not.
    /// Only the labels are translated: the patterns must come through as
    /// English has them.</summary>
    [Fact]
    public void File_Dialog_Filters_Keep_Their_Patterns_In_Every_Language()
    {
        var english = LoadLanguage("en");
        var filters = english.Keys.Where(k => k.EndsWith("FileFilter", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(filters);
        foreach (var key in filters)
        {
            var expected = FilterPatterns(english[key]);
            Assert.True(expected.Count > 0, $"en:{key} is not label|pattern pairs");
            foreach (var code in LanguageCodes.Skip(1))
            {
                var actual = FilterPatterns(LoadLanguage(code)[key]);
                Assert.True(expected.SequenceEqual(actual),
                    $"{code}:{key} patterns [{string.Join(" ", actual)}] != english [{string.Join(" ", expected)}]");
            }
        }
    }

    /// <summary>The pattern halves of a filter string; empty when the
    /// string is not whole pairs.</summary>
    private static List<string> FilterPatterns(string filter)
    {
        var parts = filter.Split('|');
        return parts.Length % 2 != 0 || parts.Any(p => p.Length == 0)
            ? []
            : [.. parts.Where((_, i) => i % 2 == 1)];
    }

    /// <summary>JSON lets a key appear twice and the deserializer keeps the
    /// last one without a word, so two edits that each add the same key
    /// would both "work" and one translation would silently win.</summary>
    [Fact]
    public void No_Dictionary_Declares_A_Key_Twice()
    {
        foreach (var code in LanguageCodes)
        {
            var path = Path.Combine(AppDir(), "Localization", $"strings.{code}.json");
            var repeated = Regex.Matches(File.ReadAllText(path), @"^\s*""([^""]+)""\s*:", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value)
                .GroupBy(k => k, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            Assert.True(repeated.Count == 0, $"strings.{code}.json declares more than once: {string.Join(", ", repeated)}");
        }
    }

    // ── Keys that are not a plain literal at their call site ───────────────
    //
    // Every_Referenced_Key_Exists_In_English reads Loc.Get("Key") and
    // {loc:Tr Key}. A key assembled at run time is invisible to it, and a
    // missing one renders as the raw key in the app. So each such FAMILY
    // has a test below that enumerates it from whatever really drives it
    // (a Core list, or the literal pieces in the source file), and
    // Dynamic_Loc_Calls_Are_On_The_Allow_List fails on any call site that
    // has not been tied to one of those tests.

    private const string MethodGroup = "<method group>";

    /// <summary>One <c>Loc.Get</c> / <c>Loc.Format</c> reference in the
    /// App sources: where it is, and the text of its first argument with
    /// white space collapsed (<see cref="MethodGroup"/> when the method is
    /// named without being called).</summary>
    private sealed record LocCall(string File, int Line, string Method, string Argument);

    /// <summary>The App's own C# sources, by path relative to the project
    /// with forward slashes. Build output is left out.</summary>
    private static IEnumerable<(string File, string Text)> AppSources()
    {
        var root = AppDir();
        foreach (var cs in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, cs).Replace('\\', '/');
            if (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal))
                continue;
            yield return (relative, File.ReadAllText(cs));
        }
    }

    private static string AppSource(string relative) =>
        File.ReadAllText(Path.Combine(AppDir(), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static List<LocCall> LocCalls() =>
        [.. AppSources().SelectMany(s => ScanLocCalls(s.File, s.Text))];

    private static IEnumerable<LocCall> ScanLocCalls(string file, string text)
    {
        foreach (Match m in Regex.Matches(text, @"(?<![A-Za-z0-9_])Loc\.(Get|Format)\b"))
        {
            // A line that is all comment may talk about Loc.Get freely.
            var lineStart = text.LastIndexOf('\n', m.Index) + 1;
            if (text[lineStart..m.Index].TrimStart().StartsWith("//", StringComparison.Ordinal))
                continue;
            var line = 1 + text.Take(m.Index).Count(c => c == '\n');
            var i = m.Index + m.Length;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
            yield return i < text.Length && text[i] == '('
                ? new LocCall(file, line, m.Groups[1].Value, FirstArgument(text, i + 1))
                : new LocCall(file, line, m.Groups[1].Value, MethodGroup);
        }
    }

    /// <summary>The source text from just inside a call's opening bracket
    /// up to its first top-level comma or its closing bracket. Strings
    /// (plain, verbatim, raw and interpolated, with whatever sits in their
    /// holes) and char literals are stepped over whole, so a comma or a
    /// bracket inside one does not end the argument early.</summary>
    private static string FirstArgument(string text, int start)
    {
        var i = start;
        var depth = 0;
        while (i < text.Length)
        {
            if (StartsString(text, i))
            {
                i = SkipString(text, i);
                continue;
            }
            var c = text[i];
            if (c == '\'')
            {
                i = SkipChar(text, i);
                continue;
            }
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                    break;
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                break;
            }
            i++;
        }
        return Regex.Replace(text[start..Math.Min(i, text.Length)], @"\s+", " ").Trim();
    }

    private static bool StartsString(string text, int i)
    {
        while (i < text.Length && text[i] is '$' or '@')
            i++;
        return i < text.Length && text[i] == '"';
    }

    /// <summary>Index just past the string literal that starts at
    /// <paramref name="i"/> (at its quote, or at its $ / @ prefix).</summary>
    private static int SkipString(string text, int i)
    {
        var interpolated = false;
        var verbatim = false;
        for (; text[i] != '"'; i++)
        {
            interpolated |= text[i] == '$';
            verbatim |= text[i] == '@';
        }
        if (string.CompareOrdinal(text, i, "\"\"\"", 0, 3) == 0)
        {
            var close = text.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
            return close < 0 ? text.Length : close + 3;
        }
        i++;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                if (!verbatim || i + 1 >= text.Length || text[i + 1] != '"')
                    return i + 1;
                i += 2;
            }
            else if (c == '\\' && !verbatim)
            {
                i += 2;
            }
            else if (c == '{' && interpolated)
            {
                i = i + 1 < text.Length && text[i + 1] == '{' ? i + 2 : SkipHole(text, i + 1);
            }
            else
            {
                i++;
            }
        }
        return text.Length;
    }

    /// <summary>Index just past the closing brace of an interpolation
    /// hole; <paramref name="i"/> is the first character inside it.</summary>
    private static int SkipHole(string text, int i)
    {
        var depth = 0;
        while (i < text.Length)
        {
            if (StartsString(text, i))
            {
                i = SkipString(text, i);
                continue;
            }
            var c = text[i];
            if (c == '\'')
            {
                i = SkipChar(text, i);
                continue;
            }
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']')
            {
                depth--;
            }
            else if (c == '}')
            {
                if (depth == 0)
                    return i + 1;
                depth--;
            }
            i++;
        }
        return text.Length;
    }

    private static int SkipChar(string text, int i)
    {
        i++;
        while (i < text.Length && text[i] != '\'')
            i += text[i] == '\\' ? 2 : 1;
        return i + 1;
    }

    private static bool IsPlainLiteral(string argument) => Regex.IsMatch(argument, @"^""[^""\\]*""$");

    /// <summary>Both keys of an argument that picks between two literals
    /// (<c>n == 1 ? "X_One" : "X_Many"</c>), or null for any other shape.
    /// Such a key is not built at run time — both are in the source — but
    /// the literal scan does not see past the condition.</summary>
    private static string[]? ConditionalKeys(string argument)
    {
        var m = Regex.Match(argument, @"^[^""]*\?\s*""([A-Za-z0-9_]+)""\s*:\s*""([A-Za-z0-9_]+)""$");
        return m.Success ? [m.Groups[1].Value, m.Groups[2].Value] : null;
    }

    private static void AssertInEveryLanguage(IEnumerable<string> keys, string family)
    {
        var wanted = keys.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(wanted.Count > 0, $"{family}: the family came out empty — its scan no longer matches the source");
        foreach (var code in LanguageCodes)
        {
            var lang = LoadLanguage(code);
            var missing = wanted.Where(k => !lang.ContainsKey(k)).ToList();
            Assert.True(missing.Count == 0, $"{family}: strings.{code}.json has no {string.Join(", ", missing)}");
        }
    }

    /// <summary>The scanner is what every test below stands on, so it is
    /// pinned on the shapes the App really uses and on the ones that would
    /// fool a simpler reader.</summary>
    [Theory]
    [InlineData("x = Loc.Get(\"Plain_Key\");", "\"Plain_Key\"")]
    [InlineData("x = Loc.Format(\n    \"Split_Key\",\n    a, b);", "\"Split_Key\"")]
    [InlineData("x = Loc.Format(n == 1 ? \"A_One\" : \"A_Many\", n);", "n == 1 ? \"A_One\" : \"A_Many\"")]
    [InlineData("x = Loc.Get($\"Arch_{a}\");", "$\"Arch_{a}\"")]
    [InlineData("x = Loc.Format($\"C_{e}_{(n == 1 ? \"One\" : \"Many\")}\", n);", "$\"C_{e}_{(n == 1 ? \"One\" : \"Many\")}\"")]
    [InlineData("x = Loc.Format(prefix + (n == 1 ? \"One\" : \"Many\"), n);", "prefix + (n == 1 ? \"One\" : \"Many\")")]
    [InlineData("x = Loc.Get(Pick(a, b));", "Pick(a, b)")]
    [InlineData("x = Loc.Get(\"a, (b\" + c);", "\"a, (b\" + c")]
    [InlineData("x = Localization.Loc.Get(key);", "key")]
    [InlineData("Func<string, string> f = Loc.Get;", MethodGroup)]
    public void The_Loc_Call_Scanner_Reads_The_First_Argument(string source, string expected) =>
        Assert.Equal(expected, Assert.Single(ScanLocCalls("sample.cs", source)).Argument);

    [Theory]
    [InlineData("/// Loc.Get(key) returns the key when nothing is loaded.")]
    [InlineData("    // Loc.Format(pattern, args) falls back to English")]
    [InlineData("var x = MyLoc.Get(key);")]
    public void The_Loc_Call_Scanner_Skips_Comments_And_Other_Types(string source) =>
        Assert.Empty(ScanLocCalls("sample.cs", source));

    /// <summary>ExportWindow builds one checkbox per archetype with
    /// <c>Loc.Get($"Arch_{a}")</c>. The family is Core's
    /// <see cref="Archetype.All"/>: add an archetype without its four
    /// strings and this fails.</summary>
    [Fact]
    public void Archetype_Keys_Exist_In_Every_Language()
    {
        var source = AppSource("Views/ExportWindow.xaml.cs");
        Assert.Contains("Archetype.All", source);
        Assert.Contains("Loc.Get($\"Arch_{a}\")", source);

        AssertInEveryLanguage(Archetype.All.Select(a => $"Arch_{a}"), "Arch_");
    }

    /// <summary>ActImportFlow serves the Triggers and the Timers page with
    /// one body and a key prefix per page: every suffix it joins to
    /// <c>locPrefix</c> has to exist under every prefix a caller passes.
    /// Both halves are read from the source — the prefixes from the
    /// ImportXmlFile call sites, the suffixes from ActImportFlow.cs — so a
    /// new suffix, or a third page, without its strings fails.</summary>
    [Fact]
    public void Act_Import_Keys_Exist_For_Every_Page_In_Every_Language()
    {
        const string flow = "ViewModels/ActImportFlow.cs";
        var sources = AppSources().ToList();

        var callSites = sources.Sum(s => Regex.Count(s.Text, @"ActImportFlow\.ImportXmlFile\("));
        var prefixes = sources
            .SelectMany(s => Regex.Matches(s.Text, @"ActImportFlow\.ImportXmlFile\(\s*[^,""]+,\s*""([A-Za-z0-9_]+)""\s*,"))
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.True(prefixes.Count == callSites,
            $"{callSites} calls to ActImportFlow.ImportXmlFile but only {prefixes.Count} pass the key prefix as a literal — this test cannot enumerate the others");
        Assert.True(prefixes.Distinct().Count() >= 2, "expected the Triggers and the Timers page to call ActImportFlow.ImportXmlFile");

        List<string> suffixes = [];
        var calls = LocCalls().Where(c => c.File == flow).ToList();
        Assert.NotEmpty(calls);
        foreach (var call in calls)
        {
            var m = Regex.Match(call.Argument,
                @"^locPrefix \+ (?:""(?<a>[A-Za-z0-9_]+)""|\([^""]*\? ?""(?<a>[A-Za-z0-9_]+)"" ?: ?""(?<b>[A-Za-z0-9_]+)""\))$");
            Assert.True(m.Success,
                $"{flow}:{call.Line} Loc.{call.Method}({call.Argument}) is not locPrefix + \"Suffix\" or locPrefix + (cond ? \"A\" : \"B\") — teach this test the new shape");
            suffixes.Add(m.Groups["a"].Value);
            if (m.Groups["b"].Success)
                suffixes.Add(m.Groups["b"].Value);
        }

        AssertInEveryLanguage(
            from prefix in prefixes.Distinct()
            from suffix in suffixes
            select prefix + suffix,
            "ActImportFlow locPrefix");
    }

    /// <summary>The One/Many pairs (TriggersViewModel, TimersViewModel and
    /// the rest) and every other call that picks between two literal keys,
    /// such as the token-state lookup in SettingsViewModel. Read from the
    /// call sites themselves, wherever in the App they are.</summary>
    [Fact]
    public void Conditional_Keys_Exist_In_Every_Language()
    {
        var keys = LocCalls()
            .Select(c => ConditionalKeys(c.Argument))
            .Where(pair => pair is not null)
            .SelectMany(pair => pair!)
            .ToList();
        // The scan really reads the import summaries it was written for.
        Assert.Contains("TriggersVm_TriggersImportedOne", keys);
        Assert.Contains("TriggersVm_TriggersImportedMany", keys);
        Assert.Contains("TimersVm_TimersImportedOne", keys);
        Assert.Contains("TimersVm_TimersImportedMany", keys);

        AssertInEveryLanguage(keys, "conditional (cond ? \"A\" : \"B\") keys");
    }

    /// <summary>The toast for a mass-detriment callout is one whole
    /// sentence per control effect and per one/many:
    /// <c>Callout_{effect}_One</c> / <c>_Many</c>. The family is Core's
    /// <see cref="Vocabulary.ControlEffects"/>.</summary>
    [Fact]
    public void Callout_Keys_Exist_For_Every_Control_Effect_In_Every_Language()
    {
        Assert.Contains(
            "$\"Callout_{effect}_{(count == 1 ? \"One\" : \"Many\")}\"",
            AppSource("Services/SourceManager.cs"));

        AssertInEveryLanguage(
            from effect in Vocabulary.ControlEffects
            from form in CountForms
            select $"Callout_{effect}_{form}",
            "Callout_");
    }

    private static readonly string[] CountForms = ["One", "Many"];

    /// <summary>CrashLog looks its dialog strings up by key through one
    /// helper (it keeps an English copy for a crash that beats the
    /// dictionaries). The family is every <c>CrashDlg_</c> literal in
    /// CrashLog.cs — the table and the call sites alike.</summary>
    [Fact]
    public void Crash_Dialog_Keys_Exist_In_Every_Language() =>
        AssertInEveryLanguage(
            Regex.Matches(AppSource("Services/CrashLog.cs"), @"""(CrashDlg_[A-Za-z0-9_]+)""").Select(m => m.Groups[1].Value),
            "CrashDlg_");

    /// <summary>Every Loc call in the App whose key is not there to be
    /// read at the call site, each with the test that enumerates its
    /// family. One entry per call site.</summary>
    private static readonly (string File, string Argument, string CoveredBy)[] DynamicCallSites =
    [
        // The {loc:Tr Key} markup extension: its keys are the XAML scan's.
        ("Localization/TrExtension.cs", "Key", nameof(Every_Referenced_Key_Exists_In_English)),
        ("Views/ExportWindow.xaml.cs", "$\"Arch_{a}\"", nameof(Archetype_Keys_Exist_In_Every_Language)),
        ("ViewModels/ActImportFlow.cs", "locPrefix + \"ImportActXmlTitle\"", nameof(Act_Import_Keys_Exist_For_Every_Page_In_Every_Language)),
        ("ViewModels/ActImportFlow.cs", "locPrefix + \"XmlFileFilter\"", nameof(Act_Import_Keys_Exist_For_Every_Page_In_Every_Language)),
        ("ViewModels/ActImportFlow.cs", "locPrefix + \"FileReadError\"", nameof(Act_Import_Keys_Exist_For_Every_Page_In_Every_Language)),
        ("ViewModels/ActImportFlow.cs", "locPrefix + \"NotActXml\"", nameof(Act_Import_Keys_Exist_For_Every_Page_In_Every_Language)),
        ("ViewModels/ActImportFlow.cs", "locPrefix + (result.Timers.Count == 1 ? \"SpellTimersImportedOne\" : \"SpellTimersImportedMany\")", nameof(Act_Import_Keys_Exist_For_Every_Page_In_Every_Language)),
        ("ViewModels/ActImportFlow.cs", "locPrefix + (result.Triggers.Count == 1 ? \"TriggersImportedOne\" : \"TriggersImportedMany\")", nameof(Act_Import_Keys_Exist_For_Every_Page_In_Every_Language)),
        ("ViewModels/ActImportFlow.cs", "locPrefix + (linked == 1 ? \"LinkedTimersOne\" : \"LinkedTimersMany\")", nameof(Act_Import_Keys_Exist_For_Every_Page_In_Every_Language)),
        ("ViewModels/ActImportFlow.cs", "locPrefix + (result.Skipped == 1 ? \"EntriesSkippedOne\" : \"EntriesSkippedMany\")", nameof(Act_Import_Keys_Exist_For_Every_Page_In_Every_Language)),
        ("ViewModels/ActImportFlow.cs", "locPrefix + \"FileNoEntries\"", nameof(Act_Import_Keys_Exist_For_Every_Page_In_Every_Language)),
        ("Services/SourceManager.cs", "key", nameof(Callout_Keys_Exist_For_Every_Control_Effect_In_Every_Language)),
        ("Services/CrashLog.cs", "key", nameof(Crash_Dialog_Keys_Exist_In_Every_Language)),
    ];

    /// <summary>The tripwire. A Loc call passes when its first argument is
    /// a plain string literal (the literal scan checks it), or a choice
    /// between two literals (Conditional_Keys_Exist_In_Every_Language
    /// checks both, wherever the call is). Anything else — interpolation,
    /// concatenation, a variable, the method handed on as a delegate — is
    /// a key no test can see, and fails here until its call site is in
    /// <see cref="DynamicCallSites"/> beside a test that enumerates the
    /// family. Each entry stands for exactly one call site, so a second
    /// dynamic call in a listed file fails too.</summary>
    [Fact]
    public void Dynamic_Loc_Calls_Are_On_The_Allow_List()
    {
        var sources = AppSources().ToList();
        // Reaching Loc under another name would walk around this test.
        var renamed = sources
            .Where(s => Regex.IsMatch(s.Text, @"^\s*using\s[^;]*\bLocalization\.Loc\s*;", RegexOptions.Multiline))
            .Select(s => s.File)
            .ToList();
        Assert.True(renamed.Count == 0,
            $"Loc is imported by alias or `using static` in {string.Join(", ", renamed)} — call it as Loc.Get / Loc.Format so the key guards can see the call");

        var dynamic = sources
            .SelectMany(s => ScanLocCalls(s.File, s.Text))
            .Where(c => !IsPlainLiteral(c.Argument) && ConditionalKeys(c.Argument) is null)
            .ToList();
        var listed = DynamicCallSites
            .GroupBy(s => (s.File, s.Argument))
            .ToDictionary(g => g.Key, g => g.Count());

        List<string> problems = [];
        foreach (var group in dynamic.GroupBy(c => (c.File, c.Argument)))
        {
            var allowed = listed.GetValueOrDefault(group.Key);
            if (group.Count() > allowed)
            {
                var call = group.Key.Argument == MethodGroup
                    ? $"Loc.{group.First().Method} passed on as a delegate"
                    : $"Loc.{group.First().Method}({group.Key.Argument}, …)";
                problems.Add(
                    $"{group.Key.File}:{string.Join(",", group.Select(c => c.Line))}  {call}"
                    + $"  — {group.Count()} call site(s), {allowed} on the allow-list");
            }
        }
        Assert.True(problems.Count == 0,
            "A Loc call builds its key at run time, so no test can see whether the key exists:\n  "
            + string.Join("\n  ", problems)
            + "\nUse a literal key if you can. If the key really is a family, write a test that enumerates the family from its"
            + " source of truth and asserts every key in all four dictionaries, then add the call site to DynamicCallSites naming that test.");

        var seen = dynamic.GroupBy(c => (c.File, c.Argument)).ToDictionary(g => g.Key, g => g.Count());
        var stale = listed
            .Where(entry => seen.GetValueOrDefault(entry.Key) < entry.Value)
            .Select(entry => $"{entry.Key.File}: {entry.Key.Argument}")
            .ToList();
        Assert.True(stale.Count == 0,
            "DynamicCallSites lists call sites that are no longer in the source (moved, reworded or removed) — update the list:\n  "
            + string.Join("\n  ", stale));

        foreach (var site in DynamicCallSites)
        {
            var test = typeof(LocalizationTests).GetMethod(site.CoveredBy);
            Assert.True(test is not null && test.GetCustomAttributes(typeof(FactAttribute), inherit: true).Length > 0,
                $"DynamicCallSites names {site.CoveredBy} for {site.File}, which is not a test in this class");
        }
    }

    private static readonly string[] BuildConfigs = ["Release", "Debug"];

    /// <summary>The v0.2.5 regression: MSBuild's AssignCulture read the
    /// ".de"/".en" filename segment as a culture suffix and routed every
    /// dictionary into satellite assemblies — the app shipped rendering
    /// raw keys. Reads the built dll's manifest via metadata (no load) and
    /// requires every dictionary in the MAIN assembly. Skips when the App
    /// hasn't been built (test-only runs).</summary>
    [Fact]
    public void Dictionaries_Are_Embedded_In_The_Built_App_Assembly()
    {
        var binRoot = Path.Combine(AppDir(), "bin");
        var dll = BuildConfigs
            .SelectMany(cfg =>
            {
                var dir = Path.Combine(binRoot, cfg);
                return Directory.Exists(dir)
                    ? Directory.EnumerateFiles(dir, "EQ2Parser.App.dll", SearchOption.AllDirectories)
                    : [];
            })
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (dll is null)
            return; // app never built in this checkout — nothing to verify

        using var stream = File.OpenRead(dll);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var md = pe.GetMetadataReader();
        var names = md.ManifestResources
            .Select(h => md.GetString(md.GetManifestResource(h).Name))
            .ToList();
        foreach (var code in LanguageCodes)
            Assert.Contains($"EQ2Parser.App.Localization.strings.{code}.json", names);
    }

    // ── OS-language defaulting (the out-of-box rule) ───────────────────────

    private static readonly string[] Supported = ["en", "de", "fr", "ru"];

    [Theory]
    // No explicit setting: follow the OS when we ship that language...
    [InlineData("", "ru", "ru")]
    [InlineData("", "de", "de")]
    [InlineData("", "fr", "fr")]
    [InlineData("", "en", "en")]
    [InlineData(null, "ru", "ru")]
    // ...and fall back to English when we don't (Croatian, Japanese, ...).
    [InlineData("", "hr", "en")]
    [InlineData("", "ja", "en")]
    // An explicit user choice always wins over the OS.
    [InlineData("de", "ru", "de")]
    [InlineData("en", "ru", "en")]
    // A stale/unknown persisted code degrades to English.
    [InlineData("xx", "ru", "en")]
    public void Os_Language_Defaults_Are_Honoured(string? requested, string os, string expected) =>
        Assert.Equal(expected, UiLanguage.Resolve(requested, os, Supported));
}
