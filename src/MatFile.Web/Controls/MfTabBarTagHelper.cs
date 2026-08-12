using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatFile.Web.Controls;

/// <summary>
/// Tab-Leiste: <code>&lt;mf-tabbar id="admin" items="Model.Tabs" /&gt;</code>
/// Die Id erlaubt mehrere Leisten auf einer Seite.
/// </summary>
[HtmlTargetElement("mf-tabbar", TagStructure = TagStructure.WithoutEndTag)]
public class MfTabBarTagHelper : TagHelper
{
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Default;

    [HtmlAttributeName("id")]
    public string Id { get; set; } = "tabs";

    [HtmlAttributeName("items")]
    public IReadOnlyList<TabItem> Items { get; set; } = [];

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "nav";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "mf-tabbar");
        output.Attributes.SetAttribute("id", Id);
        output.Attributes.SetAttribute("role", "tablist");

        var html = new StringBuilder();

        foreach (var item in Items)
        {
            var active = item.IsActive ? " is-active" : string.Empty;
            html.Append($"<a class=\"mf-tab{active}\" role=\"tab\" aria-selected=\"{(item.IsActive ? "true" : "false")}\" href=\"{Encoder.Encode(item.Url)}\">");

            if (MfIconTagHelper.Svg(item.Icon) is { } icon)
            {
                html.Append(icon);
            }

            html.Append($"<span class=\"mf-tab-title\">{Encoder.Encode(item.Title)}</span>");

            if (item.Badge is { } badge)
            {
                html.Append($"<span class=\"mf-tab-badge\">{badge}</span>");
            }

            html.Append("</a>");
        }

        output.Content.SetHtmlContent(html.ToString());
    }
}
