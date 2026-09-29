namespace Siakun.AutoUpdate;

/// <summary>
/// 호출 앱이 소유하는 업데이트 설정입니다.
/// 구버전으로 전환한 뒤에도 선택을 유지하려면 setter에서 저장까지 마쳐야 합니다.
/// </summary>
// INTENT: 저장 위치와 파일 형식은 앱이 결정하되, 구버전 선택 시 자동 업데이트를 끄는 규칙은
// 라이브러리에서 지킨다. UI마다 이 규칙을 복제하면 종료나 다음 실행에서 선택이 사라질 수 있다.
public interface IUpdateSettings
{
    bool AutoUpdateEnabled { get; set; }
    bool PrereleaseEnabled { get; }
}
