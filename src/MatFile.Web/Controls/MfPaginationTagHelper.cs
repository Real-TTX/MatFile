using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;

namespace MatFile.Web.Controls;

/// <summary>
/// Seitenwechsel direkt unter der Tabelle:
/// <code>&lt;mf-pagination page="@Model.Result.Page" page-count="@Model.Result.PageCount" total="@Model.Result.TotalCount" /&gt;</code>
/// </summary>
[HtmlTargetElement("mf-pagination", TagStructure = TagStructure.WithoutEndTag)]
public class MfPaginationTagHelper : TagHelper
{
    private const int Window = 2;
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Default;

    private readonly IStringLocalizer<SharedResource> _text;

    public MfPaginationTagHelper(IStringLocalizer<SharedResource> text) => _text = text;

    [HtmlAttributeName("page")]
    public int Page { get; set; } = 1;

    [HtmlAttributeName("page-count")]
    public int PageCount { get; set; } = 1;

    [HtmlAttributeName("total")]
    public int Total { get; set; }

    /// <summary>Präfix der Seitenparameter, damit mehrere Listen auf einer Seite möglich sind.</summary>
    [HtmlAttributeName("param-prefix")]
    public string? ParamPrefix { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "nav";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "mf-pagination");
        output.Attributes.SetAttribute("aria-label", _text["Pagination.Label"].Value);

        var html = new StringBuilder();
        html.Append($"<span class=\"mf-pagination-info\">{Encoder.Encode(_text["Pagination.Summary", Page, Math.Max(1, PageCount), Total].Value)}</span>");

        if (PageCount > 1)
        {
            html.Append("<span class=\"mf-pagination-pages\">");
            html.Append(Link(Page - 1, "‹", Page > 1, "mf-pagination-step"));

            var first = Math.Max(1, Page - Window);
            var last = Math.Min(PageCount, Page + Window);

            if (first > 1)
            {
                html.Append(Link(1, "1", true));
                if (first > 2)
                {
                    html.Append("<span class=\"mf-pagination-gap\">…</span>");
                }
            }

            for (var i = first; i <= last; i++)
            {
                html.Append(Link(i, i.ToString(), true, i == Page ? "is-active" : null));
            }

            if (last < PageCount)
            {
                if (last < PageCount - 1)
                {
                    html.Append("<span class=\"mf-pagination-gap\">…</span>");
                }
                html.Append(Link(PageCount, PageCount.ToString(), true));
            }

            html.Append(Link(Page + 1, "›", Page < PageCount, "mf-pagination-step"));
            html.Append("</span>");
        }

        output.Content.SetHtmlContent(html.ToString());
    }

    private string Link(int page, string label, bool enabled, string? cssClass = null)
    {
        var text = Encoder.Encode(label);
        var css = string.IsNullOrEmpty(cssClass) ? string.Empty : " " + cssClass;

        if (!enabled)
        {
            return $"<span class=\"mf-pagination-link is-disabled{css}\">{text}</span>";
        }

        return $"<a class=\"mf-pagination-link{css}\" href=\"{Encoder.Encode(BuildUrl(page))}\">{text}</a>";
    }

    private string BuildUrl(int page)
    {
        var request = ViewContext.HttpContext.Request;
        var prefix = string.IsNullOrEmpty(ParamPrefix) ? string.Empty : ParamPrefix;

        var values = new Dictionary<string, string?>();
        foreach (var pair in QueryHelpers.ParseQuery(request.QueryString.Value))
        {
            values[pair.Key] = pair.Value.ToString();
        }

        values[prefix + "page"] = page.ToString();
        return QueryHelpers.AddQueryString(request.Path, values);
    }
}
