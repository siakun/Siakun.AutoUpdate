# Siakun.AutoUpdate 프로젝트 설계 문서

<!--
INTENT
라이브러리를 고치거나 앱에 붙이기 전에 읽는 설계 기록이다. 코드만 읽어서는 알 수 없는 것을 남긴다.
라이브러리가 기대는 Velopack 내부 동작, 그 동작이 정하는 delta의 성립 조건, 아직 정하지 않은 결정이다.
Velopack 동작은 소스를 읽어야만 확인되므로, 같은 조사를 되풀이하지 않게 결과와 확인 방법을 함께 적는다.
사용법은 README.md가 맡으므로 여기서 되풀이하지 않는다.
-->

## 개요

`Siakun.AutoUpdate`는 GitHub Releases와 Velopack으로 배포하는 Windows 데스크톱 앱에 자동 업데이트, 버전 선택, 다운그레이드를 붙이는 라이브러리입니다. 패키지 생성과 파일 교체, 체크섬 검증은 Velopack이 맡고, 이 라이브러리는 어느 릴리스를 언제 받아 언제 적용할지 정합니다. 화면, 설정 저장 형식, 프로세스 종료는 호출 앱이 맡습니다.

연결 방법과 지원하는 배포 구조는 [README.md](README.md)에 있습니다.

## 두 업데이트 경로

업데이트는 두 경로로 나뉘며, delta를 구성하는 주체가 다릅니다.

| 항목 | 자동 업데이트 | 버전 전환 |
|---|---|---|
| 진입점 | `UpdateService.CheckAndDownloadAsync()` | `UpdateService.InstallVersionAsync()` |
| 대상 | 베타 수신 설정에 맞는 최신 버전 | 사용자가 고른 버전(다운그레이드 포함) |
| 릴리스 조회 | Velopack `GithubSource` | `GitHubReleaseCatalog` |
| delta 구성 | Velopack `UpdateManager` | `GitHubReleaseSource.ResolvePlanAsync()` |
| 사슬 검증 | 하지 않음, 버전 범위만 봄 | 피드에 기록된 기준을 버전, 크기, 해시로 대조 |

여기서 사슬은 여러 버전을 건너뛸 때 delta를 순서대로 이어 적용하는 경로입니다.

두 경로 모두 받은 패키지를 `ApplyOnExit()`에서 Velopack의 `WaitExitThenApplyUpdates`로 예약합니다. Velopack은 Update.exe를 `--silent apply --waitPid <앱 PID>`로 띄우고, Update.exe는 앱이 끝나기를 기다린 뒤 설치 폴더의 `current` 폴더를 교체합니다. 재시작을 요청하지 않은 적용에는 `--norestart`가 붙습니다.

## Velopack 동작

이 절의 내용은 Velopack 0.0.1298 소스를 읽어 확인했습니다. `Siakun.AutoUpdate/Siakun.AutoUpdate.csproj`가 참조하는 Velopack 버전이 이와 다르면 "다시 확인하는 방법" 절의 순서대로 확인하고 이 절을 고칩니다.

### 조회

- `GithubSource`는 GitHub API로 릴리스 목록의 첫 페이지 10개를 받아 게시 시각이 늦은 순으로 정렬하고, 베타 수신을 끄면 그중 프리릴리스를 뺍니다. 프리릴리스도 10개에 포함되므로 베타가 많을수록 조회되는 정식 릴리스가 줄어듭니다.
- 받은 릴리스마다 `releases.{channel}.json`을 내려받아 모든 자산을 피드 하나로 합칩니다. 피드 파일이 없는 릴리스는 건너뛰고, 피드 다운로드가 실패하면 조회 전체가 실패합니다.
- 각 자산은 자기가 속한 릴리스를 기억하고, 파일은 그 릴리스에서 받습니다. 토큰이 없으면 API가 아닌 브라우저 다운로드 주소를 쓰므로 조회 한 번에 드는 API 요청은 1회입니다.

### delta 선택

