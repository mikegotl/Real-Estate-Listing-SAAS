using System.Text;
using Microsoft.AspNetCore.Components.Rendering;
using MudBlazor;

namespace ListingStudio.Web.Components.Layout;

/// <summary>
/// Writes the MudBlazor dark palette as CSS variables that only apply when dark mode is on, so the static
/// server-rendered shell can switch themes without an interactive theme provider. Dark mode is on when the
/// html element carries data-theme="dark", or when the device prefers dark and the visitor has not chosen light.
/// </summary>
public sealed class DarkThemeVariables : MudThemeProvider
{
    public const string DarkScope = ":root[data-theme=\"dark\"]";
    public const string SystemDarkScope = ":root:not([data-theme=\"light\"])";

    protected override void BuildRenderTree(RenderTreeBuilder __builder)
    {
        var variables = new StringBuilder();
        GenerateTheme(variables);

        var css = new StringBuilder();
        css.Append(DarkScope).Append('{').Append(variables).AppendLine("}");
        css.Append("@media (prefers-color-scheme: dark){")
            .Append(SystemDarkScope).Append('{').Append(variables).AppendLine("}}");

        __builder.OpenElement(0, "style");
        __builder.AddAttribute(1, "class", "ls-dark-theme");
        __builder.AddMarkupContent(2, css.ToString());
        __builder.CloseElement();
    }
}
