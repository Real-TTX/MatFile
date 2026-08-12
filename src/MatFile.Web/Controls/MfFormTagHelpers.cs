using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.TagHelpers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatFile.Web.Controls;

/// <summary>
/// Werkzeugleiste über einer Liste (Suchtext, Filter, Sortierung).
/// <code>&lt;mf-toolbar&gt;...&lt;/mf-toolbar&gt;</code>
/// </summary>
[HtmlTargetElement("mf-toolbar")]
public class MfToolbarTagHelper : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.AddClass("mf-toolbar", HtmlEncoder.Default);
    }
}

/// <summary>
/// Aktionsleiste. Buttons stehen in einer Zeile von positiv nach negativ;
/// alles nach <c>&lt;mf-actions-gap /&gt;</c> wird rechts abgesetzt (typisch: Löschen).
/// </summary>
[HtmlTargetElement("mf-actions")]
public class MfActionsTagHelper : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.AddClass("mf-actions", HtmlEncoder.Default);
    }
}

[HtmlTargetElement("mf-actions-gap", TagStructure = TagStructure.WithoutEndTag)]
public class MfActionsGapTagHelper : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "mf-actions-gap");
    }
}

/// <summary>
/// Formularfeld mit Beschriftung, Hinweis und Validierungsmeldung.
/// Das Eingabeelement kommt als Inhalt, damit jeder Feldtyp möglich ist:
/// <code>
/// &lt;mf-field label="Benutzername" for="Input.Username"&gt;
///     &lt;input asp-for="Input.Username" /&gt;
/// &lt;/mf-field&gt;
/// </code>
/// </summary>
[HtmlTargetElement("mf-field")]
public class MfFieldTagHelper : TagHelper
{
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Default;

    [HtmlAttributeName("label")]
    public string? Label { get; set; }

    [HtmlAttributeName("for")]
    public ModelExpression? For { get; set; }

    [HtmlAttributeName("hint")]
    public string? Hint { get; set; }

    /// <summary>Feld über die volle Breite eines zweispaltigen Formulars.</summary>
    [HtmlAttributeName("wide")]
    public bool Wide { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.AddClass("mf-field", Encoder);
        if (Wide)
        {
            output.AddClass("mf-field-wide", Encoder);
        }

        var fieldName = For is null
            ? null
            : ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name);

        if (!string.IsNullOrEmpty(Label))
        {
            var forAttribute = string.IsNullOrEmpty(fieldName)
                ? string.Empty
                : $" for=\"{Encoder.Encode(TagBuilder.CreateSanitizedId(fieldName, "_"))}\"";

            output.PreContent.SetHtmlContent($"<label{forAttribute}>{Encoder.Encode(Label)}</label>");
        }

        var extra = string.Empty;

        if (!string.IsNullOrEmpty(Hint))
        {
            extra += $"<span class=\"mf-field-hint\">{Encoder.Encode(Hint)}</span>";
        }

        if (!string.IsNullOrEmpty(fieldName)
            && ViewContext.ModelState.TryGetValue(fieldName, out var entry)
            && entry.Errors.Count > 0)
        {
            extra += $"<span class=\"mf-field-error\">{Encoder.Encode(entry.Errors[0].ErrorMessage)}</span>";
        }

        if (extra.Length > 0)
        {
            output.PostContent.SetHtmlContent(extra);
        }
    }
}
