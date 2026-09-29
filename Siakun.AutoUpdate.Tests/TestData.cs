using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using NuGet.Versioning;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Siakun.AutoUpdate.Tests;

internal sealed class TestHttp : HttpMessageHandler
{
    internal const string Repository = "https://github.com/example/application";
    internal const string ReleasesUrl = "https://api.github.com/repos/example/application/releases?per_page=100&page=1";
    internal const string TagsUrl = "https://api.github.com/repos/example/application/tags?per_page=100&page=1";
    internal Dictionary<string, string> Responses { get; } = [];
    internal ConcurrentQueue<string> Requests { get; } = new();
    internal HttpClient Client { get; }
    internal TestHttp() => Client = new HttpClient(this);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Assert.NotEmpty(request.Headers.UserAgent);
        var url = request.RequestUri!.AbsoluteUri;
        Requests.Enqueue(url);
        var found = Responses.TryGetValue(url, out var body);
        return Task.FromResult(new HttpResponseMessage(found ? HttpStatusCode.OK : HttpStatusCode.NotFound)
        {
            Content = new StringContent(body ?? "not found")
        });
    }

    internal GitHubReleaseCatalog Catalog() => new(Repository, httpClient: Client);

    internal static VelopackAsset Asset(string version, string hash = "A", bool delta = false, long? size = null) => new()
    {
        PackageId = "Application",
        Version = SemanticVersion.Parse(version),
        Type = delta ? VelopackAssetType.Delta : VelopackAssetType.Full,
        FileName = $"Application-{version}-{(delta ? "delta" : "full")}.nupkg",
        SHA256 = string.Concat(Enumerable.Repeat(hash, 64)),
        Size = size ?? (delta ? 10 : 10_000)
    };

    internal static ReleaseVersion Release(string version, bool? prerelease = null) => new()
    {
        Tag = $"v{version}",
        Version = SemanticVersion.Parse(version),
        IsPrerelease = prerelease ?? SemanticVersion.Parse(version).IsPrerelease,
        PackageFileName = Asset(version).FileName,
        PackageUrl = $"{Repository}/releases/download/v{version}/{Asset(version).FileName}",
        FeedUrl = $"{Repository}/releases/download/v{version}/releases.win.json",
        PackageSize = 10_000,
        DeltaFileName = Asset(version, delta: true).FileName,
        DeltaUrl = $"{Repository}/releases/download/v{version}/{Asset(version, delta: true).FileName}",
        DeltaSize = 10
    };

    internal static object ReleaseJson(ReleaseVersion release, bool draft = false, bool complete = true) => new
    {
        tag_name = release.Tag,
        prerelease = release.IsPrerelease,
        draft,
        assets = complete ? new[]
        {
            new { name = release.PackageFileName, browser_download_url = release.PackageUrl, size = release.PackageSize },
            new { name = "releases.win.json", browser_download_url = release.FeedUrl, size = 100L },
            new { name = release.DeltaFileName!, browser_download_url = release.DeltaUrl!, size = release.DeltaSize }
        } : []
    };

    internal void Releases(params ReleaseVersion[] releases) =>
        Responses[ReleasesUrl] = JsonSerializer.Serialize(releases.Select(r => ReleaseJson(r)));

    internal void Feed(ReleaseVersion release, params VelopackAsset[] assets) =>
        Responses[release.FeedUrl] = JsonSerializer.Serialize(new
        {
            Assets = assets.Select(a => new
            {
                a.PackageId,
                Version = a.Version.ToString(),
                Type = a.Type.ToString(),
                a.FileName,
                a.SHA256,
                a.SHA1,
                a.Size
            })
        });
}

internal sealed class TestSettings : IUpdateSettings
{
    public bool AutoUpdateEnabled { get; set; } = true;
    public bool PrereleaseEnabled { get; set; }
}

// INTENT: 테스트에서 다운로드와 적용의 순서를 제어하되 실제 프로세스나 설치 파일은 실행하지 않는다.
internal sealed class TestManager(IUpdateSource source, UpdateOptions options)
    : PackageUpdateManager(source, options, new TestVelopackLocator("Application", "1.0.0", Path.GetTempPath()))
{
    internal SemanticVersion InstalledVersion { get; set; } = SemanticVersion.Parse("1.0.0");
    internal bool Installed { get; set; } = true;
    internal bool Portable { get; set; }
    internal VelopackAsset? Downloaded { get; set; }
    internal VelopackAsset? BasePackage { get; set; }
    internal UpdateInfo? NextUpdate { get; set; }
    internal Func<UpdateInfo, CancellationToken, Task>? Download { get; set; }
    internal List<UpdateInfo> Downloads { get; } = [];
    internal List<(VelopackAsset Asset, bool Restart)> Applies { get; } = [];
    public override bool IsInstalled => Installed;
    public override bool IsPortable => Portable;
    public override SemanticVersion CurrentVersion => InstalledVersion;
    public override VelopackAsset? UpdatePendingRestart => Downloaded;
    public override Task<UpdateInfo?> CheckForUpdatesAsync() => Task.FromResult(NextUpdate);
    internal override VelopackAsset? GetBasePackage() => BasePackage;
    internal override void ScheduleApply(VelopackAsset asset, bool restart) => Applies.Add((asset, restart));
    public override async Task DownloadUpdatesAsync(UpdateInfo updates, Action<int>? progress = null, CancellationToken cancelToken = default)
    {
        Downloads.Add(updates);
        if (Download != null) await Download(updates, cancelToken);
        progress?.Invoke(100);
    }
}
