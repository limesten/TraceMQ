using Microsoft.Extensions.FileProviders;
using System.Reflection;
using TraceMQ.Api.Ingest;
using TraceMQ.Api.Model;
using TraceMQ.Api.Storage;
using TraceMQ.Api.Endpoints;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

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
builder.Services.AddScoped(_ =>
{
    var connection = new SqliteConnection("Data Source=tracemq.db");
    connection.Open();
    return connection;
});

var app = builder.Build();

var files = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot");
var opts = new StaticFileOptions { FileProvider = files };

app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(opts);
app.MapFallbackToFile("index.html", opts);
app.MapMessageEndpoints();

app.Run();
