using Velopack;
using Velopack.Logging;

namespace Siakun.AutoUpdate.Tests;

public sealed class InstallPlanTests
{
    [Fact]
    public async Task LinksDeltasByBaseHashesInApplicationOrder()
    {
        using var http = new TestHttp();
        var middle = TestHttp.Release("1.1.0");
        var target = TestHttp.Release("1.2.0");
        var installed = TestHttp.Asset("1.0.0", "A");
        var middleFull = TestHttp.Asset("1.1.0", "B");
        http.Feed(target, TestHttp.Asset("1.2.0", "C"), middleFull, TestHttp.Asset("1.2.0", delta: true));
        http.Feed(middle, middleFull, installed, TestHttp.Asset("1.1.0", delta: true));
        var source = new GitHubReleaseSource(target, [target, middle], http.Client);
        var plan = await source.ResolvePlanAsync(installed);
        Assert.Equal(new[] { "1.1.0", "1.2.0" }, plan.Deltas.Select(d => d.Version.ToString()));
        Assert.Equal("1.2.0", plan.TargetFull.Version.ToString());
    }

    [Theory]
    [InlineData("wrong_hash")]
    [InlineData("large_delta")]
    [InlineData("missing_base")]
    public async Task FallsBackToFullWhenDeltaCannotBeUsed(string failure)
    {
        using var http = new TestHttp();
        var target = TestHttp.Release("1.1.0");
        var installed = TestHttp.Asset("1.0.0", "A");
        var expectedBase = TestHttp.Asset("1.0.0", failure == "wrong_hash" ? "B" : "A");
        var entries = new List<VelopackAsset>
        {
            TestHttp.Asset("1.1.0"),
            TestHttp.Asset("1.1.0", delta: true, size: failure == "large_delta" ? 2000 : 10)
        };
        if (failure != "missing_base") entries.Add(expectedBase);
        http.Feed(target, [.. entries]);
        var plan = await new GitHubReleaseSource(target, httpClient: http.Client).ResolvePlanAsync(installed);
        Assert.Empty(plan.Deltas);
    }

    [Fact]
    public async Task DowngradeDoesNotTraverseDeltaFeeds()
    {
        using var http = new TestHttp();
        var target = TestHttp.Release("1.0.0");
        http.Feed(target, TestHttp.Asset("1.0.0"), TestHttp.Asset("0.9.0"), TestHttp.Asset("1.0.0", delta: true));
        var plan = await new GitHubReleaseSource(target, httpClient: http.Client)
            .ResolvePlanAsync(TestHttp.Asset("2.0.0"));
        Assert.Empty(plan.Deltas);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task AmbiguousDeltaBaseFallsBackEvenWhenOneHashMatches()
    {
        using var http = new TestHttp();
        var target = TestHttp.Release("1.2.0");
        var installed = TestHttp.Asset("1.0.0");
        http.Feed(target, TestHttp.Asset("1.2.0"), installed,
            TestHttp.Asset("1.1.0"), TestHttp.Asset("1.2.0", delta: true));
        var plan = await new GitHubReleaseSource(target, httpClient: http.Client).ResolvePlanAsync(installed);
        Assert.Empty(plan.Deltas);
    }

    [Fact]
    public async Task RejectsFeedWhosePackageVersionDoesNotMatchSelectedTag()
    {
        using var http = new TestHttp();
        var target = TestHttp.Release("1.0.0");
        var wrong = TestHttp.Asset("2.0.0");
        wrong.FileName = target.PackageFileName;
        http.Feed(target, wrong);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GitHubReleaseSource(target, httpClient: http.Client).ResolvePlanAsync(null));
    }

    [Fact]
    public async Task CancellationIsNotConvertedIntoFullDownload()
    {
        using var http = new TestHttp();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GitHubReleaseSource(TestHttp.Release("1.0.0"), httpClient: http.Client)
                .ResolvePlanAsync(null, cancellation.Token));
    }

    [Fact]
    public async Task ReportsFullDownloadSizeOnceWhenVelopackAbandonsDelta()
    {
        using var http = new TestHttp();
        var target = TestHttp.Release("1.1.0");
        var installed = TestHttp.Asset("1.0.0");
        http.Feed(target, TestHttp.Asset("1.1.0"), installed, TestHttp.Asset("1.1.0", delta: true));
        http.Responses[target.PackageUrl] = "package bytes";
        var source = new GitHubReleaseSource(target, httpClient: http.Client);
        var plan = await source.ResolvePlanAsync(installed);
        Assert.Single(plan.Deltas);
        var sizes = new List<long>();
        source.FullFallbackStarted += sizes.Add;
        var file = Path.GetTempFileName();
        try
        {
            await source.DownloadReleaseEntry(new NullVelopackLogger(), plan.TargetFull, file, _ => { });
            await source.DownloadReleaseEntry(new NullVelopackLogger(), plan.TargetFull, file, _ => { });
            Assert.Equal(new[] { plan.TargetFull.Size }, sizes);
            Assert.Equal("package bytes", await File.ReadAllTextAsync(file));
        }
        finally { File.Delete(file); }
    }
}
