using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;
using Velopack;

namespace EQ2Parser.App.Tests;

/// <summary>
/// The update service builds Velopack's manager on first use, not when it
/// is constructed — so the service (and SourceManager, which owns one) can
/// be built in a process that never ran the Velopack bootstrap. These tests
/// never build a real manager: the seam's factory stands in for Velopack
/// refusing to construct one, so nothing is looked up or downloaded.
/// </summary>
public sealed class UpdateServiceTests : IDisposable
{
    private readonly TestDataDir _dir = new();

    public UpdateServiceTests() => Loc.Initialize("en");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _dir.Dispose();
    }

    /// <summary>What Velopack throws when the process has no locator.</summary>
    private static UpdateManager NoLocator() =>
        throw new InvalidOperationException("No VelopackLocator has been set.");

    [Fact]
    public void Constructing_The_Service_Does_Not_Build_The_Manager()
    {
        var built = 0;

        // Before: the manager was a field initializer, so this line threw
        // in any process without the Velopack bootstrap.
        var service = new UpdateService(() =>
        {
            built++;
            return NoLocator();
        });

        Assert.Equal(0, built);
        Assert.Equal("", service.Status);
    }

    [Fact]
    public void The_Public_Constructor_Needs_No_Velopack_Bootstrap()
    {
        // Whatever this process has or has not set up: no throw.
        var service = new UpdateService();

        Assert.Equal("", service.Status);
    }

    [Fact]
    public async Task A_Check_Without_A_Manager_Fails_Into_The_Status_Line_Not_Out_Of_The_Task()
    {
        var built = 0;
        var service = new UpdateService(() =>
        {
            built++;
            return NoLocator();
        });
        List<string> statuses = [];
        service.StatusChanged += statuses.Add;

        // "Never throws" includes building the manager.
        await service.CheckAndDownloadAsync();
        await service.CheckAndDownloadAsync();

        Assert.Equal(Loc.Format("UpdateSvc_CheckFailed", "No VelopackLocator has been set."), service.Status);
        Assert.DoesNotContain("UpdateSvc_", service.Status);
        Assert.Equal(2, statuses.Count);
        // Built once, however often it is asked for.
        Assert.Equal(1, built);
    }
}
