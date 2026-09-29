using System.Text.Json;
using System.Text.Json.Serialization;
using NuGet.Versioning;
using Velopack;

namespace Siakun.AutoUpdate;

/// <summary>공개 GitHub 저장소의 설치 가능한 릴리스를 조회합니다.</summary>
// INTENT: 앱 설정이나 설치 상태 없이 목록을 조회하도록 분리해, UI와 다운로드가 같은 릴리스 정보를 쓴다.
public sealed class GitHubReleaseCatalog
{
    private readonly HttpClient _http;
    private readonly Action<string>? _log;
    private const int TagFeedConcurrency = 6;
    public string RepositoryUrl { get; }
    public string Channel { get; }
    private string FeedFileName => $"releases.{Channel}.json";

    /// <summary>httpClient를 전달하면 수명 관리는 호출자가 맡습니다. 인증 없는 공개 저장소를 대상으로 합니다.</summary>
    public GitHubReleaseCatalog(string repositoryUrl, string channel = "win", HttpClient? httpClient = null, Action<string>? log = null)
    {
        var (owner, repo) = ParseRepoUrl(repositoryUrl);
        if (string.IsNullOrWhiteSpace(channel) || channel.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException("유효한 Velopack 채널 이름이 필요합니다.", nameof(channel));
        RepositoryUrl = $"https://github.com/{owner}/{repo}";
        Channel = channel;
        _http = httpClient ?? GitHubHttp.Shared;
        _log = log;
    }

    internal GitHubReleaseSource CreateSource(ReleaseVersion target, IReadOnlyList<ReleaseVersion> versions) =>
        new(target, versions, _http, _log);

    private async Task<List<T>> GetPagesAsync<T>(string url, CancellationToken cancelToken)
    {
        var items = new List<T>();
        for (var page = 1; ; page++)
        {
            var json = await GitHubHttp.GetStringAsync(_http, $"{url}&page={page}", cancelToken).ConfigureAwait(false);
            var batch = JsonSerializer.Deserialize<T[]>(json) ?? [];
            items.AddRange(batch);
            if (batch.Length < 100) return items;
        }
    }

    private static (string Owner, string Repo) ParseRepoUrl(string repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("https://github.com/owner/repository 형식의 주소가 필요합니다.", nameof(repositoryUrl));
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 2 || segments.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("GitHub 저장소 주소가 필요합니다.", nameof(repositoryUrl));
        return (segments[0], segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1]);
    }

    /// <summary>
    /// 저장소의 릴리스를 조회해 설치할 수 있는 버전 목록을 최신 순으로 돌려준다.
    /// 네트워크 실패는 호출자가 처리한다
    ///
    /// 목록이 비어 돌아오면 태그와 피드로 다시 만든다.
    /// 조회가 예외로 끝나는 경우(한도 초과, 연결 실패)는 폴백하지 않는다. 태그 조회도 같은
    /// API라 같은 이유로 실패하고, 그 사정은 호출 측이 이미 구분해 알린다
    /// </summary>
    public async Task<IReadOnlyList<ReleaseVersion>> GetVersionsAsync(CancellationToken cancelToken = default)
    {
        var (owner, repo) = ParseRepoUrl(RepositoryUrl);

        var versions = await FetchFromReleaseListAsync(owner, repo, cancelToken);
        if (versions.Count > 0)
        {
            _log?.Invoke($"[GitHubReleaseCatalog] Fetched {versions.Count} installable version(s)");
            return versions;
        }

        _log?.Invoke("[GitHubReleaseCatalog] Release list came back empty, rebuilding from tags");

        versions = await FetchFromTagsAsync(owner, repo, cancelToken);
        _log?.Invoke($"[GitHubReleaseCatalog] Fetched {versions.Count} installable version(s) from tags");
        return versions;
    }

    /// <summary>
    /// 릴리스 목록 엔드포인트로 버전 목록을 만든다 (기본 경로, 페이지 단위 조회)
    /// </summary>
    private async Task<List<ReleaseVersion>> FetchFromReleaseListAsync(
        string owner, string repo, CancellationToken cancelToken)
    {
        var apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=100";

        var releases = await GetPagesAsync<GitHubRelease>(apiUrl, cancelToken);

        var versions = new List<ReleaseVersion>();
        foreach (var release in releases)
        {
            if (release.Draft) continue;

            var packages = release.Assets.Where(
                asset => asset.Name.EndsWith("-full.nupkg", StringComparison.OrdinalIgnoreCase)).ToArray();
            var feed = release.Assets.FirstOrDefault(
                asset => string.Equals(asset.Name, FeedFileName, StringComparison.OrdinalIgnoreCase));

            // Velopack 산출물이 갖춰지지 않은 릴리스는 설치 대상이 될 수 없다
            if (packages.Length == 0 || feed == null) continue;
            // 앱과 아키텍처를 고르는 정보가 없는 상태에서 첫 파일을 고르면 다른 배포물을 설치할 수 있다.
            if (packages.Length > 1)
                throw new NotSupportedException($"릴리스 {release.TagName}에 full 패키지가 여러 개 있습니다. 앱과 아키텍처별 저장소가 필요합니다.");
            var package = packages[0];

            if (!SemanticVersion.TryParse(release.TagName.TrimStart('v', 'V'), out var version)) continue;

            // delta는 없을 수 있다. 첫 릴리스이거나 delta 생성 단계가 실패한 경우다
            var delta = release.Assets.FirstOrDefault(
                asset => asset.Name.EndsWith("-delta.nupkg", StringComparison.OrdinalIgnoreCase));

            versions.Add(new ReleaseVersion
            {
                Tag = release.TagName,
                Version = version,
                IsPrerelease = release.Prerelease,
                PackageFileName = package.Name,
                PackageUrl = package.DownloadUrl,
                FeedUrl = feed.DownloadUrl,
                PackageSize = package.Size,
                DeltaFileName = delta?.Name,
                DeltaUrl = delta?.DownloadUrl,
                DeltaSize = delta?.Size ?? 0,
            });
        }

        versions.Sort((left, right) => right.Version.CompareTo(left.Version));
        return versions;
    }