- 합친 피드에서 버전이 가장 높은 full이 대상입니다.
- 로컬 `packages` 폴더에 full 패키지가 없거나, 피드에 대상 버전의 delta가 없으면 full을 받습니다.
- 로컬 full보다 높고 대상 이하인 delta를 모두 모아 버전 순서로 적용합니다. 각 delta가 바로 앞 버전을 기준으로 만들어졌는지는 확인하지 않습니다.
- delta 수가 `UpdateOptions.MaximumDeltasBeforeFallback`(기본 10)을 넘거나 합계가 full보다 크면 full을 받습니다.

### 다운로드와 적용

- 받은 delta 파일은 하나씩 피드의 체크섬으로 검증한 뒤 Update.exe의 `patch` 명령으로 기준 패키지에 적용합니다. 적용이 실패하면 full을 다시 받습니다.
- delta로 조립한 패키지는 대상 full의 해시로 다시 검증하지 않습니다. 패키지 전체의 체크섬 검증은 full을 받았을 때만 합니다.
- 다운로드가 끝나면 성공, 실패, 취소와 관계없이 `packages` 폴더에서 이번 대상 외의 nupkg를 모두 지웁니다. 실패하거나 취소하면 기준 패키지까지 지워져 다음 업데이트는 full입니다.
- 받은 패키지에 든 Update.exe를 꺼내 기존 Update.exe를 교체합니다. 업데이트 프로그램도 앱과 함께 갱신됩니다.

### Update.exe의 patch 명령

- 기준 패키지를 풀고 delta에 든 파일별 zstd 패치를 적용합니다. vpk가 zstd CLI의 `--patch-from`으로 패치를 만들고 CLI는 기본으로 체크섬을 넣으므로, 기준이 틀리면 바뀐 파일의 패치가 실패합니다.
- 크기가 0인 패치는 바뀌지 않은 파일이라는 표시여서 기준 파일을 검사 없이 그대로 둡니다.
- 결과를 새로 압축해 nupkg를 만듭니다. 그래서 delta로 조립한 로컬 패키지는 게시된 full과 크기와 해시가 다릅니다.

### 로컬 패키지와 설치 형태

- 로컬 패키지의 크기와 해시는 `packages` 폴더에 있는 파일에서 계산합니다.
- Setup.exe는 게시된 full을 그대로 `packages` 폴더에 복사하므로, 설치 직후 첫 업데이트부터 delta를 쓸 수 있습니다. 포터블 zip에는 이 패키지가 없어 첫 업데이트는 full입니다.
- 포터블도 설치본과 같은 구조(루트의 Update.exe와 앱 폴더의 매니페스트)로 인식해 `IsInstalled`가 true입니다. `IsPortable`은 루트의 `.portable` 파일로 판정합니다. 적용 단계는 포터블에서 제거 항목 등록과 바로 가기 갱신만 건너뛰고 파일은 그대로 교체합니다. Velopack 자체는 포터블 자동 업데이트를 막지 않습니다.

### vpk download

- `vpk download github`은 `--pre` 없이 실행하면 합친 피드에서 가장 높은 정식 버전의 full을 받고, `vpk pack`은 그 full을 기준으로 delta를 만듭니다. 그래서 베타의 delta도 그 시점의 최신 정식 버전을 기준으로 만들어집니다.

## delta가 성립하는 조건

자동 업데이트가 delta를 쓰려면 다음이 모두 맞아야 합니다.

- 사이의 릴리스마다 delta가 있고, 각 delta가 바로 앞 버전을 기준으로 만들어져 있습니다. 정식 릴리스를 `vpk download github`, `vpk pack` 순서로 연달아 내면 이렇게 됩니다.
- 사이의 릴리스가 모두 조회 범위 안에 있습니다.
- 로컬에 기준 full 패키지가 있습니다.

조건이 깨지는 대표 경우입니다.

| 경우 | 결과 |
|---|---|
| delta 없이 나간 릴리스를 건너는 업데이트 | delta를 모두 받은 뒤 적용 실패, full을 다시 받음 |
| 베타에서 출발하거나 베타를 거쳐 가는 업데이트 | 사슬이 이어지지 않아 적용 실패 후 full |
| 조회 범위보다 오래 뒤처진 설치본 | 앞쪽 delta가 빠져 적용 실패 후 full |
| 기준 패키지가 없는 설치본 | 처음부터 full |

