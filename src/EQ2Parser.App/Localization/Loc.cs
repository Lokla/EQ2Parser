using System.IO;
using System.Reflection;
using System.Text.Json;

namespace EQ2Parser.App.Localization;

/// <summary>
/// Interface localization: flat key → string dictionaries embedded as
/// <c>Localization/strings.{lang}.json</c>. Deliberately JSON + a static
/// lookup instead of resx/satellite assemblies — no VS-only codegen in the
/// build, and community translators can PR a single readable file.
///
/// Static by design: <see cref="Initialize"/> runs once at startup before
/// any window loads, and a language change applies on restart. Every
/// lookup falls back English → key, so a missing translation shows
/// readable English (or, worst case, the key) rather than blank UI.
/// Parse grammar, log data, and report output are NOT localized — logs
/// are English-client-shaped and reports are power-user output.
/// </summary>
public static class Loc
{
    /// <summary>Supported UI languages: code → native display name.
    /// "" = follow the OS (mapped at init; unsupported OS languages fall
    /// back to English).</summary>
    public static readonly IReadOnlyList<(string Code, string DisplayName)> Languages =
    [
        ("", "System"),
        ("en", "English"),
        ("de", "Deutsch"),
        ("fr", "Français"),
        ("ru", "Русский"),
    ];

    private static Dictionary<string, string> _strings = [];
    private static Dictionary<string, string> _english = [];

    /// <summary>The resolved two-letter code after Initialize ("en" when
    /// following an unsupported OS language).</summary>
    public static string ActiveLanguage { get; private set; } = "en";

    /// <summary>Load the language dictionaries. Call once, before any
    /// window is constructed — the XAML markup extension resolves at load
    /// time. <paramref name="languageCode"/> "" follows the OS UI culture
    /// when supported (Russian Windows → Russian), else English (Croatian
    /// Windows → English). Rule lives in Core.UiLanguage for testability.</summary>
    public static void Initialize(string languageCode)
    {
        var code = Core.UiLanguage.Resolve(
            languageCode,
            System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            [.. Languages.Select(l => l.Code).Where(c => c.Length > 0)]);
        ActiveLanguage = code;
        _english = LoadEmbedded("en");
        _strings = code == "en" ? _english : LoadEmbedded(code);
    }

    /// <summary>The translated string, falling back active → English → the
    /// key itself (a visible-but-readable failure, never a crash).</summary>
    public static string Get(string key)
    {
        if (_strings.TryGetValue(key, out var s))
            return s;
        if (_english.TryGetValue(key, out var en))
            return en;
        return key;
    }

    /// <summary>Composite-format lookup for strings with placeholders.
    /// A malformed translated format never crashes the UI — it falls back
    /// to the English format, then to the raw pattern.</summary>
    public static string Format(string key, params object?[] args)
    {
        var pattern = Get(key);
        try
        {
            return string.Format(pattern, args);
        }
        catch (FormatException)
        {
            if (_english.TryGetValue(key, out var en) && !ReferenceEquals(en, pattern))
            {
                try
                {
                    return string.Format(en, args);
                }
                catch (FormatException)
                {
                }
            }
            return pattern;
        }
    }

    private static Dictionary<string, string> LoadEmbedded(string code)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream($"EQ2Parser.App.Localization.strings.{code}.json");
            if (stream is null)
                return [];
            using var reader = new StreamReader(stream);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd()) ?? [];
        }
        catch (Exception)
        {
            // A corrupt dictionary degrades to English/keys, never a crash.
            return [];
        }
    }
}
