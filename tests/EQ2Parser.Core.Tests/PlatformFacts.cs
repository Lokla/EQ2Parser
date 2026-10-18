namespace EQ2Parser.Core.Tests;

/// <summary>A fact about Windows behaviour (path syntax, file-locking
/// semantics) that has no meaning elsewhere. Reported as skipped on other
/// platforms so a macOS or Linux run stays green without hiding the case.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only behaviour";
    }
}

/// <summary>The <see cref="WindowsFactAttribute"/> counterpart for theories,
/// e.g. cases written as Windows path literals.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only behaviour";
    }
}
