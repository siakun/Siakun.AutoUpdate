using NuGet.Versioning;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Siakun.AutoUpdate;

/// <summary>선택한 GitHub 릴리스와 검증한 delta 경로를 Velopack 업데이트 소스로 제공합니다.</summary>
// INTENT: 릴리스마다 패키지를 따로 보관하는 배포에서 오래된 버전도 선택할 수 있게 한다.
// 설치와 체크섬 검증은 Velopack에 맡기고, 이 소스는 원본 피드와 실제 자산의 주소를 연결한다.
// 기준 패키지의 해시가 일치할 때만 delta를 연결하며, 경로를 확인할 수 없으면 full을 사용한다.
public sealed class GitHubReleaseSource : IUpdateSource
{
    private readonly HttpClient _http;
    private readonly Action<string>? _log;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(2);
    private const int MaximumDeltaChainLength = 10;
    // delta 조립 비용을 감수할 만큼 전송량이 줄어드는 경로만 선택한다.
    private const int DeltaToFullSizeRatio = 10;
    private readonly ReleaseVersion _target;
    private readonly IReadOnlyList<ReleaseVersion> _allVersions;
    private readonly Dictionary<string, string> _downloadUrls = new(StringComparer.OrdinalIgnoreCase);
    private VelopackAssetFeed? _cachedFeed;
    private bool _deltaPlanned;

    public event Action<long>? FullFallbackStarted;

    public GitHubReleaseSource(ReleaseVersion target, IReadOnlyList<ReleaseVersion>? allVersions = null,
        HttpClient? httpClient = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
        _allVersions = allVersions ?? [];
        _http = httpClient ?? GitHubHttp.Shared;
        _log = log;
        _downloadUrls[target.PackageFileName] = target.PackageUrl;
    }

    /// <summary>
    /// 이번 설치에서 실제로 받을 것을 정한다.
    ///
    /// 대상의 full은 반드시 있어야 하므로 그 조회가 실패하면 예외가 나간다. 이전에도 그랬다.
    /// 반면 delta는 없어도 설치할 수 있으므로, 사슬을 찾다 무엇이 잘못되면 예외를 삼키고
    /// 빈 목록을 돌려준다. delta 때문에 되던 설치가 안 되는 일이 없어야 한다
    /// </summary>
    public async Task<InstallPlan> ResolvePlanAsync(
        VelopackAsset? localBase,
        CancellationToken cancelToken = default)
    {
        var targetFeed = await LoadReleaseFeedAsync(_target, cancelToken);
        var targetFull = RequireAsset(targetFeed, _target, _target.PackageFileName, VelopackAssetType.Full);

        if (localBase == null || localBase.Version.CompareTo(targetFull.Version) >= 0
            || !string.Equals(localBase.PackageId, targetFull.PackageId, StringComparison.Ordinal))
        {
            return Finish(targetFull, []);
        }

        try
        {
            var deltas = await WalkChainAsync(targetFeed, targetFull, localBase, cancelToken);
            return Finish(targetFull, deltas);
        }
        catch (OperationCanceledException) when (cancelToken.IsCancellationRequested)
        {
            // 사용자가 멈춘 것을 조회 실패로 바꿔 읽으면 설치가 그대로 이어진다.
            // 토큰을 함께 봐야 한다. HttpClient는 자기 Timeout이 지나도 같은 예외를 던지는데,
            // 그것은 조회 실패이므로 full로 넘어가야 한다
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[GitHubReleaseSource] Delta plan failed ({ex.Message}), using full package");
            return Finish(targetFull, []);
        }
    }

