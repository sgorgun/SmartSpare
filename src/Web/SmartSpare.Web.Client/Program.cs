using System.Globalization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SmartSpare.Web.Client;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// en-US only in stage 3; every text goes through IStringLocalizer, so another culture is a translation task (#47).
var culture = CultureInfo.GetCultureInfo("en-US");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

builder.Services.AddSmartSpareClient();

// The principal the host serialized with the prerendered page (STAGE_3_PLAN 1.4). It is fixed for the lifetime of the
// app, so sign-in and sign-out reload the page.
builder.Services.AddAuthenticationStateDeserialization();

await builder.Build().RunAsync().ConfigureAwait(false);
