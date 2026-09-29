using NuGet.Versioning;
using Velopack;
using Velopack.Sources;

namespace Siakun.AutoUpdate;

/// <summary>자동 업데이트와 수동 버전 전환을 준비하고, 호출 앱의 정상 종료 뒤 적용합니다.</summary>
// INTENT: UI 프레임워크와 프로세스 종료를 소유하지 않는다. 다운로드 완료와 재시작 요청은
// 이벤트로 전달하고, 앱이 설정 저장과 자원 정리를 마친 뒤 ApplyOnExit을 호출한다.
// 자동 다운로드와 수동 선택이 겹치면 수동 선택이 우선한다. 같은 패키지 폴더를 갱신하므로
// 완료 상태만 잠그지 않고 다운로드 작업 자체도 직렬화한다.
// 포터블 판도 설치본과 같이 업데이트한다. Velopack은 포터블도 IsInstalled로 보고 파일을 교체하며,
// 여기서 제외하면 그 버전을 받은 포터블 사용자는 앱 안에서 다음 버전으로 넘어갈 수 없다.
public sealed class UpdateService
{
    private readonly GitHubReleaseCatalog _catalog;
    private readonly IUpdateSettings _settings;
    private readonly Action<string>? _log;
    private readonly Func<IUpdateSource, UpdateOptions, PackageUpdateManager> _createManager;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _stateLock = new();
    private PackageUpdateManager? _automaticManager;
    private bool _acceptsPrerelease;
    private PackageUpdateManager? _pendingManager;
    private UpdateInfo? _pendingUpdate;
    private bool _pendingIsManual;
    private bool _manualInProgress;
    private bool _restartAfterApply;
    private bool _applyScheduled;
    private bool _isExiting;
    private long _selectionRevision;

    public UpdateService(GitHubReleaseCatalog catalog, IUpdateSettings settings, Action<string>? log = null)
        : this(catalog, settings, log, (source, options) => new PackageUpdateManager(source, options)) { }

