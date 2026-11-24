using MudBlazor;

namespace SmartSpare.Web.Client.Layout;

/// <summary>
/// The MudBlazor theme. Every colour used for text or on coloured surfaces has a contrast ratio of at least 4.5:1 against
/// its background (WCAG 2.1 AA): white on the primary (#0D47A1) is 8.6:1, on secondary (#00695C) 6.3:1, on error
/// (#B71C1C) 6.6:1, on success (#1B5E20) 7.9:1, and the warning text (#7A4100) on white 7.4:1.
/// </summary>
public static class SmartSpareTheme
{
    public static MudTheme Theme { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#0D47A1",
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#00695C",
            SecondaryContrastText = "#FFFFFF",
            AppbarBackground = "#0D47A1",
            AppbarText = "#FFFFFF",
            Error = "#B71C1C",
            ErrorContrastText = "#FFFFFF",
            Success = "#1B5E20",
            SuccessContrastText = "#FFFFFF",
            Warning = "#7A4100",
            WarningContrastText = "#FFFFFF",
            Info = "#01579B",
            InfoContrastText = "#FFFFFF",
            TextPrimary = "#1A1A1A",
            TextSecondary = "#4A4A4A",
        },
        Typography = new Typography
        {
            // System fonts: no web-font download, so the CSP needs no font host (STAGE_3_PLAN 8.7).
            Default = new DefaultTypography { FontFamily = ["system-ui", "-apple-system", "Segoe UI", "Roboto", "Helvetica", "Arial", "sans-serif"] },
        },
    };
}
