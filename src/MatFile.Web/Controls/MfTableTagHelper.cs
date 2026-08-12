using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.AspNetCore.WebUtilities;

namespace MatFile.Web.Controls;

/// <summary>
/// Tabellen-Control. Kopfzeile inklusive Sortierlinks kommt aus <c>columns</c>,
/// die Zeilen liefert der Inhalt des Tags:
/// <code>
/// &lt;mf-table id="users" columns="Model.Columns" sort="@Model.Query.Sort" dir="@Model.Query.Dir"&gt;
///     &lt;tr&gt;&lt;td&gt;...&lt;/td&gt;&lt;/tr&gt;
/// &lt;/mf-table&gt;
/// </code>
/// Die Id trennt mehrere Tabellen auf derselben Seite (auch für die Sortierparameter).
/// </summary>
[HtmlTargetElement("mf-table")]
public class MfTableTagHelper : TagHelper
{
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Default;

    [HtmlAttributeName("id")]
    public string Id { get; set; } = "table";

    [HtmlAttributeName("columns")]
    public IReadOnlyList<TableColumn> Columns { get; set; } = [];

    [HtmlAttributeName("sort")]
    public string? Sort { get; set; }

    [HtmlAttributeName("dir")]
    public string? Dir { get; set; }

    /// <summary>Präfix der Sortierparameter in der URL. Standard: die Id der Tabelle.</summary>
    [HtmlAttributeName("param-prefix")]
    public string? ParamPrefix { get; set; }

    [HtmlAttributeName("empty-text")]
    public string? EmptyText { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var body = (await output.GetChildContentAsync()).GetContent();
        var isEmpty = string.IsNullOrWhiteSpace(body);

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "mf-table-wrap");

        var head = new StringBuilder();
        head.Append($"<table class=\"mf-table\" id=\"{Encoder.Encode(Id)}\"><thead><tr>");

        foreach (var column in Columns)
        {
            var cssClass = string.IsNullOrEmpty(column.CssClass) ? string.Empty : $" class=\"{Encoder.Encode(column.CssClass)}\"";
            var style = string.IsNullOrEmpty(column.Width) ? string.Empty : $" style=\"width:{Encoder.Encode(column.Width)}\"";
            head.Append($"<th scope=\"col\"{cssClass}{style}>");

            if (string.IsNullOrEmpty(column.SortKey))
            {
                head.Append(Encoder.Encode(column.Title));
            }
            else
            {
                var isActive = string.Equals(Sort, column.SortKey, StringComparison.OrdinalIgnoreCase);
                var ascending = isActive && !string.Equals(Dir, "desc", StringComparison.OrdinalIgnoreCase);
                var arrow = !isActive ? string.Empty : ascending ? " ▲" : " ▼";
                var url = BuildSortUrl(column.SortKey, isActive && ascending ? "desc" : "asc");

                head.Append($"<a class=\"mf-th-sort{(isActive ? " is-active" : string.Empty)}\" href=\"{Encoder.Encode(url)}\">");
                head.Append(Encoder.Encode(column.Title));
                head.Append($"<span class=\"mf-th-arrow\">{arrow}</span></a>");
            }

            head.Append("</th>");
        }

        head.Append("</tr></thead><tbody>");
        output.PreContent.SetHtmlContent(head.ToString());

        if (isEmpty)
        {
            var columnCount = Math.Max(1, Columns.Count);
            var text = Encoder.Encode(EmptyText ?? "-");
            output.Content.SetHtmlContent(
                $"<tr class=\"mf-table-empty\"><td colspan=\"{columnCount}\">{text}</td></tr>");
        }

        output.PostContent.SetHtmlContent("</tbody></table>");
    }

    private string BuildSortUrl(string sortKey, string direction)
    {
        var request = ViewContext.HttpContext.Request;
        var prefix = string.IsNullOrEmpty(ParamPrefix) ? string.Empty : ParamPrefix;

        var values = new Dictionary<string, string?>();
        foreach (var pair in QueryHelpers.ParseQuery(request.QueryString.Value))
        {
            values[pair.Key] = pair.Value.ToString();
        }

        values[prefix + "sort"] = sortKey;
        values[prefix + "dir"] = direction;
        values[prefix + "page"] = "1";

        return QueryHelpers.AddQueryString(request.Path, values);
    }
}