    /// <summary>
    /// 대상에서 뒤로 따라가며 로컬 패키지에 닿는 delta 사슬을 만든다.
    ///
    /// 버전 번호로 사이 릴리스를 훑지 않는다. 릴리스마다 자기 delta의 기준이 피드에 적혀
    /// 있으므로 그것을 따라가야 실제로 붙는 경로가 나온다. 번호로 훑으면 두 가지로 어긋난다.
    /// 사이 릴리스가 지워졌을 때, 그리고 프리릴리스처럼 직전이 아닌 버전을 기준으로 delta가
    /// 만들어졌을 때다. 뒤에서 따라가면 1.0.0에서 1.1.0-beta를 건너뛰고 1.1.0으로 바로
    /// 가는 경로처럼, 번호만 보면 놓치는 지름길도 그대로 잡힌다.
    ///
    /// 기준 대조는 버전과 해시를 함께 본다. 버전만 보면 같은 번호로 만들어진 다른 패키지에
    /// 붙을 수 있는데, Velopack은 delta로 조립한 결과를 대상 해시로 다시 검증하지 않아
    /// 그 오류가 조용히 남는다
    /// </summary>
    private async Task<VelopackAsset[]> WalkChainAsync(
        VelopackAssetFeed targetFeed,
        VelopackAsset targetFull,
        VelopackAsset localBase,
        CancellationToken cancelToken)
    {
        var deltas = new List<VelopackAsset>();
        var current = _target;
        var currentFeed = targetFeed;
        long chainBytes = 0;

        for (var step = 0; step < MaximumDeltaChainLength; step++)
        {
            var delta = currentFeed.Assets.FirstOrDefault(
                asset => asset.Type == VelopackAssetType.Delta
                         && asset.PackageId == targetFull.PackageId
                         && string.Equals(asset.FileName, current.DeltaFileName, StringComparison.OrdinalIgnoreCase)
                         && asset.Version.CompareTo(current.Version) == 0);

            if (delta == null)
            {
                throw new InvalidOperationException($"릴리스 {current.Tag}에 delta가 없습니다.");
            }

            chainBytes += delta.Size;
            if (chainBytes > targetFull.Size / DeltaToFullSizeRatio)
            {
                throw new InvalidOperationException(
                    $"delta 합계 {chainBytes / 1024}KB가 full의 {DeltaToFullSizeRatio}분의 1을 넘습니다.");
            }

            var bases = currentFeed.Assets.Where(
                asset => asset.Type == VelopackAssetType.Full
                         && asset.PackageId == targetFull.PackageId
                         && asset.Version.CompareTo(current.Version) != 0).ToArray();

            // 기준 관계가 명시되지 않은 피드에서 여러 full 중 하나를 추측해 고르지 않는다.
            // 버전과 해시가 맞아도 그 패키지가 이 delta를 만들 때 사용된 기준인지는 알 수 없다.
            if (bases.Length != 1 || bases[0].Version.CompareTo(current.Version) >= 0)
            {
                throw new InvalidOperationException($"릴리스 {current.Tag}의 피드에서 delta 기준을 확정할 수 없습니다.");
            }
            var baseAsset = bases[0];

            if (string.IsNullOrEmpty(current.DeltaUrl))
            {
                throw new InvalidOperationException($"릴리스 {current.Tag}의 delta 주소를 찾지 못했습니다.");
            }

            deltas.Insert(0, delta);
            _downloadUrls[delta.FileName] = current.DeltaUrl;

            if (SameAsset(baseAsset, localBase))
            {
                _log?.Invoke(
                    $"[GitHubReleaseSource] Delta chain of {deltas.Count} ({chainBytes / 1024}KB vs full {targetFull.Size / 1024}KB)");
                return [.. deltas];
            }

            // 상한에 닿았으면 다음 피드를 받지 않는다. 어차피 쓰지 못할 조회다
            if (step + 1 >= MaximumDeltaChainLength)
            {
                throw new InvalidOperationException($"사슬이 {MaximumDeltaChainLength}단계를 넘었습니다.");
            }

            (current, currentFeed) = await FindBaseReleaseAsync(baseAsset, cancelToken);
        }

        throw new InvalidOperationException($"사슬이 {MaximumDeltaChainLength}단계를 넘었습니다.");
    }

    /// <summary>
    /// delta의 기준이 되는 릴리스를 찾는다. 버전이 같은 후보가 여럿일 수 있으므로
    /// 각 후보의 full이 그 기준과 같은 내용인지 확인하고 맞는 것만 고른다.
    ///
    /// 버전만 보고 고르면 사슬 중간에서 다른 패키지로 갈아타게 된다. Velopack이 조립 결과를
    /// 다시 검증하지 않으므로 그 어긋남은 드러나지 않고 설치까지 간다.
    ///
    /// 내용까지 같은 후보가 여럿이면 먼저 찾은 것을 쓰고 되돌아가지 않는다. 그 갈래가 뒤에서
    /// 끊겨도 다른 갈래를 다시 뒤지지 않는다는 뜻이다. 되돌아가려면 갈래마다 delta 목록과
    /// 누적 크기, 주소 표를 따로 들고 다녀야 해서 값이 큰데, 얻는 것은 full 대신 delta를
    /// 쓸 기회 하나뿐이다. 잘못 이어 붙일 위험은 없다. 그리고 이 상황은 서로 다른 릴리스가
    /// 바이트까지 같은 full을 담아야 생긴다
    /// </summary>
    private async Task<(ReleaseVersion Release, VelopackAssetFeed Feed)> FindBaseReleaseAsync(
        VelopackAsset baseAsset,
        CancellationToken cancelToken)
    {
        foreach (var candidate in _allVersions.Where(v => v.Version.CompareTo(baseAsset.Version) == 0))
        {
            VelopackAssetFeed feed;
            try
            {
                feed = await LoadReleaseFeedAsync(candidate, cancelToken);
            }
            catch (OperationCanceledException) when (cancelToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 후보 하나를 읽지 못한 것으로 사슬을 포기하지 않는다
                _log?.Invoke($"[GitHubReleaseSource] {candidate.Tag} feed unreadable ({ex.Message}), trying next");
                continue;
            }

            var full = feed.Assets.FirstOrDefault(
                asset => asset.Type == VelopackAssetType.Full
                         && string.Equals(asset.FileName, candidate.PackageFileName, StringComparison.OrdinalIgnoreCase));

            if (full != null && SameAsset(full, baseAsset))
            {
                return (candidate, feed);
            }

            _log?.Invoke($"[GitHubReleaseSource] {candidate.Tag} is v{baseAsset.Version} but its content differs");
        }

        throw new InvalidOperationException($"delta 기준인 {baseAsset.Version}과 같은 내용의 릴리스를 찾지 못했습니다.");
    }

