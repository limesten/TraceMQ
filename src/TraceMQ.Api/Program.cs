using Microsoft.Extensions.FileProviders;
using System.Reflection;
using TraceMQ.Api.Ingest;
using TraceMQ.Api.Model;
using TraceMQ.Api.Storage;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.AddSingleton(_ => Channel.CreateBounded<LogMessage>(
    new BoundedChannelOptions(100_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
    }
));
builder.Services.AddHostedService<MqttIngestService>();
builder.Services.AddHostedService<WriterService>();

var app = builder.Build();

app.MapGet("/api/ping", () => new { message = "pong", at = DateTimeOffset.Now });

var files = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot");
var opts = new StaticFileOptions { FileProvider = files };

app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(opts);
app.MapFallbackToFile("index.html", opts);

app.Run();
