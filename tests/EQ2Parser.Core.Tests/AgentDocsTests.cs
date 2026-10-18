using System.Text.RegularExpressions;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// Keeps the agent-facing docs honest. CLAUDE.md and the skills under
/// .claude/skills name files so an agent can go straight to them; a rename
/// that leaves a stale path behind sends every later session to a file that
/// is not there. Source-tree test, like LocalizationTests and LayeringTests.
/// </summary>
public class AgentDocsTests
{
    /// <summary>A backticked token is checked when it starts with one of
    /// these. Anything else in backticks (symbols, commands, paths in the
    /// sibling EQ2Lexicon repo, build output under artifacts/, bin/ or obj/) is left alone.</summary>
    private static readonly string[] RepoPrefixes =
    [
        "src/", "tests/", "scripts/", "docs/", ".github/", ".githooks/", ".claude/",
    ];

    private static readonly string[] RootFiles =
    [
        "CLAUDE.md", "README.md", "CONTRIBUTING.md", "SECURITY.md", "LICENSE",
        "Directory.Build.props", "global.json", "EQ2Parser.slnx", ".editorconfig", ".gitattributes",
    ];

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "EQ2Parser.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "could not locate the repo root (EQ2Parser.slnx) above the test bin dir");
        return dir!;
    }

    private static string SkillsDir() => Path.Combine(RepoRoot(), ".claude", "skills");

    private static IEnumerable<string> DocFiles()
    {
        yield return Path.Combine(RepoRoot(), "CLAUDE.md");
        foreach (var file in Directory.EnumerateFiles(SkillsDir(), "*.md", SearchOption.AllDirectories))
            yield return file;
    }

    /// <summary>The checkable part of a backticked token, or null when it is
    /// not a concrete repo path (a glob, a placeholder, a command line).</summary>
    private static string? AsRepoPath(string token)
    {
        var t = token.Trim().TrimEnd('.', ',', ';', ':');
        if (t.Length == 0 || t.IndexOfAny([' ', '<', '>', '*', '{', '}', '$', '%', '|', '=', '(', ')']) >= 0)
            return null;
        if (t.Contains("...", StringComparison.Ordinal) || t.Contains('\\'))
            return null;
        // `path/File.cs:42` style references: check the file part.
        t = Regex.Replace(t, @":\d+(-\d+)?$", "");
        // Build output (bin/, obj/) exists only after a build of that
        // project — the WPF app is never built on macOS or Linux.
        if (Regex.IsMatch(t, @"/(bin|obj)(/|$)"))
            return null;
        if (RootFiles.Contains(t, StringComparer.Ordinal))
            return t;
        return RepoPrefixes.Any(p => t.StartsWith(p, StringComparison.Ordinal)) ? t : null;
    }

    [Fact]
    public void Every_Repo_Path_Named_In_Agent_Docs_Exists()
    {
        var root = RepoRoot();
        List<string> missing = [];
        foreach (var doc in DocFiles())
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(doc))
            {
                lineNumber++;
                foreach (Match m in Regex.Matches(line, "`([^`]+)`"))
                {
                    var path = AsRepoPath(m.Groups[1].Value);
                    if (path is null)
                        continue;
                    var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(full) && !Directory.Exists(full))
                        missing.Add($"{Path.GetRelativePath(root, doc)}:{lineNumber} names `{path}`");
                }
            }
        }
        Assert.True(missing.Count == 0,
            "Agent docs name paths that do not exist. Fix the doc, or the rename that orphaned it.\n"
            + string.Join("\n", missing));
    }

    [Fact]
    public void Every_Skill_Has_Frontmatter_A_Prefixed_Name_And_The_Repo_Guard()
    {
        var skills = Directory.GetDirectories(SkillsDir());
        Assert.NotEmpty(skills);
        List<string> problems = [];
        foreach (var dir in skills)
        {
            var name = Path.GetFileName(dir);
            var skillFile = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(skillFile))
            {
                problems.Add($"{name}: no SKILL.md");
                continue;
            }
            var lines = File.ReadAllLines(skillFile);
            var end = lines.Length > 0 && lines[0] == "---" ? Array.IndexOf(lines, "---", 1) : -1;
            if (end < 0)
            {
                problems.Add($"{name}: SKILL.md does not open with a --- frontmatter block");
                continue;
            }
            var front = lines[1..end];
            // Skills from every checked-out repo share one namespace in a
            // session, so an unprefixed name here can shadow another repo's.
            if (!name.StartsWith("parser-", StringComparison.Ordinal))
                problems.Add($"{name}: skill directories are named parser-*");
            if (!front.Contains($"name: {name}"))
                problems.Add($"{name}: frontmatter name must equal the directory name");
            if (!front.Any(l => l.StartsWith("description: ", StringComparison.Ordinal) && l.Length > 60))
                problems.Add($"{name}: frontmatter needs a one-line description that says when to use it");
            // This repo's CLAUDE.md is not loaded when it is an additional
            // directory, so each skill carries the wrong-repo guard itself.
            if (!lines.Contains("## Which repo"))
                problems.Add($"{name}: missing the '## Which repo' guard section");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Skill_Reference_Links_Resolve()
    {
        List<string> missing = [];
        foreach (var dir in Directory.GetDirectories(SkillsDir()))
        {
            foreach (var doc in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(doc), @"`(references/[A-Za-z0-9._/-]+\.md)`"))
                {
                    var target = Path.Combine(dir, m.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(target))
                        missing.Add($"{Path.GetFileName(dir)}/{Path.GetFileName(doc)} names `{m.Groups[1].Value}`");
                }
            }
        }
        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    /// <summary>Windows PowerShell 5.1 reads a BOM-less script as ANSI, so one
    /// stray smart quote or dash breaks parsing for anyone on the in-box
    /// shell. It has happened to scripts/release.ps1.</summary>
    [Fact]
    public void PowerShell_Scripts_Are_Ascii_Only()
    {
        var root = RepoRoot();
        List<string> offenders = [];
        foreach (var script in Directory.EnumerateFiles(Path.Combine(root, "scripts"), "*.ps1", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(script);
            var at = Array.FindIndex(bytes, b => b > 127);
            if (at >= 0)
                offenders.Add($"{Path.GetRelativePath(root, script)}: non-ASCII byte at offset {at}");
        }
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }
}
