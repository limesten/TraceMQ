using Microsoft.Extensions.FileProviders;
using System.Reflection;
using TraceMQ.Api.Correlation;
using TraceMQ.Api.Ingest;
using TraceMQ.Api.Model;
using TraceMQ.Api.Storage;
using TraceMQ.Api.Endpoints;
using System.Threading.Channels;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Without this a service's working directory is C:\Windows\System32, so config loading
// fails in a way that looks like nothing happening at all. No-op off Windows.
builder.Host.UseWindowsService();

builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.AddSingleton<SqliteConnectionFactory>();

var dropped = new DroppedCounter();
builder.Services.AddSingleton(dropped);

// Bounded, drop-oldest: the logger must never become the reason the broker backs up.
// TryWrite returns true on an eviction, so the count has to come from this callback.
builder.Services.AddSingleton(_ => Channel.CreateBounded<LogMessage>(
    new BoundedChannelOptions(100_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        // MQTTnet does not guarantee a single dispatch thread under all option combinations.
        SingleWriter = false,
    },
    itemDropped: _ => dropped.Increment()
));

builder.Services.AddSingleton(_ => new MessageRing(100_000));

builder.Services.AddSingleton(sp => CorrelationPaths.Load(
    sp.GetRequiredService<SqliteConnectionFactory>(),
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Correlation")));

builder.Services.AddHostedService<MqttIngestService>();
builder.Services.AddHostedService<WriterService>();

var app = builder.Build();

// The database has to exist and be at the current schema before any hosted service runs.
var factory = app.Services.GetRequiredService<SqliteConnectionFactory>();
var schemaLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Schema");
Schema.Initialize(factory, schemaLog);

// Rule 5: ids come from ingest. Continue from what is already on disk, or a restart hands
// out ids the database already holds and the live-to-history handoff breaks.
app.Services.GetRequiredService<MessageRing>().SeedFrom(Schema.LastMessageId(factory));

// Serilog writes beside the database, which is a directory a service account can write to.
var logDirectory = Path.Combine(Path.GetDirectoryName(factory.DbPath)!, "logs");
Directory.CreateDirectory(logDirectory);
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.File(
        Path.Combine(logDirectory, "tracemq-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .CreateLogger();

var files = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot");
var opts = new StaticFileOptions { FileProvider = files };

app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(opts);
app.MapFallbackToFile("index.html", opts);
app.MapMessageEndpoints();

app.Run();