    internal UpdateService(GitHubReleaseCatalog catalog, IUpdateSettings settings, Action<string>? log,
        Func<IUpdateSource, UpdateOptions, PackageUpdateManager> createManager)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);
        _catalog = catalog;
        _settings = settings;
        _log = log;
        _createManager = createManager;
    }

    /// <summary>업데이트를 준비한 스레드에서 알립니다. UI 갱신은 호출 앱에서 전환합니다.</summary>
    public event EventHandler<UpdateReadyEventArgs>? UpdateReady;

    /// <summary>호출 앱이 설정 저장과 정상 종료를 시작하도록 요청합니다.</summary>
    public event EventHandler? RestartRequested;

    public bool UpdateDownloaded { get { lock (_stateLock) return _pendingUpdate != null; } }

    public bool IsManagedInstall
    {
        get
        {
            try { return AutomaticManager.IsInstalled; }
            catch { return false; }
        }
    }

    public SemanticVersion? CurrentVersion
    {
        get
        {
            try { return IsManagedInstall ? AutomaticManager.CurrentVersion : null; }
            catch { return null; }
        }
    }

    private PackageUpdateManager AutomaticManager
    {
        get
        {
            lock (_stateLock)
            {
                var prerelease = _settings.PrereleaseEnabled;
                if (_automaticManager == null || _acceptsPrerelease != prerelease)
                {
                    _automaticManager = _createManager(
                        new GithubSource(_catalog.RepositoryUrl, null, prerelease),
                        new UpdateOptions { ExplicitChannel = _catalog.Channel });
                    _acceptsPrerelease = prerelease;
                }
                return _automaticManager;
            }
        }
    }

    /// <summary>자동 업데이트 실패는 로그로 전달합니다. 호출자가 요청한 취소는 예외로 전달합니다.</summary>
    public async Task CheckAndDownloadAsync(CancellationToken cancelToken = default)
    {
        await _operationLock.WaitAsync(cancelToken).ConfigureAwait(false);
        try
        {
            long selectionRevision;
            lock (_stateLock)
            {
                if (_manualInProgress || _pendingIsManual || _isExiting) return;
                selectionRevision = _selectionRevision;
            }

            var manager = AutomaticManager;
            if (!manager.IsInstalled) return;

            // 이전 실행에서 준비한 패키지는 자동 업데이트 설정을 끈 뒤에도 적용할 수 있다.
            var downloaded = manager.UpdatePendingRestart;
            if (downloaded != null)
            {
                var downgrade = manager.CurrentVersion != null && downloaded.Version < manager.CurrentVersion;
                ValidatePackage(manager, downloaded);
                if (SetPending(manager, new UpdateInfo(downloaded, downgrade), manual: false, selectionRevision))
                    UpdateReady?.Invoke(this, new(downloaded.Version));
                return;
            }

            if (!_settings.AutoUpdateEnabled) return;
            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            cancelToken.ThrowIfCancellationRequested();
            lock (_stateLock)
                if (_manualInProgress || _isExiting) return;
            if (update == null || !_settings.AutoUpdateEnabled) return;

            ValidatePackage(manager, update.TargetFullRelease);
            await manager.DownloadUpdatesAsync(update, cancelToken: cancelToken).ConfigureAwait(false);
            if (SetPending(manager, update, manual: false, selectionRevision))
                UpdateReady?.Invoke(this, new(update.TargetFullRelease.Version));
        }
        catch (OperationCanceledException) when (cancelToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log?.Invoke($"[UpdateService] Update check failed: {ex.Message}"); }
        finally { _operationLock.Release(); }
    }

    public async Task<IReadOnlyList<ReleaseVersion>> GetAvailableVersionsAsync(CancellationToken cancelToken = default)
    {
        var releases = await _catalog.GetVersionsAsync(cancelToken).ConfigureAwait(false);
        return _settings.PrereleaseEnabled ? releases : releases.Where(release => !release.IsPrerelease).ToArray();
    }

    /// <summary>지정한 버전을 준비한 뒤 정상 종료와 재시작을 요청합니다.</summary>
    public async Task InstallVersionAsync(ReleaseVersion target, Action<int>? progress = null,
        Action<long>? onDownloadSizeResolved = null, CancellationToken cancelToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_stateLock)
        {
            if (_manualInProgress) throw new InvalidOperationException("다른 버전 설치가 이미 진행 중입니다.");
            if (_isExiting) throw new InvalidOperationException("앱 종료가 이미 시작됐습니다.");
            _manualInProgress = true;
            // 수동 선택이 취소돼도 그보다 먼저 시작한 자동 다운로드 결과는 되살리지 않는다.
            _selectionRevision++;
            // 새 선택이 실패하거나 취소돼도 이전 자동 업데이트를 대신 적용하지 않는다.
            _pendingUpdate = null;
            _pendingManager = null;
            _pendingIsManual = false;
        }

        var entered = false;
        var restart = false;
        try
        {
            await _operationLock.WaitAsync(cancelToken).ConfigureAwait(false);
            entered = true;
            var versions = await FetchVersionsSafelyAsync(cancelToken).ConfigureAwait(false);
            var source = _catalog.CreateSource(target, versions);
            if (onDownloadSizeResolved != null) source.FullFallbackStarted += onDownloadSizeResolved;
            var manager = _createManager(source,
                new UpdateOptions { AllowVersionDowngrade = true, ExplicitChannel = _catalog.Channel });
            if (!manager.IsInstalled)
                throw new InvalidOperationException("Velopack으로 설치된 실행 파일에서만 버전을 바꿀 수 있습니다.");

            var downgrade = manager.CurrentVersion != null && target.Version < manager.CurrentVersion;
            VelopackAsset? basePackage = null;
            if (!downgrade)
            {
                try { basePackage = manager.GetBasePackage(); }
                catch (Exception ex) { _log?.Invoke($"[UpdateService] Local base package lookup failed: {ex.Message}"); }
                if (basePackage?.Version.CompareTo(manager.CurrentVersion) != 0) basePackage = null;
            }
            var plan = await source.ResolvePlanAsync(basePackage, cancelToken).ConfigureAwait(false);
            ValidatePackage(manager, plan.TargetFull);

            var latest = versions.Where(v => _settings.PrereleaseEnabled || !v.IsPrerelease).FirstOrDefault();
            if (downgrade || latest == null || target.Version != latest.Version
                || (target.IsPrerelease && !_settings.PrereleaseEnabled))
                _settings.AutoUpdateEnabled = false;

            // BaseRelease가 있어야 Velopack이 delta를 사용한다.
            var update = basePackage != null && plan.Deltas.Length > 0
                ? new UpdateInfo(plan.TargetFull, downgrade, basePackage, plan.Deltas)
                : new UpdateInfo(plan.TargetFull, downgrade);
            onDownloadSizeResolved?.Invoke(plan.Deltas.Length > 0
                ? plan.Deltas.Sum(delta => delta.Size) : plan.TargetFull.Size);
            await manager.DownloadUpdatesAsync(update, progress, cancelToken).ConfigureAwait(false);
            cancelToken.ThrowIfCancellationRequested();
            restart = SetPending(manager, update, manual: true);
        }
        finally
        {
            lock (_stateLock) _manualInProgress = false;
            if (entered) _operationLock.Release();
        }
        if (restart) RequestRestart();
    }

    private async Task<IReadOnlyList<ReleaseVersion>> FetchVersionsSafelyAsync(CancellationToken token)
    {
        try { return await _catalog.GetVersionsAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log?.Invoke($"[UpdateService] Version list lookup failed ({ex.Message}), using full package");
            return [];
        }
    }

    public void ApplyAndRestartNow()
    {
        lock (_stateLock)
        {
            if (_pendingUpdate == null || _manualInProgress || _isExiting) return;
            _restartAfterApply = true;
        }
        RequestRestart();
    }

    /// <summary>앱 종료를 표시하고 준비한 업데이트를 적용하도록 예약합니다. 예약하지 못하면 false입니다.</summary>
    public bool ApplyOnExit()
    {
        PackageUpdateManager manager;
        UpdateInfo update;
        bool restart;
        lock (_stateLock)
        {
            _isExiting = true;
            if (_pendingUpdate == null || _pendingManager == null || _applyScheduled || _manualInProgress) return false;
            manager = _pendingManager;
            update = _pendingUpdate;
            restart = _restartAfterApply;
            _applyScheduled = true;
        }
        try
        {
            manager.ScheduleApply(update.TargetFullRelease, restart);
            return true;
        }
        catch (Exception ex)
        {
            lock (_stateLock) _applyScheduled = false;
            _log?.Invoke($"[UpdateService] ApplyOnExit failed: {ex.Message}");
            return false;
        }
    }

    private static void ValidatePackage(PackageUpdateManager manager, VelopackAsset asset)
    {
        if (!string.Equals(asset.PackageId, manager.AppId, StringComparison.Ordinal))
            throw new InvalidOperationException("선택한 패키지의 앱 ID가 설치된 앱과 다릅니다.");
    }

    private bool SetPending(PackageUpdateManager manager, UpdateInfo update, bool manual, long selectionRevision = 0)
    {
        lock (_stateLock)
        {
            if (_isExiting || (!manual && (_manualInProgress || _pendingIsManual || selectionRevision != _selectionRevision))) return false;
            _pendingManager = manager;
            _pendingUpdate = update;
            _pendingIsManual = manual;
            _restartAfterApply = manual;
            return true;
        }
    }

    private void RequestRestart()
    {
        var handler = RestartRequested;
        if (handler == null)
            _log?.Invoke("[UpdateService] Restart requested without an application lifecycle handler");
        else
            handler(this, EventArgs.Empty);
    }
}
