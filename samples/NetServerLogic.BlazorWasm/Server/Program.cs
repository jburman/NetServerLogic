using NetServerLogic.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPowerPagesServerLogicEmulator(
    builder.Configuration.GetSection("PowerPagesServerLogic"));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    app.MapPowerPagesServerLogic();
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();
