using Saem.Mcp;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSaemMcp(builder.Configuration);

var app = builder.Build();
app.MapSaemMcp();
app.Run();

/// <summary>The host's entry point, named so a test can start the same host.</summary>
public partial class Program;
