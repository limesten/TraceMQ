# TraceMQ — Architecture

MQTT message logger and sequence tracer for CodeIT Enterprise services.

Services on a line communicate over MQTT in a chain: a trigger in service A causes a
publish, which triggers service B, and so on. Troubleshooting means following one
sequence of messages across many topics. Every message in a marking sequence carries
the same correlation key, from the first message to the last.

---

## 1. Shape

One process. One executable. Not microservices — this is a troubleshooting tool that
must be easy to drop onto a customer machine.

```
MQTT broker ──subscribe(codeit/#)──▶ ┌──────────────────────────────┐
                                     │  tracemq.exe                 │
                                     │                              │
                                     │  MqttIngestService           │
                                     │        │ Channel (bounded)   │
                                     │        ▼                     │
                                     │  WriterService ──▶ SQLite    │
                                     │        │                     │
                                     │  ring buffer (in memory)     │
                                     │        │                     │
                                     │  ASP.NET Core — API + SPA    │
                                     └──────────────┬───────────────┘
                                                    ▼
                                              browser (React)
```

Four components inside the one host:

| Component           | Type                | Job                                                                                                                                     |
| ------------------- | ------------------- | --------------------------------------------------------------------------------------------------------------------------------------- |
| `MqttIngestService` | `BackgroundService` | One MQTT client. Subscribes to configured wildcards. Extracts the correlation key. Pushes to a bounded channel. Never touches the database. |
| `WriterService`     | `BackgroundService` | Owns the single SQLite writer connection. Drains the channel and batch-inserts.                                                         |
| `RetentionService`  | `BackgroundService` | Deletes rows past the retention window on a timer.                                                                                      |
| `WebApplication`    | ASP.NET Core        | Read-only API plus the embedded React app.                                                                                              |

The live pane reads the **in-memory ring buffer** (last ~100k messages). SQLite is only
touched for history and for traces. Because it is one process, that is a field access,
not a query.

---

## 2. Tech stack

### Backend

|              | Choice                                         | Why                                                                                              |
| ------------ | ---------------------------------------------- | ------------------------------------------------------------------------------------------------ |
| Runtime      | **.NET 10** (LTS)                              | Supported into 2028. .NET 8 and 9 both reach End of Support on 10 Nov 2026 — do not start there. |
| Web          | **ASP.NET Core Minimal APIs**                  | Four endpoints. No MVC.                                                                          |
| MQTT         | **MQTTnet**                                    | MQTT 5, mature, first-class on Windows.                                                          |
| Store        | **SQLite** via `Microsoft.Data.Sqlite`         | One file, zero operations, no install at the customer.                                           |
| SQL          | **Dapper**                                     | Raw SQL. No EF Core — this is a query app.                                                       |
| Queue        | `System.Threading.Channels`                    | Built in. Back-pressure between ingest and writer.                                               |
| Service host | `Microsoft.Extensions.Hosting.WindowsServices` | `sc create` support and correct content root.                                                    |
| Static files | `Microsoft.Extensions.FileProviders.Embedded`  | React app inside the exe.                                                                        |
| Logging      | **Serilog** + `Serilog.Sinks.File`             | Rolling file log on the customer box. You will need it.                                          |

No EF Core, no AutoMapper, no MediatR.

### Frontend

|           | Choice                    | Why                                                   |
| --------- | ------------------------- | ----------------------------------------------------- |
| Framework | **React + TypeScript**    |                                                       |
| Build     | **Vite**                  |                                                       |
| Table     | **TanStack Virtual**      | Non-negotiable — 100k rows will not render otherwise. |
| Data      | **TanStack Query**        | Polling, caching, retries.                            |
| State     | **Zustand**               | Filters and selection. Nothing more.                  |
| Styling   | **Tailwind CSS**          |                                                       |
| Dates     | **`Intl.DateTimeFormat`** | Built into the browser. Skip date-fns.                |

---

## 3. Data model