베타의 delta는 직전 베타가 아니라 그 시점의 최신 정식 버전을 기준으로 만들어지므로, 베타에서 출발하거나 베타를 거치는 사슬은 이어지지 않습니다. 기준 패키지가 없는 설치본은 포터블의 첫 업데이트와 직전 다운로드가 실패하거나 취소된 경우입니다.

앱 어셈블리는 대개 버전 정보를 담아 릴리스마다 바뀌므로, 기준이 어긋나면 사실상 적용 단계에서 실패합니다. 잘못 조립된 패키지가 설치될 위험은 낮지만 헛받은 delta만큼 전송이 낭비됩니다.

버전 전환 경로는 대상 피드에서 출발해 피드에 기록된 기준을 따라 거슬러 올라가며, 로컬 패키지와 버전, 크기, 해시가 모두 같은 지점을 찾습니다. 사슬이 끊기면 delta를 받기 전에 full로 정하므로 헛받는 전송이 없습니다. 대신 로컬 패키지가 게시된 full과 바이트까지 같아야 하므로, delta로 업데이트해 온 설치본에서는 상향 전환도 full을 받습니다. 사슬 길이와 크기 비율의 상한은 `GitHubReleaseSource`의 상수로 정합니다.

## 릴리스 피드 계약

라이브러리는 표준 배포 흐름이 만드는 릴리스를 전제로 합니다. 표준 배포 흐름은 `vpk download github`로 직전 릴리스를 받고 `vpk pack`으로 묶은 뒤, 그 버전의 nupkg와 피드만 릴리스에 올리는 흐름입니다.

- 릴리스마다 full nupkg 하나, delta nupkg 하나(첫 릴리스에는 없음), `releases.{channel}.json` 하나가 있습니다. full이 둘 이상인 릴리스가 있으면 카탈로그가 조회를 거부합니다.
- 피드에는 자기 full과 자기 delta, 그리고 delta의 기준이 된 full이 적힙니다. 기준 full 파일은 그 릴리스에 올라가 있지 않습니다.
- 버전 전환의 사슬 탐색은 피드에서 자기 버전이 아닌 full이 정확히 하나일 때만 기준을 확정합니다. 여러 버전을 누적한 피드에서는 기준을 확정하지 못해 full을 받습니다.

## 호출 앱에 붙일 때 확인할 것

기본 연결 절차는 README에 있습니다. 자체 Velopack 업데이트 코드를 쓰던 앱을 옮길 때는 다음을 더 확인합니다.

- 앱에서 Velopack 버전을 따로 지정하지 않습니다. 라이브러리는 Velopack `UpdateManager`의 protected 멤버인 `Locator`와 적용 API에 기대므로, 다른 버전이 섞이면 깨질 수 있습니다. Velopack은 이 패키지를 참조하면 함께 들어옵니다.
- `VelopackApp.Build()` 초기화는 앱의 `Main` 첫머리에 그대로 둡니다. 제거 훅처럼 앱에만 있는 수명주기 처리도 앱에 남깁니다.
- 설정 화면이 설정 값을 자기 속성으로 들고 있다가 저장할 때 통째로 덮어쓰는 구조라면, `InstallVersionAsync()`를 부르기 전에 화면 쪽 자동 업데이트 값도 끕니다. 라이브러리는 `IUpdateSettings`의 값만 끄므로, 설치가 실패한 뒤 다른 설정을 저장하면 화면에 남은 옛 값이 되살아납니다.
- `CurrentVersion`은 개발 빌드에서 null입니다. 이 값으로 버전을 표시하면 개발 빌드에서는 버전이 보이지 않습니다.
- 로컬 빌드 스크립트에 `vpk download github` 단계가 없으면 그 스크립트로 낸 릴리스에는 delta가 없고, 그 릴리스를 건너는 사용자는 full을 받습니다.