    /// <summary>
    /// 두 자산이 같은 패키지인지 본다. 버전이 같아도 내용이 다를 수 있으므로 해시까지 본다.
    /// SHA256이 없는 옛 피드를 위해 SHA1도 받아들인다
    /// </summary>
    private static bool SameAsset(VelopackAsset left, VelopackAsset right)
    {
        if (left.Version.CompareTo(right.Version) != 0 || left.PackageId != right.PackageId || left.Size != right.Size) return false;

        if (!string.IsNullOrEmpty(left.SHA256) && !string.IsNullOrEmpty(right.SHA256))
        {
            return string.Equals(left.SHA256, right.SHA256, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrEmpty(left.SHA1) && !string.IsNullOrEmpty(right.SHA1))
        {
            return string.Equals(left.SHA1, right.SHA1, StringComparison.OrdinalIgnoreCase);
        }

        // 해시를 견줄 수 없으면 같다고 보지 않는다. 틀린 기준에 붙이는 것보다 full이 낫다
        return false;
    }

    private InstallPlan Finish(VelopackAsset targetFull, VelopackAsset[] deltas)
    {
        _cachedFeed = new VelopackAssetFeed { Assets = [targetFull, .. deltas] };
        _deltaPlanned = deltas.Length > 0;
        return new InstallPlan(targetFull, deltas);
    }

    private async Task<VelopackAssetFeed> LoadReleaseFeedAsync(
        ReleaseVersion release, CancellationToken cancelToken)
    {
        var json = await GitHubHttp.GetStringAsync(_http, release.FeedUrl, cancelToken);
        return VelopackAssetFeed.FromJson(json);
    }

    /// <summary>
    /// 피드에서 자산 하나를 찾는다.
    ///
    /// 피드에는 delta를 만들 때 기준으로 삼은 버전의 full도 적혀 있는데, 그 파일은 이
    /// 릴리스에 올라가 있지 않아 주소를 보장할 수 없다. 그래서 파일명까지 대조해 이 릴리스가
    /// 실제로 갖고 있는 자산만 남긴다
    /// </summary>
    private static VelopackAsset RequireAsset(
        VelopackAssetFeed feed, ReleaseVersion release, string fileName, VelopackAssetType type)
    {
        var asset = feed.Assets.FirstOrDefault(
            candidate => candidate.Type == type
                         && candidate.Version.CompareTo(release.Version) == 0
                         && string.Equals(candidate.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        if (asset == null)
        {
            throw new InvalidOperationException(
                $"릴리스 {release.Tag}의 피드에서 {fileName} 정보를 찾지 못했습니다.");
        }

        return asset;
    }

    public Task<VelopackAssetFeed> GetReleaseFeed(
        IVelopackLogger logger,
        string? appId,
        string channel,
        Guid? stagingId = null,
        VelopackAsset? latestLocalRelease = null)
    {
        // 이 소스는 릴리스 하나에 고정돼 있어 채널과 스테이징 인자가 대상을 바꾸지 않는다.
        // 이 시그니처에는 취소 토큰이 없으므로 공통 HTTP 제한 시간으로 피드 조회를 끝낸다
        return LoadFeedAsync(CancellationToken.None);
    }

    public async Task DownloadReleaseEntry(
        IVelopackLogger logger,
        VelopackAsset releaseEntry,
        string localFile,
        Action<int> progress,
        CancellationToken cancelToken = default)
    {
        var url = ResolveDownloadUrl(releaseEntry.FileName);
        if (url == null)
        {
            throw new InvalidOperationException(
                $"이 소스가 제공하지 않는 패키지를 요청했습니다: {releaseEntry.FileName}");
        }

        // delta로 받기로 해 놓고 full을 요청받았다면 Velopack이 되돌린 것이다.
        // 화면에 남은 delta 크기를 바로잡지 않으면 진행률이 뒤로 가는 것처럼 보인다
        if (_deltaPlanned
            && string.Equals(releaseEntry.FileName, _target.PackageFileName, StringComparison.OrdinalIgnoreCase))
        {
            _log?.Invoke("[GitHubReleaseSource] Velopack fell back to the full package");
            _deltaPlanned = false;
            FullFallbackStarted?.Invoke(releaseEntry.Size);
        }

        _log?.Invoke($"[GitHubReleaseSource] Downloading {releaseEntry.FileName}");
        await DownloadFileAsync(url, localFile, progress, cancelToken);
    }

    /// <summary>
    /// 파일명으로 다운로드 주소를 찾는다. 이 소스가 제공하는 것은 대상의 full 하나와
    /// 사슬의 delta들뿐이며, 그 밖의 요청은 주소를 보장할 수 없어 null을 돌려준다
    /// </summary>
    private string? ResolveDownloadUrl(string fileName)
        => _downloadUrls.TryGetValue(fileName, out var url) ? url : null;

    private async Task<VelopackAssetFeed> LoadFeedAsync(CancellationToken cancelToken)
    {
        if (_cachedFeed != null) return _cachedFeed;

        // ResolvePlanAsync를 거치지 않고 Velopack이 먼저 피드를 물어보는 경로를 위한 대비다.
        // 그때는 사슬을 확인할 기준 버전이 없으므로 대상의 full만 내놓는다
        var targetFull = await FindAssetAsync(
            _target, _target.PackageFileName, VelopackAssetType.Full, cancelToken);

        _cachedFeed = new VelopackAssetFeed { Assets = [targetFull] };
        return _cachedFeed;
    }

    /// <summary>
    /// 릴리스의 피드에서 자산 하나를 찾는다.
    ///
    /// 피드에는 delta를 만들 때 기준으로 삼은 버전의 full도 적혀 있는데, 그 파일은 이
    /// 릴리스에 올라가 있지 않아 주소를 보장할 수 없다. 그래서 파일명까지 대조해 이 릴리스가
    /// 실제로 갖고 있는 자산만 남긴다
    /// </summary>
    private async Task<VelopackAsset> FindAssetAsync(
        ReleaseVersion release,
        string fileName,
        VelopackAssetType type,
        CancellationToken cancelToken)
    {
        var json = await GitHubHttp.GetStringAsync(_http, release.FeedUrl, cancelToken);
        var feed = VelopackAssetFeed.FromJson(json);

        var asset = feed.Assets.FirstOrDefault(
            candidate => candidate.Type == type
                         && candidate.Version.CompareTo(release.Version) == 0
                         && string.Equals(candidate.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        if (asset == null)
        {
            throw new InvalidOperationException(
                $"릴리스 {release.Tag}의 피드에서 {fileName} 정보를 찾지 못했습니다.");
        }

        return asset;
    }

    private async Task DownloadFileAsync(string url, string localFile, Action<int>? progress, CancellationToken cancelToken)
    {
        using var response = await GitHubHttp.GetAsync(_http, url, cancelToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0L;

        using var source = await response.Content.ReadAsStreamAsync(cancelToken);
        using var destination = File.Create(localFile);

        // 한 번 읽을 때마다 시한을 다시 건다. 데이터가 계속 오면 시한도 계속 밀리고,
        // 조용히 멈추면 StallTimeout 뒤에 취소되어 설치가 끝나지 않는 상태로 남지 않는다
        using var stallWatch = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
        stallWatch.CancelAfter(StallTimeout);

        var buffer = new byte[81920];
        long receivedBytes = 0;
        var lastPercent = -1;
        int read;

        while ((read = await source.ReadAsync(buffer, stallWatch.Token)) > 0)
        {
            stallWatch.CancelAfter(StallTimeout);

            await destination.WriteAsync(buffer.AsMemory(0, read), cancelToken);
            receivedBytes += read;

            if (totalBytes <= 0) continue;

            // 스트림 조각마다 UI를 갱신하지 않도록 백분율이 달라졌을 때만 알린다
            var percent = (int)(receivedBytes * 100 / totalBytes);
            if (percent == lastPercent) continue;

            lastPercent = percent;
            progress?.Invoke(percent);
        }
    }

}
