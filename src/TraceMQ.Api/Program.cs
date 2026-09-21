using Microsoft.Extensions.FileProviders;
using System.Reflection;
using TraceMQ.Api.Correlation;
using TraceMQ.Api.Ingest;
using TraceMQ.Api.Model;
using TraceMQ.Api.Storage;
using TraceMQ.Api.Endpoints;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// Without this a service's working directory is C:\Windows\System32, so config loading
// fails in a way that looks like nothing happening at all. No-op off Windows.
builder.Host.UseWindowsService();

// Logging is established before anything else, so whatever fails below has somewhere to say
// so. Serilog has to be attached to the host here, ahead of Build(): assigning Log.Logger on
// its own leaves the framework's ILogger writing to the console and nowhere else, which under
// a Windows service means no diagnostics at all.
//
// The log file sits beside the database, the one directory a service account is sure to be
// able to write to. That path therefore has to be resolved without the container, which is
// what ResolvePath and EnsureDirectory are public for.
var storageOptions =
    builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
var dbPath = SqliteConnectionFactory.ResolvePath(storageOptions, builder.Environment);
SqliteConnectionFactory.EnsureDirectory(dbPath);

var logDirectory = Path.Combine(Path.GetDirectoryName(dbPath)!, "logs");
Directory.CreateDirectory(logDirectory);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    // Serilog does not read the Logging:LogLevel section, and replacing the provider means
    // that section no longer applies to anything. Restate its one rule here, or every poll
    // from the UI writes a request line and the day's file is noise.
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logDirectory, "tracemq-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    // Last, so a Serilog section in appsettings.json can override the defaults above rather
    // than being overridden by them.
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();

// dispose: true hands the logger's lifetime to the container, so the sink is flushed and
// closed on a clean shutdown.
builder.Services.AddSerilog(Log.Logger, dispose: true);

builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.AddSingleton<SqliteConnectionFactory>();
builder.Services.AddSingleton<MessageQuery>();
builder.Services.AddSingleton<WriterQueue>();

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

builder.Services.AddSingleton<BrokerState>();
builder.Services.AddSingleton(sp =>
{
    var storage = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
    return new MessageRing(storage.RingCapacity, storage.RingBytes);
});
builder.Services.AddSingleton(_ => new RecentKeys(20));

builder.Services.AddSingleton(sp => new CorrelationSettings(CorrelationPaths.Load(
    sp.GetRequiredService<SqliteConnectionFactory>(),
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Correlation"))));
builder.Services.AddSingleton<CorrelationPathUpdater>();

builder.Services.AddHostedService<MqttIngestService>();
builder.Services.AddHostedService<WriterService>();
builder.Services.AddHostedService<RetentionService>();

var app = builder.Build();

// The database has to exist and be at the current schema before any hosted service runs.
var factory = app.Services.GetRequiredService<SqliteConnectionFactory>();
var schemaLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Schema");
Schema.Initialize(factory, schemaLog);

// Rule 5: ids come from ingest. Continue from what is already on disk, or a restart hands
// out ids the database already holds and the live-to-history handoff breaks.
app.Services.GetRequiredService<MessageRing>().SeedFrom(Schema.LastMessageId(factory));
app.Services.GetRequiredService<RecentKeys>().SeedFrom(factory);

var files = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot");
var opts = new StaticFileOptions { FileProvider = files };

app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(opts);
app.MapFallbackToFile("index.html", opts);
app.MapMessageEndpoints();

app.Run();
