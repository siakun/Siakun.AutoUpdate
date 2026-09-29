# Siakun.AutoUpdate

<!--
INTENT
앱마다 업데이트 코드를 복사하지 않고 재사용하도록 버전 전환 기능을 라이브러리로 제공한다.
배포 형식과 검증은 Velopack에 맡기고, 이 라이브러리는 릴리스 선택과 다운로드, 적용 시점을 조율한다.
라이브러리가 UI와 설정 파일 형식까지 정하면 호출 앱의 구조를 강제하므로 그 경계는 인터페이스와 이벤트로 남긴다.
-->

GitHub Releases와 Velopack으로 배포하는 Windows 앱에 자동 업데이트와 버전 선택, 다운그레이드를
붙이는 라이브러리입니다. UI 프레임워크에 의존하지 않으며, 설치 패키지 생성과 파일 교체는 Velopack이
맡습니다.

## 설치

```powershell
dotnet add package Siakun.AutoUpdate
```

## 호출 앱에 연결

호출 앱에서 `IUpdateSettings`를 구현합니다. `AutoUpdateEnabled`의 setter는 설정을 저장한 뒤
반환해야 합니다. `PrereleaseEnabled`는 앱의 베타 수신 설정을 읽습니다. 설정 파일과 사용자 데이터는
업데이트로 교체되는 설치 디렉터리 밖에 둡니다.

```csharp
using Siakun.AutoUpdate;

static UpdateService CreateUpdater(IUpdateSettings settings, Action requestExit)
{
    var catalog = new GitHubReleaseCatalog("https://github.com/owner/repository");
    var updates = new UpdateService(catalog, settings, Console.WriteLine);

    updates.UpdateReady += (_, e) => Console.WriteLine($"적용할 버전: {e.Version}");
    updates.RestartRequested += (_, _) => requestExit();
    return updates;
}
```

저장소 주소는 호출 앱이 릴리스를 배포하는 주소로 바꿉니다. `requestExit`에는 앱의 정상 종료를
요청하는 함수를 전달합니다. 이벤트가 UI 스레드에서 실행된다는 보장은 없으므로 WPF 앱은
`Dispatcher.BeginInvoke` 등으로 UI 스레드에 종료 요청과 화면 갱신을 전달합니다.

앱의 진입점에서는 다른 초기화와 단일 인스턴스 검사보다 먼저 Velopack을 초기화합니다.
준비된 업데이트는 종료 경로에서 적용하므로 시작 시 자동 적용은 끕니다.

```csharp
Velopack.VelopackApp.Build()
    .SetAutoApplyOnStartup(false)
    .Run();
```

