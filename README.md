# TraceMQ

MQTT message logger and sequence tracer for CodeIT Enterprise services.

It subscribes to the broker, stores every message, and lets you follow one marking
sequence across all topics in a browser.

## Features

- Logs every message under `codeit/#` (configurable).
- Live table of incoming messages.
- Click a row to see its payload.
- Follow a sequence by its correlation key (`trigger.uid` by default).
- Correlation paths can be changed in the UI. Existing messages are re-indexed.
- Filter by topic, date, date range or time window.
- Delta column shows the time between messages, so stops and gaps stand out.
- Old messages are deleted automatically after 7 days.
- One exe, with the web UI built in. No install, no external database.

## Running it

1. Copy `TraceMQ.Api.exe` and `appsettings.json` to a folder on the machine.
2. Set the broker in `appsettings.json` (`Mqtt:Host`, `Mqtt:Port`).
3. Start the exe and open <http://localhost:5027>.

To run it as a Windows service:

```bash
sc create TraceMQ binPath= "C:\tracemq\TraceMQ.Api.exe" start= auto
```

## Storage

Messages are stored in a local SQLite database, one file:

```
C:\ProgramData\CodeIT\tracemq\data.db
```

Log files are written next to it, in `C:\ProgramData\CodeIT\tracemq\logs\`.

Set `Storage:DbPath` in `appsettings.json` to store it somewhere else.

## Capacity

Tested on a laptop with a local Mosquitto broker:

| Payload | Messages per second, no loss |
| ------- | ---------------------------- |
| ~400 B  | ~20 000                      |
| 4 KB    | ~5 000                       |

The limit was the broker, not TraceMQ.

## Good to know

- **Port.** The UI is only reachable from the same machine. Set `Urls` to
  `http://0.0.0.0:5027` to open it from other machines.
- **One client id per broker.** Two TraceMQ instances with the same `Mqtt:ClientId` on
  one broker disconnect each other. Give a second instance its own id.
- **It only listens.** It subscribes with QoS 0 and never publishes, so it does not
  affect the services on the line.
- **Dropped counter.** The header shows how many messages were lost because TraceMQ could
  not keep up. It should stay at 0.
- **Disk size.** The database grows with traffic and payload size, and levels off once
  retention starts deleting. It does not shrink on disk. Delete the file (with TraceMQ
  stopped) to start fresh.
- **Settings** can also be given as environment variables, for example
  `Mqtt__Host=10.0.0.5`.

## Development

Needs .NET 10 SDK and Node.js.

| Command        | What it does                                         |
| -------------- | ---------------------------------------------------- |
| `make run`     | API and UI at <http://localhost:5027>                |
| `make web`     | Vite dev server at <http://localhost:5173>           |
| `make check`   | Build, lint and test everything                      |
| `make sim`     | Play a simulated assembly line into the local broker |
| `make demo`    | Fresh instance on port 5028, filled by the simulator |
| `make publish-win` | Build the Windows exe into `dist/win-x64`        |

See [ARCHITECTURE.md](ARCHITECTURE.md) for how it works.
