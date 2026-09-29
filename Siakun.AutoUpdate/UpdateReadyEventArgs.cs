using NuGet.Versioning;

namespace Siakun.AutoUpdate;

/// <summary>다운로드를 마치고 정상 종료 후 적용할 수 있는 버전입니다.</summary>
public sealed class UpdateReadyEventArgs(SemanticVersion version) : EventArgs
{
    public SemanticVersion Version { get; } = version;
}