Velopack 초기화와 적용 계약은 [공식 통합 안내](https://docs.velopack.io/integrating/overview)를 따릅니다.

앱 화면을 표시한 뒤 `CheckAndDownloadAsync()`를 호출합니다. 새 버전을 받으면 `UpdateReady`가
발생하며, 사용자가 즉시 적용을 선택하면 `ApplyAndRestartNow()`를 호출합니다. 이 메서드는
`RestartRequested`를 발생시킵니다.

호출 앱은 설정 저장과 브라우저, 파일 감시 등의 자원 정리를 마친 뒤 종료 직전에 `ApplyOnExit()`을
호출합니다. 반환값이 `true`이면 Velopack에 적용을 예약한 상태입니다. `false`이면 이번 호출에서
예약하지 않았으며, 실패 원인은 생성자에 전달한 로그 콜백으로 확인합니다. 라이브러리에서 프로세스를
종료하지 않으므로 호출 앱이 정상 종료를 끝내야 합니다.

## 버전 선택

`GetAvailableVersionsAsync()`는 설치할 수 있는 버전을 최신 순으로 반환합니다. 베타 수신을 꺼 두면
프리릴리스를 제외합니다. 화면에서 선택한 `ReleaseVersion`을 `InstallVersionAsync()`에 전달합니다.

```csharp
static async Task InstallSelection(UpdateService updates, ReleaseVersion selected,
    IProgress<int> progress, CancellationToken cancellation)
{
    await updates.InstallVersionAsync(selected,
        progress: progress.Report,
        cancelToken: cancellation);
}
```

`Progress<int>`를 UI 스레드에서 생성하면 진행률을 그 스레드에서 받을 수 있습니다.
`onDownloadSizeResolved` 콜백은 실제 다운로드할 패키지 크기를 바이트 단위로 알립니다.
delta를 포기하고 full로 전환하면 크기도 다시 알립니다.

구버전이나 현재 수신 설정의 최신 버전이 아닌 대상을 고르면 다운로드 전에 자동 업데이트를 끕니다.
그 뒤 다운로드가 실패하거나 취소돼도 설정은 꺼진 상태로 남습니다. 사용자가 자동 업데이트를
다시 켜는 시점은 호출 앱에서 정합니다. 다운로드를 이미 마친 패키지는 자동 업데이트를 꺼도
다음 실행에서 적용 대기 상태로 복원합니다.

자동 다운로드와 수동 설치는 직렬로 처리하며 수동 선택이 적용 대상을 결정합니다. 수동 다운로드 중
앱을 닫거나 다운로드가 실패하면, 이번 실행에서 앞서 준비한 자동 업데이트를 대신 적용하지 않습니다.

## 지원하는 배포 구조

공개 GitHub 저장소를 대상으로 하며, 릴리스마다 한 앱과 한 아키텍처의 full 패키지와 Velopack
피드가 함께 있어야 합니다. 한 릴리스에 full 패키지가 여러 개 있으면 임의로 고르지 않고 조회에
실패합니다. 기본 채널은 `win`이며 `GitHubReleaseCatalog`의 `channel` 인자로 다른 피드를 지정합니다.
비공개 저장소 인증과 여러 아키텍처의 패키지 선택은 제공하지 않습니다.

상향 전환은 피드에 기록된 기준 패키지의 버전과 해시를 대조해 delta 경로를 구성합니다. 기준을
확정할 수 없거나 경로가 끊기면 full을 받습니다. 다운그레이드는 full을 사용합니다. 패키지 다운로드와
체크섬 검증은 Velopack을 거칩니다.

포터블 판은 설치본과 같이 자동 업데이트와 버전 전환을 지원합니다. 포터블 zip에는 기준 패키지가
없으므로 첫 업데이트는 full을 받습니다. 개발 빌드에서는 목록을 조회할 수 있지만 설치 버전 전환은
지원하지 않습니다. 사용자 데이터와 설정 형식의 다운그레이드 호환성은 호출 앱이 관리합니다. 자동 업데이트 설정을
읽지 않는 옛 버전으로 돌아간 뒤의 동작은 그 버전의 코드가 결정합니다.

`GitHubReleaseCatalog`와 `GitHubReleaseSource`는 `HttpClient`를 받을 수 있습니다. 전달한 클라이언트의
수명은 호출자가 관리하며 목록 조회와 수동 버전 전환에 사용합니다. 자동 업데이트 조회와 다운로드는
Velopack의 `GithubSource`를 사용합니다.

## 개발과 패키징

저장소 루트에서 실행합니다. 대상 프레임워크와 의존 패키지는 `Siakun.AutoUpdate/Siakun.AutoUpdate.csproj`에서 관리합니다.

```powershell
dotnet build Siakun.AutoUpdate.slnx --configuration Release
dotnet test Siakun.AutoUpdate.slnx --configuration Release
dotnet pack Siakun.AutoUpdate/Siakun.AutoUpdate.csproj --configuration Release --output artifacts
```

생성한 NuGet 패키지를 로컬 패키지 소스에 추가하거나, 호출 앱의 프로젝트에서 이 프로젝트를
`ProjectReference`로 참조합니다. 테스트는 HTTP 응답과 설치 관리자를 대체해 조회, 다운로드 계획,
취소, 종료 순서를 검사합니다. 실제 설치 파일을 교체하거나 앱을 재시작하지 않으므로 배포 전에는
Velopack으로 설치한 테스트 앱에서 업데이트와 다운그레이드를 별도로 확인합니다.

## 라이선스

[Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0)으로 배포합니다.
