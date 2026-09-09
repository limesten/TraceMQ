using Microsoft.Extensions.FileProviders;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/api/ping", () => new { message = "pong", at = DateTimeOffset.Now });

var files = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot");
var opts = new StaticFileOptions { FileProvider = files };

app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(opts);
app.MapFallbackToFile("index.html", opts);

app.Run();
