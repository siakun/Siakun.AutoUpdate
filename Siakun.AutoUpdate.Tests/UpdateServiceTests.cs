using NuGet.Versioning;
using Velopack;
using Velopack.Sources;

namespace Siakun.AutoUpdate.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task VersionListFollowsPrereleasePreference()
    {
        using var rig = new Rig();
        rig.Http.Releases(TestHttp.Release("1.0.0"), TestHttp.Release("2.0.0-beta"));
        Assert.Single(await rig.Service.GetAvailableVersionsAsync());
        rig.Settings.PrereleaseEnabled = true;
        Assert.Equal(2, (await rig.Service.GetAvailableVersionsAsync()).Count);
    }

    [Fact]
    public async Task DevelopmentBuildDoesNotCheckForUpdates()
    {
        using var rig = new Rig();
        rig.Automatic.Installed = false;
        await rig.Service.CheckAndDownloadAsync();
        Assert.False(rig.Service.IsManagedInstall);
        Assert.Null(rig.Service.CurrentVersion);
        Assert.Empty(rig.Automatic.Downloads);
        Assert.Empty(rig.Http.Requests);
    }

    [Fact]
    public async Task PortableInstallReceivesAutomaticUpdatesAndVersionSwitches()
    {
        using var rig = new Rig();
        rig.Automatic.Portable = true;
        rig.Manual.Portable = true;
        rig.Automatic.NextUpdate = new UpdateInfo(TestHttp.Asset("2.0.0"), false);
        await rig.Service.CheckAndDownloadAsync();
        Assert.True(rig.Service.IsManagedInstall);
        Assert.Equal("1.0.0", rig.Service.CurrentVersion?.ToString());
        Assert.Single(rig.Automatic.Downloads);

        var target = TestHttp.Release("1.1.0");
        rig.Http.Releases(target);
        rig.Http.Feed(target, TestHttp.Asset("1.1.0"));
        await rig.Service.InstallVersionAsync(target);
        Assert.Empty(Assert.Single(rig.Manual.Downloads).DeltasToTarget);
    }

    [Fact]
    public async Task RestoresPendingUpdateEvenWhenAutomaticUpdatesAreDisabled()
    {
        using var rig = new Rig();
        rig.Settings.AutoUpdateEnabled = false;
        rig.Automatic.Downloaded = TestHttp.Asset("2.0.0");
        SemanticVersion? ready = null;
        rig.Service.UpdateReady += (_, e) => ready = e.Version;
        await rig.Service.CheckAndDownloadAsync();
        Assert.Equal("2.0.0", ready?.ToString());
        Assert.Empty(rig.Automatic.Downloads);
        Assert.True(rig.Service.ApplyOnExit());
        Assert.False(rig.Service.ApplyOnExit());
        Assert.False(Assert.Single(rig.Automatic.Applies).Restart);
    }

    [Fact]
    public async Task DowngradePausesAutomaticUpdatesBeforeRequestingRestart()
    {
        using var rig = new Rig();
        var target = TestHttp.Release("1.0.0");
        rig.Manual.InstalledVersion = SemanticVersion.Parse("2.0.0");
        rig.Http.Releases(TestHttp.Release("2.0.0"), target);
        rig.Http.Feed(target, TestHttp.Asset("1.0.0"));
        var restarted = false;
        rig.Service.RestartRequested += (_, _) =>
        {
            Assert.False(rig.Settings.AutoUpdateEnabled);
            Assert.True(rig.Service.UpdateDownloaded);
            restarted = true;
        };
        await rig.Service.InstallVersionAsync(target);
        Assert.True(restarted);
        Assert.Empty(Assert.Single(rig.Manual.Downloads).DeltasToTarget);
        Assert.True(rig.Service.ApplyOnExit());
        var apply = Assert.Single(rig.Manual.Applies);
        Assert.True(apply.Restart);
        Assert.Equal(target.Version, apply.Asset.Version);
    }

    [Fact]
    public async Task AutomaticAndManualDownloadsAreSerializedAndManualChoiceWins()
    {
        using var rig = new Rig();
        var target = TestHttp.Release("1.1.0");
        rig.Http.Releases(TestHttp.Release("2.0.0"), target);
        rig.Http.Feed(target, TestHttp.Asset("1.1.0"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Automatic.NextUpdate = new UpdateInfo(TestHttp.Asset("2.0.0"), false);
        rig.Automatic.Download = async (_, token) =>
        {
            started.SetResult();
            await finish.Task.WaitAsync(token);
        };
        var automaticNotifications = 0;
        rig.Service.UpdateReady += (_, _) => automaticNotifications++;
        var automaticTask = rig.Service.CheckAndDownloadAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var manualTask = rig.Service.InstallVersionAsync(target);
        Assert.Empty(rig.Manual.Downloads);
        finish.SetResult();
        await Task.WhenAll(automaticTask, manualTask).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, automaticNotifications);
        Assert.True(rig.Service.ApplyOnExit());
        Assert.Empty(rig.Automatic.Applies);
        Assert.Equal(target.Version, Assert.Single(rig.Manual.Applies).Asset.Version);
    }

    [Fact]
    public async Task ExitingDuringManualDownloadDoesNotApplyOrRestartLater()
    {
        using var rig = new Rig();
        var target = TestHttp.Release("1.1.0");
        rig.Http.Releases(target);
        rig.Http.Feed(target, TestHttp.Asset("1.1.0"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Manual.Download = async (_, token) =>
        {
            started.SetResult();
            await finish.Task.WaitAsync(token);
        };
        var restart = false;
        rig.Service.RestartRequested += (_, _) => restart = true;
        var task = rig.Service.InstallVersionAsync(target);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(rig.Service.ApplyOnExit());
        finish.SetResult();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(restart);
        Assert.False(rig.Service.UpdateDownloaded);
        Assert.Empty(rig.Manual.Applies);
    }

    [Fact]
    public async Task FailedManualSelectionDoesNotRestoreAnEarlierAutomaticUpdate()
    {
        using var rig = new Rig();
        rig.Automatic.Downloaded = TestHttp.Asset("2.0.0");
        await rig.Service.CheckAndDownloadAsync();
        Assert.True(rig.Service.UpdateDownloaded);
        var target = TestHttp.Release("1.1.0");
        rig.Http.Releases(target);
        rig.Http.Feed(target, TestHttp.Asset("1.1.0"));
        rig.Manual.Download = (_, _) => throw new IOException("download interrupted");
        await Assert.ThrowsAsync<IOException>(() => rig.Service.InstallVersionAsync(target));
        Assert.False(rig.Service.UpdateDownloaded);
        Assert.False(rig.Service.ApplyOnExit());
        Assert.Empty(rig.Automatic.Applies);
    }

    [Fact]
    public async Task RejectsPackageForAnotherApplicationBeforeDownloading()
    {
        using var rig = new Rig();
        var target = TestHttp.Release("1.1.0");
        var asset = TestHttp.Asset("1.1.0");
        asset.PackageId = "AnotherApplication";
        rig.Http.Releases(target);
        rig.Http.Feed(target, asset);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.InstallVersionAsync(target));
        Assert.Empty(rig.Manual.Downloads);
        Assert.True(rig.Settings.AutoUpdateEnabled);
    }

    [Fact]
    public async Task CancelledManualDownloadReleasesOperationForRetry()
    {
        using var rig = new Rig();
        var target = TestHttp.Release("1.1.0");
        rig.Http.Releases(target);
        rig.Http.Feed(target, TestHttp.Asset("1.1.0"));
        using var cancellation = new CancellationTokenSource();
        rig.Manual.Download = (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(cancellation.Token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            rig.Service.InstallVersionAsync(target, cancelToken: cancellation.Token));
        Assert.False(rig.Service.UpdateDownloaded);
        rig.Manual.Download = null;
        await rig.Service.InstallVersionAsync(target);
        Assert.True(rig.Service.UpdateDownloaded);
    }

    [Fact]
    public async Task CancellingQueuedManualSelectionDoesNotReviveEarlierAutomaticDownload()
    {
        using var rig = new Rig();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Automatic.NextUpdate = new UpdateInfo(TestHttp.Asset("2.0.0"), false);
        rig.Automatic.Download = async (_, token) =>
        {
            started.SetResult();
            await finish.Task.WaitAsync(token);
        };
        var automatic = rig.Service.CheckAndDownloadAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var manual = rig.Service.InstallVersionAsync(TestHttp.Release("1.1.0"), cancelToken: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manual);
        finish.SetResult();
        await automatic.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(rig.Service.UpdateDownloaded);
        Assert.False(rig.Service.ApplyOnExit());
        Assert.Empty(rig.Automatic.Applies);
    }

    [Fact]
    public async Task AutomaticUpdateDoesNotDownloadAnotherApplication()
    {
        using var rig = new Rig();
        var asset = TestHttp.Asset("2.0.0");
        asset.PackageId = "AnotherApplication";
        rig.Automatic.NextUpdate = new UpdateInfo(asset, false);
        await rig.Service.CheckAndDownloadAsync();
        Assert.Empty(rig.Automatic.Downloads);
        Assert.False(rig.Service.UpdateDownloaded);
    }

    private sealed class Rig : IDisposable
    {
        internal TestHttp Http { get; } = new();
        internal TestSettings Settings { get; } = new();
        internal TestManager Automatic { get; } = new(new GithubSource(TestHttp.Repository, null, false), new());
        internal TestManager Manual { get; } = new(new GitHubReleaseSource(TestHttp.Release("1.0.0")), new());
        internal UpdateService Service { get; }
        internal Rig() => Service = new UpdateService(Http.Catalog(), Settings, null,
            (source, _) => source is GithubSource ? Automatic : Manual);
        public void Dispose() => Http.Dispose();
    }
}
