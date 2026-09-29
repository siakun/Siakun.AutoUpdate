using NuGet.Versioning;
using Velopack;

namespace Siakun.AutoUpdate;

/// <summary>
/// 설치할 수 있는 릴리스 하나의 정보 (설정 화면 버전 목록의 항목)
/// </summary>
public sealed class ReleaseVersion
{
    /// <summary> GitHub 릴리스 태그 (예: v0.1.0) </summary>
    public required string Tag { get; init; }

    /// <summary> 태그에서 v를 뗀 시맨틱 버전. 정렬과 현재 버전 비교에 쓴다 </summary>
    public required SemanticVersion Version { get; init; }

    /// <summary> GitHub에서 프리릴리스로 표시한 릴리스인지 여부 </summary>
    public required bool IsPrerelease { get; init; }

    /// <summary> 이 릴리스의 full nupkg 파일명 (피드에서 대상 패키지를 가려낼 때 쓴다) </summary>
    public required string PackageFileName { get; init; }

    /// <summary> full nupkg 다운로드 주소 </summary>
    public required string PackageUrl { get; init; }

    /// <summary> releases.{channel}.json 다운로드 주소 (패키지 체크섬의 출처) </summary>
    public required string FeedUrl { get; init; }

    /// <summary> full nupkg 크기(byte). 진행률을 MB로 환산할 때 쓴다 </summary>
    public required long PackageSize { get; init; }

    /// <summary>
    /// 이 릴리스의 delta nupkg 파일명. delta가 없는 릴리스(첫 릴리스, 생성 실패)는 null.
    ///
    /// 이 delta가 어느 버전에서 출발하는지는 파일명으로 알 수 없고 그 릴리스의 피드에만
    /// 적혀 있다. 대개는 직전 릴리스지만 언제나 그런 것은 아니다. 프리릴리스를 만들 때
    /// 최신 정식 버전을 기준으로 잡는 도구가 흔하다
    /// </summary>
    public string? DeltaFileName { get; init; }

    /// <summary> delta nupkg 다운로드 주소. DeltaFileName이 null이면 함께 null </summary>
    public string? DeltaUrl { get; init; }

    /// <summary> delta nupkg 크기(byte). full과 견줘 사슬을 쓸지 정할 때 쓴다 </summary>
    public long DeltaSize { get; init; }
}

/// <summary>
/// 이번 설치에서 실제로 받을 것. Deltas가 비어 있으면 full 하나만 받는다
/// </summary>
/// <param name="TargetFull">대상 버전의 full 패키지</param>
/// <param name="Deltas">적용 순서대로 정렬된 delta. 사슬을 못 쓰면 빈 배열</param>
public sealed record InstallPlan(VelopackAsset TargetFull, VelopackAsset[] Deltas);

