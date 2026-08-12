namespace MatFile.Web.Controls;

/// <summary>Spalte einer Tabelle. <paramref name="SortKey"/> macht die Spalte sortierbar.</summary>
public record TableColumn(string Title, string? SortKey = null, string? CssClass = null, string? Width = null);

/// <summary>Eintrag einer Tab-Leiste.</summary>
public record TabItem(string Title, string Url, bool IsActive = false, string? Icon = null, int? Badge = null);

/// <summary>Ergebnis einer seitenweisen Abfrage.</summary>
public class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public int TotalCount { get; init; }

    public int PageCount => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < PageCount;
}

/// <summary>Gemeinsame Parameter für Listenseiten (Suche, Sortierung, Seitenwechsel).</summary>
public class ListQuery
{
    public const int DefaultPageSize = 25;
    public static readonly int[] PageSizes = [10, 25, 50, 100];

    public string? Search { get; set; }
    public string? Sort { get; set; }
    public string? Dir { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = DefaultPageSize;

    public bool Descending => string.Equals(Dir, "desc", StringComparison.OrdinalIgnoreCase);

    public int NormalizedPage => Page < 1 ? 1 : Page;

    public int NormalizedPageSize => PageSizes.Contains(PageSize) ? PageSize : DefaultPageSize;

    public int Skip => (NormalizedPage - 1) * NormalizedPageSize;
}