```sql
CREATE TABLE messages (
  id              INTEGER PRIMARY KEY,   -- assigned by ingest, not by SQLite (rule 5)
  ts              INTEGER NOT NULL,      -- unix millis
  topic           TEXT    NOT NULL,
  correlation_key TEXT COLLATE NOCASE,
  qos             INTEGER,
  retained        INTEGER,
  payload         BLOB
);

CREATE INDEX ix_messages_corr  ON messages(correlation_key) WHERE correlation_key IS NOT NULL;
CREATE INDEX ix_messages_ts    ON messages(ts);
CREATE INDEX ix_messages_topic ON messages(topic, ts);

CREATE TABLE settings (      -- holds correlation_paths, as a JSON array
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
```

The partial index on `correlation_key` skips the noise topics (`plc/conveyor/state` and
similar) that carry no key.

`correlation_key` is extracted at ingest, from the payload, using the first of the
configured `correlation_paths` that yields a value — `trigger.uid` in the current
deployment. Extraction is a `Utf8JsonReader` that walks only the path segments and bails
on the first non-match, so a malformed payload costs nothing and throws nothing. Search is
exact equality; `COLLATE NOCASE` keeps the index usable for it and stops a service that
emits uppercase GUIDs from silently missing a lowercase search.

There is no `service` column. Topic segment 1 already carries it.

---

## 4. The five SQLite rules

These decide whether the tool works or falls over. All five matter.

**1. WAL mode, set once at startup.** Without it a reader blocks the writer and the UI
stalls the logger.

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous  = NORMAL;   -- safe with WAL, far faster than FULL
PRAGMA busy_timeout = 5000;
```

**2. Exactly one writer connection**, owned by `WriterService`. Never write from a
request thread. One writer means `SQLITE_BUSY` on writes cannot happen. Readers open
their own short-lived connections.

**3. Batch inserts inside a transaction.** This is the difference between hundreds of
rows per second and tens of thousands. Commit every 500 rows or 100 ms, whichever comes
first, using a prepared command that is reused.

**4. Never do database work in the MQTT handler.** MQTTnet calls you on its receive
loop. Block there and you back up the broker. Use a bounded channel that drops the
oldest message when full, and count the drops:

```csharp
Channel.CreateBounded<LogMessage>(
    new BoundedChannelOptions(100_000) {
        FullMode     = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false        // MQTTnet does not guarantee one dispatch thread
    },
    itemDropped: _ => Interlocked.Increment(ref _dropped));
```

The counter has to come from the `itemDropped` callback. `TryWrite` returns true on a
`DropOldest` eviction, so a writer that only checks its return value reports no drops
however many it causes.

The dropped counter is shown in the status bar. It is the honest signal that the tool
is losing data.

**5. Ids are assigned by ingest, not by SQLite.** The live pane reads the ring buffer and
history reads SQLite. If the rowid were assigned at insert, those two would number the
same messages differently and the handoff between them would break — the UI could not ask
for "everything after the last row I showed you" across the boundary. Ingest takes the
next id from an `Interlocked.Increment` seeded from `MAX(id)` at startup, and the INSERT
writes it explicitly.

---

## 5. API

```
GET  /api/messages?afterId&beforeId&topic&correlation&from&to&limit
                                    table rows, newest first, without payloads
GET  /api/messages/{id}             one message, with its payload
GET  /api/correlations/recent       keys seen recently on the wire, from the ring buffer
GET  /api/settings                  { correlationPaths: [...] }
PUT  /api/settings                  rewrites correlation_key across the table
GET  /api/status                    broker state, msgs/sec, dropped, row count, db size
```

Server-side filtering, batched frames. Never push 3000 msg/s at a browser. That includes
the topic filter: it is a display filter and never changes the broker subscription, but it
runs in SQL, not in the browser.

Three rules the shape depends on:

- **Payloads never ride on the list endpoint.** The UI shows one payload at a time, in a
  pane, on click. A list row carries the payload's size, not its bytes.
- **Page by `id`, never by `ts`.** `afterId` fetches newer rows for the live tail,
  `beforeId` older ones for scrolling back.
- **Payloads come back as `{ text, encoding }`** — UTF-8 when it decodes, base64 when it
  does not.

`PUT /api/settings` is the only write in the API. It runs on the writer connection and
rewrites the whole column:

```sql
UPDATE messages
   SET correlation_key = COALESCE(json_extract(CAST(payload AS TEXT), @p0),
                                  json_extract(CAST(payload AS TEXT), @p1))
 WHERE json_valid(CAST(payload AS TEXT));