    /// <summary>
    /// 태그와 릴리스 자산으로 버전 목록을 다시 만든다 (목록 엔드포인트 우회).
    ///
    /// 자산 주소는 태그와 파일명으로 정해지므로(releases/download 아래) 자산 목록을 API에
    /// 묻지 않아도 된다. 파일명과 크기는 그 릴리스의 피드에 적혀 있어 피드 하나만 받으면
    /// 채울 수 있고, 그 다운로드는 API가 아니라 시간당 한도와도 무관하다.
    /// </summary>
    private async Task<List<ReleaseVersion>> FetchFromTagsAsync(
        string owner, string repo, CancellationToken cancelToken)
    {
        var apiUrl = $"https://api.github.com/repos/{owner}/{repo}/tags?per_page=100";

        var tags = await GetPagesAsync<GitHubTag>(apiUrl, cancelToken);

        using var limiter = new SemaphoreSlim(TagFeedConcurrency);
        var lookups = new List<Task<ReleaseVersion?>>();

        foreach (var tag in tags)
        {
            if (!SemanticVersion.TryParse(tag.Name.TrimStart('v', 'V'), out var version)) continue;

            lookups.Add(LoadVersionFromTagAsync(owner, repo, tag.Name, version, limiter, cancelToken));
        }

        var versions = (await Task.WhenAll(lookups)).OfType<ReleaseVersion>().ToList();
        versions.Sort((left, right) => right.Version.CompareTo(left.Version));
        return versions;
    }

    /// <summary>
    /// 태그 하나의 피드를 받아 릴리스를 복원한다.
    ///
    /// 릴리스가 없는 태그나 Velopack 산출물이 없는 태그는 null이다. 그런 태그가 섞여 있다고
    /// 목록 전체를 버릴 이유가 없으므로 건너뛴 사정만 로그에 남긴다.
    ///
    /// 프리릴리스 여부는 GitHub 릴리스의 표시가 아니라 태그의 시맨틱 버전으로 판정한다.
    /// 이 경로에서는 그 표시를 알 수 없고, 태그에 포함된 프리릴리스 표기를 사용한다
    /// </summary>
    private async Task<ReleaseVersion?> LoadVersionFromTagAsync(
        string owner,
        string repo,
        string tag,
        SemanticVersion version,
        SemaphoreSlim limiter,
        CancellationToken cancelToken)
    {
        await limiter.WaitAsync(cancelToken);

        try
        {
            var feedUrl = AssetUrl(owner, repo, tag, FeedFileName);
            var json = await GitHubHttp.GetStringAsync(_http, feedUrl, cancelToken);
            var feed = VelopackAssetFeed.FromJson(json);

            // 피드에는 delta의 기준이 된 다른 버전의 full도 적혀 있어 버전까지 대조한다
            var packages = feed.Assets.Where(
                asset => asset.Type == VelopackAssetType.Full
                         && asset.Version.CompareTo(version) == 0).ToArray();

            if (packages.Length != 1) return null;
            var package = packages[0];

            var delta = feed.Assets.FirstOrDefault(
                asset => asset.Type == VelopackAssetType.Delta
                         && asset.Version.CompareTo(version) == 0);

            return new ReleaseVersion
            {
                Tag = tag,
                Version = version,
                IsPrerelease = version.IsPrerelease,
                PackageFileName = package.FileName,
                PackageUrl = AssetUrl(owner, repo, tag, package.FileName),
                FeedUrl = feedUrl,
                PackageSize = package.Size,
                DeltaFileName = delta?.FileName,
                DeltaUrl = delta == null ? null : AssetUrl(owner, repo, tag, delta.FileName),
                DeltaSize = delta?.Size ?? 0,
            };
        }
        catch (OperationCanceledException) when (cancelToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[GitHubReleaseCatalog] {tag} has no installable release ({ex.Message})");
            return null;
        }
        finally
        {
            limiter.Release();
        }
    }

    /// <summary>
    /// 릴리스 자산의 다운로드 주소. GitHub이 정한 고정 형식이라 API를 거치지 않고 만든다
    /// </summary>
    private static string AssetUrl(string owner, string repo, string tag, string fileName) =>
        $"https://github.com/{owner}/{repo}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(fileName)}";

    /// <summary> GitHub Releases API 응답에서 필요한 항목만 받는 모델 </summary>
    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("assets")] public GitHubAsset[] Assets { get; set; } = [];
    }

    private sealed class GitHubTag
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}
