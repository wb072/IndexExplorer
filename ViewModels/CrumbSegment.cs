namespace IndexExplorer.ViewModels;

/// <summary>주소 표시줄의 한 칸 ("색인 › A 프로젝트 › 견적").</summary>
public sealed class CrumbSegment
{
    public required string Text { get; init; }
    public bool IsFirst { get; init; }
    public object? Target { get; init; }
}
