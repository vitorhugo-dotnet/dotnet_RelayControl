using SonicRelay.MediaRelay;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<MediaRelayRegistry>();
builder.Services.AddHttpClient<RelayControlLeaseClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["MediaRelay:ApiBaseUrl"] ?? "http://api:8080/");
    client.Timeout = TimeSpan.FromSeconds(3);
});
var app = builder.Build();
app.UseWebSockets();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/ws/media", MediaSocketEndpoint.HandleAsync);
app.Run();