```

Expected to run once per site, if ever. It earns its place anyway: without it, a typo in a
path leaves every message logged so far unsearchable.

## 6. Build and deployment

### Publish

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <SelfContained>true</SelfContained>
  <PublishSingleFile>true</PublishSingleFile>
  <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
  <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  <InvariantGlobalization>true</InvariantGlobalization>
  <GenerateEmbeddedFilesManifest>true</GenerateEmbeddedFilesManifest>
</PropertyGroup>

<ItemGroup>
  <EmbeddedResource Include="wwwroot\**\*" />
</ItemGroup>

<Target Name="BuildFrontend" BeforeTargets="BeforeBuild" Condition="'$(Configuration)'=='Release'">
  <Exec Command="npm ci"        WorkingDirectory="../../web" />
  <Exec Command="npm run build" WorkingDirectory="../../web" />
</Target>
```

```bash
dotnet publish src/TraceMq.Api -c Release
# → tracemq.exe, ~45 MB, frontend inside
```

**Do not use NativeAOT.** It gives a smaller binary but the ecosystem support is not
there for this shape of app, and self-contained single-file already gives one file.
Leave `PublishTrimmed` off too — 45 MB is not a problem worth solving.

If the customer server already has the ASP.NET Core runtime, publish framework-dependent
instead and the exe drops to about 5 MB.

### Serving the SPA

```csharp
var files = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot");
var opts  = new StaticFileOptions { FileProvider = files };

app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(opts);
app.MapFallbackToFile("index.html", opts);   // SPA routing
```

### Windows service

```csharp
builder.Host.UseWindowsService();
```

```
sc create TraceMQ binPath= "C:\tracemq\tracemq.exe" start= auto
```

### What ships

```
tracemq.exe            ~45 MB   (frontend inside)
appsettings.json       broker, port, retention window
```

Database at `C:\ProgramData\CodeIT\tracemq\data.db`.

---

## 7. Gotchas — the list that costs a day each

|                                            |                                                                                                                                                                 |
| ------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **`IncludeNativeLibrariesForSelfExtract`** | `Microsoft.Data.Sqlite` ships a native `e_sqlite3` DLL. Without this flag the one-file publish quietly becomes two files.                                       |
| **`UseWindowsService()`**                  | Without it a service's working directory is `C:\Windows\System32`, so config loading fails in a way that looks like nothing happening at all.                   |
| **Database in `%ProgramData%`**            | `Program Files` is not writable by a service account. That failure appears at the customer site, not on your machine.                                           |
| **Do not `VACUUM`**                        | A retention delete does not shrink the file, but the workload reaches a steady size and reuses freed pages. `VACUUM` takes an exclusive lock and stalls ingest. |
| **Frontend build order**                   | `dotnet build` does not run npm. The MSBuild target above handles Release; CI must build the frontend first.                                                    |
| **QoS 0 for the subscription**             | The logger must never become a slow consumer that makes the broker queue for it.                                                                                |
| **`ORDER BY ts` under a live tail**        | Millisecond timestamps collide constantly at 1500 msg/s, so the same row comes back twice, or never, as new rows land. Page by `id`.                            |
| **`GetString` on the payload column**      | `payload` is a BLOB. Reading it as a string throws or mangles on the first non-UTF-8 payload. Decode explicitly, fall back to base64.                           |
| **JSON functions on a BLOB**               | From SQLite 3.45 `json_extract` and `json_valid` read a BLOB argument as JSONB, not as JSON text. Without `CAST(payload AS TEXT)` the backfill writes nulls.    |

---

## 8. Dev loop

- `dotnet watch` on the API (port 5000)
- `npm run dev` with Vite proxying `/api` to `localhost:5000`
- A recorded set of real messages replayed into a local Mosquitto for test data

Two moving parts in development, one artifact in production.
