namespace IndexExplorer.ViewModels;

/// <summary>목록 열 제목: 글자, 정렬 방향(0 없음, 1 오름, 2 내림), 필터 적용 여부.</summary>
public sealed record HeaderInfo(string Title, int Sort, bool Filtered)
{
    public override string ToString() => Title;
}
