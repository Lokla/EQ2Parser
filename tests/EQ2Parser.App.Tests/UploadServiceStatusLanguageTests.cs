using System.Net;
using System.Reflection;
using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Upload;

namespace EQ2Parser.App.Tests;

/// <summary>The upload queue lives in Core and cannot reach the app's
/// dictionaries, so its status lines were English for every user. The
/// service now hands it <see cref="UploadService.LocalizedUploadMessages"/>:
/// one <c>UploadQ_*</c> key per line. These tests hold the two sets of
/// English together, check every line is overridden, and see a translated
/// line arrive on the status the Settings card shows.</summary>
public sealed class UploadServiceStatusLanguageTests : IDisposable
{
    private const string VarsoonLog = @"C:\EQ2\logs\Varsoon\eq2log_Tester.txt";

    private readonly TestDataDir _dir = new();
    private readonly FakeLexiconSite _site = new();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        // Loc is process-wide: leave it as every other test class expects.
        Loc.Initialize("en");
        _dir.Dispose();
    }

    /// <summary>Every status-line method the queue can call.</summary>
    private static MethodInfo[] Lines() =>
    [
        .. typeof(UploadQueueMessages)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.IsVirtual && m.ReturnType == typeof(string))
            .OrderBy(m => m.Name, StringComparer.Ordinal),
    ];

    /// <summary>Recognisable arguments for one line: each string parameter
    /// gets its own marker, each number its own value, so a line that drops
    /// or swaps one shows.</summary>
    private static object[] SampleArguments(MethodInfo line) =>
    [
        .. line.GetParameters().Select((p, i) => p.ParameterType == typeof(string) ? (object)$"<{p.Name}>" : 101 + i),
    ];

    private static string Call(UploadQueueMessages messages, MethodInfo line, object[] arguments) =>
        (string)messages.GetType().GetMethod(line.Name, [.. line.GetParameters().Select(p => p.ParameterType)])!.Invoke(messages, arguments)!;

    [Fact]
    public void Every_Queue_Line_Is_Overridden_By_The_Localized_Set()
    {
        // A line added to the queue without a translation would stay
        // English for every user, silently.
        Assert.Equal(11, Lines().Length);
        foreach (var line in Lines())
        {
            var overriding = typeof(UploadService.LocalizedUploadMessages)
                .GetMethod(line.Name, [.. line.GetParameters().Select(p => p.ParameterType)])!;
            Assert.True(
                overriding.DeclaringType == typeof(UploadService.LocalizedUploadMessages),
                $"{line.Name} is not overridden by LocalizedUploadMessages");
        }
    }

    [Fact]
    public void English_Dictionary_Lines_Are_The_Queue_Defaults_Word_For_Word()
    {
        // Core's defaults are what the Core tests assert; the app shows the
        // dictionary's. Two copies of the same English, kept equal here.
        Loc.Initialize("en");
        foreach (var line in Lines())
        {
            var arguments = SampleArguments(line);
            Assert.Equal(
                Call(UploadQueueMessages.English, line, arguments),
                Call(UploadService.LocalizedUploadMessages.Instance, line, arguments));
        }
    }

    [Theory]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void Translated_Lines_Carry_Every_Argument_And_Are_Not_The_English(string language)
    {
        Loc.Initialize(language);
        foreach (var line in Lines())
        {
            var arguments = SampleArguments(line);
            var translated = Call(UploadService.LocalizedUploadMessages.Instance, line, arguments);

            Assert.NotEqual(Call(UploadQueueMessages.English, line, arguments), translated);
            Assert.False(translated.StartsWith("UploadQ_", StringComparison.Ordinal), $"{language}: {line.Name} rendered as its key");
            // The site's text, the fight's title and every figure are
            // inserted exactly as given.
            foreach (var argument in arguments)
                Assert.Contains(argument.ToString()!, translated);
        }
    }

    [Fact]
    public async Task A_German_User_Sees_The_Queue_Speak_German()
    {
        Loc.Initialize("de");
        _site.Refusals["Kaladim"] = (HttpStatusCode.Forbidden, "some-code", "Server 'Kaladim' is not on the allowed list.");
        using var svc = _site.NewService();

        svc.OnEncounterEnded(FakeLexiconSite.Fight(VarsoonLog));
        await FakeLexiconSite.WaitFor(() => svc.Status.Contains("hochgeladen (inserted)"), "the uploaded line");
        Assert.Equal("Lord Bob — hochgeladen (inserted). 1 in dieser Sitzung hochgeladen.", svc.Status);

        // The site's detail goes in as it arrived, inside the German line.
        svc.OnEncounterEnded(FakeLexiconSite.Fight(@"C:\EQ2\logs\Kaladim\eq2log_Tester.txt", "Lady Alice"));
        await FakeLexiconSite.WaitFor(() => svc.Status.Contains("abgelehnt"), "the refused line");
        Assert.Equal("Upload von Lady Alice abgelehnt: Server 'Kaladim' is not on the allowed list.", svc.Status);
    }

    [Fact]
    public async Task A_Pause_Is_A_State_Whatever_Language_Says_So()
    {
        // No English "paused" in this line — which is why callers and tests
        // read UploadsPaused and never search the text.
        Loc.Initialize("ru");
        _site.Refusals["Varsoon"] = (HttpStatusCode.Unauthorized, null, "Invalid or revoked token");
        using var svc = _site.NewService();

        svc.OnEncounterEnded(FakeLexiconSite.Fight(VarsoonLog));
        await FakeLexiconSite.WaitFor(() => svc.UploadsPaused, "the pause");
        await FakeLexiconSite.WaitFor(() => svc.Status.Contains("401"), "the pause line");

        Assert.Equal("загрузки приостановлены — токен отклонён (401). Обновите его и нажмите «Тест».", svc.Status);
        Assert.DoesNotContain("paused", svc.Status);
    }

    [Fact]
    public void The_Folder_Name_Line_Is_Translated_Too()
    {
        // The one UploadQ_ line the service writes itself, before anything
        // reaches the queue.
        Loc.Initialize("fr");
        using var svc = _site.NewService();

        Assert.False(svc.ShouldAutoUpload(FakeLexiconSite.Fight(@"C:\EQ2\logs\2nd account\eq2log_Tester.txt")));

        Assert.StartsWith("Lord Bob non envoyé — son log est dans un dossier nommé « 2nd account »", svc.Status, StringComparison.Ordinal);
    }
}
