namespace SmartSpare.Web.Client.Resources;

/// <summary>
/// Marker of the shared UI strings (<c>SharedResource.resx</c>, en-US). Every text of the client goes through
/// <c>IStringLocalizer&lt;SharedResource&gt;</c>, so a second culture is a translation task (STAGE_3_PLAN 8.6, #47).
/// </summary>
#pragma warning disable S2094 // A marker type: the localizer finds the resources by its name.
public sealed class SharedResource;
#pragma warning restore S2094