## 열린 결정과 한계

### 포터블 판

라이브러리는 포터블 판도 설치본과 같이 자동 업데이트와 버전 전환 대상에 포함합니다. Velopack은 포터블 업데이트를 막지 않으므로, 제외하면 그것은 라이브러리의 선택이 됩니다. 제외한 채 배포하면 그 버전을 받은 포터블 사용자는 앱 안에서 다음 버전으로 넘어갈 방법이 없고, 이미 배포된 버전의 업데이트 코드는 나중에 고칠 수 없습니다. 되돌릴 수 없는 쪽을 피하려고 포함했고, `IsInstalled`만으로 설치본을 판정하던 앱은 라이브러리로 옮겨도 포터블 사용자의 업데이트 동작이 그대로입니다.

### 자동 경로의 사슬 미검증

Velopack이 사슬을 검증하지 않아 "delta가 성립하는 조건" 절의 표에 적은 경우마다 전송이 낭비됩니다. 자동 경로에도 라이브러리의 사슬 탐색을 쓰면 줄일 수 있지만, 그 탐색은 로컬 패키지 해시가 게시본과 같아야 하므로 delta로 업데이트해 온 설치본은 오히려 매번 full을 받게 됩니다. 도입하려면 로컬 패키지와 기준을 대조하는 규칙부터 다시 정해야 합니다.

### 받기 전 확인 API

`CheckAndDownloadAsync()`는 확인과 다운로드를 한 번에 합니다. 새 버전이 있다고 먼저 알리고 사용자가 받기를 고르는 화면이 필요하면 확인만 하는 API를 추가해야 합니다.

### 실제 설치 검증

테스트는 가짜 HTTP 응답과 가짜 `UpdateManager`로 카탈로그 조회, delta 계획, 서비스 상태 전환을 검사합니다. Velopack의 실제 다운로드, 패치, 적용은 검사하지 않으므로 배포 전에 Velopack으로 설치한 테스트 앱에서 다음을 확인합니다.

- 한 단계와 여러 단계 delta 자동 업데이트
- 베타 수신 상태의 자동 업데이트
- 다운그레이드와 상향 버전 전환
- 포터블 판의 자동 업데이트와 버전 전환

## 다시 확인하는 방법

Velopack 버전을 바꾸면 그 태그의 소스를 받아 아래 파일을 다시 읽고 "Velopack 동작" 절을 고칩니다.

```bash
git init velopack && cd velopack
git remote add origin https://github.com/velopack/velopack
git fetch --depth 1 origin tag <버전>
git checkout <버전>
```

| 확인할 동작 | 파일 |
|---|---|
| 조회 범위와 프리릴리스 제외 | `src/lib-csharp/Sources/GithubSource.cs`의 `GetReleases` |
| 피드 병합과 자산별 다운로드 위치 | `src/lib-csharp/Sources/GitBase.cs` |
| delta 선택, full 되돌리기, 패키지 정리 | `src/lib-csharp/UpdateManager.cs`의 `CheckForUpdatesAsync`, `CreateDeltaUpdateStrategy`, `DownloadUpdatesAsync` |
| 적용 프로세스 인자 | `src/lib-csharp/UpdateExe.cs` |
| 로컬 패키지의 크기와 해시 | `src/lib-csharp/VelopackAsset.cs`의 `FromZipPackage` |
| 설치 판정과 포터블 판정 | `src/lib-csharp/Locators/WindowsVelopackLocator.cs` |
| delta 적용과 재압축 | `src/bins/src/commands/patch.rs` |
| 설치 시 패키지 복사 | `src/bins/src/commands/install.rs` |
| 포터블 적용 | `src/bins/src/commands/apply_windows_impl.rs` |
| delta 생성 | `src/vpk/Velopack.Packaging/Zstd.cs` |
| `vpk download`가 받는 릴리스 | `src/vpk/Velopack.Deployment/GitHubRepository.cs`, `src/vpk/Velopack.Deployment/_Repository.cs`의 `DownloadLatestFullPackageAsync` |
