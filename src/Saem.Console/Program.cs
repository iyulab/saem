using Saem.Console;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddSaemConsole(builder.Configuration);

var app = builder.Build();
app.MapSaemConsole();
app.Run();
