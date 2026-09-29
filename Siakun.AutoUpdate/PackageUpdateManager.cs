using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Siakun.AutoUpdate;

// INTENT: delta의 출발점은 실행 중인 버전 번호만으로 정할 수 없다.
// UpdateManager가 protected로 제공하는 Locator에서 실제 로컬 패키지를 얻는다.
internal class PackageUpdateManager(IUpdateSource source, UpdateOptions options, IVelopackLocator? locator = null)
    : UpdateManager(source, options, locator)
{
    internal virtual VelopackAsset? GetBasePackage() => Locator.GetLatestLocalFullPackage();

    internal virtual void ScheduleApply(VelopackAsset asset, bool restart) =>
        WaitExitThenApplyUpdates(asset, silent: true, restart: restart);
}
