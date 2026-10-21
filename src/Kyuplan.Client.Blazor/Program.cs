using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Kyuplan.Client;
using Kyuplan.Client.Blazor;
using Kyuplan.Client.Localization;
using Kyuplan.Client.Storage;
using Kyuplan.Client.ViewModels;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// wwwroot/appsettings.json is public: anything in it, including an API key, is visible to every visitor.
builder.Services.AddPlannerClient(options =>
{
    // Without an address the page talks to the server it came from (the API serves the client); an address is for a client hosted elsewhere.
    var address = builder.Configuration["PlannerApi:BaseAddress"];
    options.BaseAddress = string.IsNullOrWhiteSpace(address) ? builder.HostEnvironment.BaseAddress : address;
    options.ApiKey = builder.Configuration["PlannerApi:ApiKey"];
});

// The page asks for the login itself (login screen) and keeps it in memory only; the browser's own login dialog stays out of it.
builder.Services.AddTransient<BrowserNoCredentialsHandler>();
builder.Services.ConfigureHttpClientDefaults(http => http.AddHttpMessageHandler<BrowserNoCredentialsHandler>());
builder.Services.AddSingleton<ILoginStore, SessionOnlyLoginStore>();
builder.Services.AddSingleton<LoginViewModel>(); // the login screen is replaced while a sign-in runs; a rejected login must find its error again

// The filter of the statistics page comes back after a reload, like the theme and the language.
builder.Services.AddSingleton<IFilterSettingsStore, LocalStorageFilterStore>();

var host = builder.Build();

// js/lang.js started Blazor with the chosen language as its culture; this makes it the app's language
// (and the culture of every thread), so texts, numbers and dates agree.
var language = host.Services.GetRequiredService<LanguageService>();
language.Select(language.Current);

await host.RunAsync();
