var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "Hello Worldz!");
app.MapGet("/api/ping", () => new { message = "pong", at = DateTimeOffset.Now });

app.Run();
