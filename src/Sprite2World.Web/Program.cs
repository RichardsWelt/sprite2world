using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Sprite2World.Application;
using Sprite2World.Infrastructure;
using Sprite2World.Web.Components;
using Sprite2World.Web.Services;

if (args.Contains("--healthcheck", StringComparer.Ordinal))
{
    try { using var client = new HttpClient(); using var response = await client.GetAsync("http://localhost:8080/health"); Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1; }
    catch (HttpRequestException) { Environment.ExitCode = 1; }
    return;
}

var builder = WebApplication.CreateBuilder(args);
var configuredDataPath = builder.Configuration["SPRITE2WORLD_DATA_PATH"] ?? "/app/data";
var keyPath = Path.Combine(configuredDataPath, ".data-protection-keys");
Directory.CreateDirectory(keyPath);
builder.Services.AddDataProtection()
    .SetApplicationName("Sprite2World")
    .PersistKeysToFileSystem(new DirectoryInfo(keyPath));
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHealthChecks();
builder.Services.AddHttpClient<WorkerClient>(client =>
{
    client.BaseAddress = new(builder.Configuration["Worker:BaseUrl"] ?? "http://sprite2world-worker:8080/");
    client.Timeout = TimeSpan.FromMinutes(3);
});
builder.Services.Configure<OpenAiOptions>(options =>
{
    options.ApiKey = builder.Configuration["OPENAI_API_KEY"];
    options.DefaultModel = builder.Configuration["OPENAI_DEFAULT_MODEL"] ?? OpenAiModelCatalog.DefaultModelId;
    options.ReasoningEffort = builder.Configuration["OPENAI_REASONING_EFFORT"] ?? "medium";
});
builder.Services.Configure<StorageOptions>(options => options.DataPath = configuredDataPath);
builder.Services.AddHttpClient("OpenAiCredentialValidation", client => { client.BaseAddress = new("https://api.openai.com/v1/"); client.Timeout = TimeSpan.FromSeconds(20); });
builder.Services.AddScoped<OpenAiCredentialState>();
builder.Services.AddScoped<IOpenAiApiKeyProvider>(sp => sp.GetRequiredService<OpenAiCredentialState>());
builder.Services.AddHttpClient<OpenAiDesignService>(client => { client.BaseAddress = new("https://api.openai.com/v1/"); client.Timeout = TimeSpan.FromMinutes(3); });
builder.Services.AddScoped<IBlueprintService>(sp => sp.GetRequiredService<OpenAiDesignService>());
builder.Services.AddScoped<IAssetClassificationService>(sp => sp.GetRequiredService<OpenAiDesignService>());
builder.Services.AddScoped<ISpriteGenerationService>(sp => sp.GetRequiredService<OpenAiDesignService>());
builder.Services.AddSingleton<IWorldExporter, JsonWorldExporter>();
builder.Services.AddScoped<UiLocalizer>();
builder.Services.AddScoped<EditorState>();
builder.Services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(options => options.DetailedErrors = false);
builder.Services.AddServerSideBlazor().AddHubOptions(options => options.MaximumReceiveMessageSize = 12 * 1024 * 1024);

var app = builder.Build();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseAntiforgery();
var dataPath = configuredDataPath;
Directory.CreateDirectory(dataPath);
app.UseStaticFiles();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(dataPath),
    RequestPath = "/data",
    OnPrepareResponse = context => { context.Context.Response.Headers.CacheControl = "public,max-age=31536000,immutable"; context.Context.Response.Headers["X-Content-Type-Options"] = "nosniff"; }
});
app.MapHealthChecks("/health");
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
