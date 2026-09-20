# TraceMQ — Plan to finish

Status snapshot and remaining work. Revised 2026-09-20 after a design review pass.
All six open questions from the first draft are now answered; see section 11.

Sections 2 to 10 are the work, as checkboxes: they are the progress state an agent or a
person works through in order. Section 16 says how to drive that in a loop.

---

## 1. Where the project stands

| Piece | State |
| --- | --- |
| `MqttIngestService` | Works — connects, subscribes, pushes to channel. No correlation extraction, no drop counting. |
| `WriterService` | Works — WAL, batching, transaction. Owns the DDL inline. Never fills `sequence_id` / `service`. |
| `RetentionService.cs` | Empty file, not registered. |
| `StorageOptions.cs` | Empty class. DB path hardcoded as `"Data Source=tracemq.db"` in two places. |
| Ring buffer | Does not exist. |
| `/api/messages` | Returns last N, no filtering, full payload on every row. |
| Frontend | Still the Vite starter template. No Tailwind, no TanStack Virtual, no Zustand. |
| Build / publish | Done properly — Makefile, single-file publish, embedded SPA. |
| Load test harness | Done, with a `wal-enabled` baseline to compare against. |

Backend is roughly 40% there. Frontend is at zero.

---

## 2. Phase 0 — Foundations

Backend hygiene. Do this before anything else; every later phase leans on it.

- [x] **`StorageOptions`** → `DbPath`, `RetentionDays`, `BatchSize`, `FlushIntervalMs`.
   Default path `%ProgramData%\CodeIT\tracemq\data.db` on Windows, `./tracemq.db` in
   Development. Bind from config; one connection-string factory, injected everywhere.
- [x] **Move DDL out of `WriterService`** into `Storage/Schema.cs`, run once at startup
   before the hosted services. Use `PRAGMA user_version` for migrations — the schema is
   about to change and dev databases already exist.
- [x] **Reader connections** — apply `busy_timeout=5000` on open. Replace the
   `AddScoped<SqliteConnection>` with an `IDbConnectionFactory` so endpoints open and
   dispose explicitly.
- [x] **Serilog** + rolling file sink next to the database. Non-negotiable for a tool that
   runs as a service at a customer.
- [x] **`builder.Host.UseWindowsService()`** — currently missing, and its absence fails only
   at the customer site.

## 3. Phase 1 — The correlation model

The mockup replaces the architecture doc's fixed `sequence_id` with configurable JSON
paths. Decided shape:

- [x] Rename `sequence_id` → `correlation_key`. The UI says "correlation", the code should too.
- [x] **Several candidate paths, tried in order, first non-null wins.** Stored as a JSON array
  in `settings`, e.g. `["trigger.uid", "header.correlationId"]`. A dotted path is prefixed
  with `$.` before it reaches SQLite; a path already starting with `$.` is passed through.
  Validate the syntax on save.
- [x] **Extract at ingest**, in C#, with a `Utf8JsonReader` that walks only the path segments
  and bails on the first non-match. One forward pass per candidate path, never throws.
  Not `json_extract` in the INSERT: it raises on malformed JSON and would abort the whole
  500-row transaction on one bad payload, and guarding with `json_valid()` parses every
  payload twice. Extraction in ingest also hands the key to the ring buffer, which the
  live pane needs in order to filter without touching the database.
- [x] Cap the candidate list (say 5). Worst case is a payload matching none of them, which
  costs one full scan per path.
- [x] **Column is `TEXT COLLATE NOCASE`**, indexed by the existing partial index. Search is
  exact equality (`WHERE correlation_key = @q`), not prefix — the short GUID in the mockup
  was drawing shorthand. `NOCASE` costs nothing, keeps the index usable, and stops an
  uppercase GUID from one service silently missing a lowercase search. Trim the search
  input server-side; pasted GUIDs pick up trailing whitespace and quotes.
- [x] **Backfill on path change.** One statement on the writer connection: DEFERRED to Phase 3, where the
  PUT endpoint that triggers it lives and can route the write through WriterService.
  ```sql
  UPDATE messages
     SET correlation_key = COALESCE(json_extract(CAST(payload AS TEXT), @p0),
                                    json_extract(CAST(payload AS TEXT), @p1), ...)
   WHERE json_valid(CAST(payload AS TEXT));
  ```
  The `CAST` is mandatory: `payload` is a BLOB, and from SQLite 3.45 the JSON functions
  read a BLOB argument as JSONB rather than as JSON text. Paths are bound as parameters,
  never concatenated.
  The cogwheel is expected to be used rarely or never, so this needs no progress UI — a
  disabled Save button until it returns is enough. It stays in scope because the realistic
  case is someone typoing the path or discovering it a day into logging, and without the
  backfill that day of history is permanently unsearchable.
- [x] One new table, and only one:
  ```sql
  CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
  ```
- [x] Drop `service` from the schema. It is never populated and topic segment 1 gives the same
  thing.

## 4. Phase 2 — Ring buffer and counters

- [x] `MessageRing`: fixed array of ~100k, single writer (ingest), lock-free reads by cursor.
  Holds the correlation key and topic so the live pane can filter in memory.
- [x] **Assign the id in ingest** (`Interlocked.Increment`, seeded from `MAX(id)` at startup)
  and insert it explicitly as the rowid. Otherwise the ring and the database have two
  different id spaces and the live-to-history handoff breaks.
- [x] Ingest pushes to the ring *and* the channel.
- [x] **Recent correlation keys** live here, not in SQL: a bounded ordered dictionary of
  key → lastSeenTs, capped at ~20, updated on each extracted key. Seeded once at startup
  from `SELECT correlation_key, MAX(ts) ... GROUP BY 1 ORDER BY 2 DESC LIMIT 20` so the
  list is not empty after a restart. That query cannot stop early, so it must not run per
  poll.
- [x] Count drops properly — use the `Channel.CreateBounded<T>(options, itemDropped)` overload.
  `TryWrite` never reports a `DropOldest` eviction, so the counter the architecture doc
  asks for cannot be built the current way.
- [x] Set `SingleWriter = false` on the channel. MQTTnet does not guarantee a single dispatch
  thread under all option combinations, and this is a silent-corruption class of bug.

## 5. Phase 3 — API

```
GET  /api/messages?afterId&beforeId&topic&correlation&from&to&limit
       -> rows WITHOUT payload (id, ts, topic, correlationKey, qos, retained, size)
GET  /api/messages/{id}            -> one message WITH payload
GET  /api/correlations/recent      -> [{ key, lastSeenTs }] from the ring
GET  /api/settings                 -> { correlationPaths: [...] }
PUT  /api/settings                 -> { correlationPaths: [...] }, triggers the backfill
GET  /api/status                   -> broker state, msgs/sec, dropped, rowCount, dbBytes, retentionDays
```

Work:

- [x] `GET /api/messages` with `afterId` / `beforeId` / `topic` / `correlation` / `from` / `to` / `limit`.
- [x] `GET /api/messages/{id}` returning the payload as `{ text, encoding }`.
- [x] `GET /api/correlations/recent`, served from the ring buffer.
- [x] `GET` and `PUT /api/settings`, the PUT triggering the backfill.
- [x] `GET /api/status`.

Rules these must hold to (verified by the tests in section 14, not ticked separately):

- **Payload does not belong on the list endpoint.** The mockup shows large JSON payloads
  and a click-to-view pane, so list rows stay small and the detail fetch happens on click.
  This is the difference between a 40 KB poll response and a 4 MB one.
- **Order and page by `id`, never `ts`.** `ts` is millisecond resolution and collides
  constantly at 1500 msg/s; ordering by it duplicates and skips rows during live tail.
  Newest first: `ORDER BY id DESC`. `afterId` returns newer rows for the live tail,
  `beforeId` returns older rows for scrolling back.
- **Payload comes back as `{ text, encoding }`** — a UTF-8 string when decodable, base64
  otherwise. The current endpoint calls `GetString` on a BLOB column, which will throw or
  mangle on the first binary payload.
- `from` / `to` are implemented now even though nothing in the UI sets them, so a time
  range picker later is frontend-only work.
- `/api/sequences/{id}` from the architecture doc folds into `?correlation=`.

## 6. Phase 4 — Frontend shell

- [x] Add Tailwind, TanStack Virtual, Zustand. All three are in the architecture doc, none are
  installed.
- [x] Delete the starter template: `App.css`, the hero / react / vite assets, the Vite links
  section in `App.tsx`.
- [x] Layout per the mockup: header, then three columns — controls rail (~260px), message
  table (flex), payload pane (~45%, the widest thing on screen).
- [x] Dark theme: near-black background, hairline borders, rounded panels.
- [x] Zustand store: `topicFilter`, `correlationSearch`, `correlationPaths`, `paused`,
  `autoScroll`, `selectedId`. Nothing more.

## 7. Phase 5 — Table and live tail

- [x] TanStack Virtual, fixed row height. Columns **Time / Delta / Topic**.
- [x] **Newest row at the top.** New rows prepend.
- [x] **Time**: `Intl.DateTimeFormat` with `fractionalSecondDigits: 3` → `10:39:05.126`.
- [x] **Delta**: time since the prior message — for a given row, its timestamp minus the
  timestamp of the row *below* it, computed over the rows currently in view. The oldest
  row on screen shows an em dash. Under a correlation search this is the gap between
  consecutive steps of the sequence, which is the number being troubleshot.
  Format: `<1s` → `842ms`, `<1min` → `4.2s`, `<1h` → `3m`, `<24h` → `2h`, else `3d`.
- [x] **Hold new rows while auto scroll is off.** They go to a pending buffer and a
  "N new messages, click to show" bar appears under the table header; flushing prepends them
  in order. Nothing moves under the pointer and the count is useful on its own.

> Superseded: the earlier plan here was to prepend always and compensate the scroll offset
> by `newRowCount * rowHeight` in the same frame. The pending buffer removes that bug class
> entirely — see section 13. Reverse this if you would rather have the live prepend.
- [x] `Auto scroll` means pinned at offset 0. It releases when the user scrolls away from the
  top and re-engages when they return.
- [x] Polling with TanStack Query at ~500ms using `afterId`, into a capped client buffer
  (~20k rows). `Pause` stops the refetch.

## 8. Phase 6 — Topic filter

The filter is a display filter. It never changes the broker subscription — `codeit/#` in
`appsettings.json` stays the subscription.

It is nevertheless applied **server-side**, not in the browser: the architecture doc's rule
is to never push 3000 msg/s at a browser only to discard most of it. "Frontend only" means
it does not reach the broker.

- [x] Trailing `#` is the common case and translates to an index-friendly
  `topic LIKE 'codeit/%'`, which uses `ix_messages_topic`.
- [x] The general case (`+` single-level wildcards, `#` mid-pattern) needs a real matcher.
  Register a `mqtt_match(topic, filter)` UDF via `SqliteConnection.CreateFunction` and
  share the same C# matcher with the ring buffer path, so live and history agree on what
  a pattern means.

## 9. Phase 7 — Payload pane and correlation UX

- [x] JSON → pretty-printed with syntax colouring. A ~60-line tokenizer rather than a
  highlighting library; the payload shape here is narrow.
- [x] Non-JSON → raw monospace. Binary → hex dump.
- [x] Size guard: anything over ~200 KB renders raw with a "format anyway" button, otherwise
  the pretty-printer will jank the UI.
- [x] Copy button and a byte-size readout.
- [x] Cogwheel next to "Correlation search" toggles the inline path editor. Because several
  paths are supported, this is a **textarea, one path per line, order significant** —
  a deviation from the single input in the drawing. Cancel / Save as drawn.
- [x] Save → `PUT /api/settings` → backfill → Save re-enables.
- [x] Recent keys list is clickable and fills the search box.

## 10. Phase 8 — Finish

- [x] `RetentionService`: delete past the window on a timer, chunked
  (`DELETE ... WHERE id < ? LIMIT 10000` in a loop) so it never holds a long write lock.
  No `VACUUM`.
- [x] Dropped counter as an amber chip at the right of the header, hidden while the count
  is zero. The architecture doc calls this the honest signal that the tool is losing data.
  There is no bottom status bar — it was removed during mockup review, and the rows / db
  size / retention readouts went with it.
- [x] Re-run the load test against the finished build and compare to the `wal-enabled`
  baseline.

---

## 11. Decisions

| # | Question | Decision |
| --- | --- | --- |
| 1 | Row order and delta direction | Newest at top. Delta = time since the prior message, i.e. the row below. Oldest row shows an em dash. The first draft of the mockup had the rows drawn in the wrong order. |
| 2 | Recent correlation keys | Recently seen on the wire. Served from the ring buffer, seeded from SQL once at startup. |
| 3 | One correlation path or many | Many. Tried in order, first non-null wins. |
| 4 | Does the topic filter re-subscribe | No. Display filter only; the broker subscription is unchanged. Still executed server-side. |
| 5 | Time range picker | Not now. `from` / `to` exist on the API so it is frontend-only work later. |
| 6 | `flows.yaml` | Dropped from scope. |
| — | `.gitignore` excluding docs | Deliberate. `PLAN.md` is added to it. |

---

## 12. Proposed edits to ARCHITECTURE.md

Not yet applied — say the word.

- Section 3: `sequence_id` → `correlation_key TEXT COLLATE NOCASE`; drop `service`; add the
  `settings` table.
- Section 5: replace the two endpoints with the six above.
- Section 4: add a fifth rule — ids are assigned by ingest, not by SQLite, so the ring
  buffer and the database share one id space.
- Section 6: remove `flows.yaml` from "what ships".
- Section 7: add three traps — `ORDER BY ts` instability, `GetString` on a BLOB payload,
  and the JSON functions reading a BLOB as JSONB without a `CAST`.

---

## 13. The UI mockup

An interactive mockup of the live view exists as an Artifact:
**https://claude.ai/artifact/2N7tkwaLMJcSGcyp1RE2MN** (private to the owner's account;
others need it shared from the page's Share menu).

It is a Design-canvas artboard, not a runnable page — the source is `.dc.html` and depends
on that canvas's runtime, so it cannot be opened in a browser or lifted into React as-is.
A session with the Artifact tool can read it; everything below is the version that does not
depend on that.

### Frame

| | |
| --- | --- |
| Canvas | 1440 × 900 |
| Header | 56 px |
| Controls rail | 264 px, left |
| Table | flex |
| Payload pane | 616 px, right |
| Table header / row / footer | 34 / 30 / 28 px |
| Type | IBM Plex Sans (UI), IBM Plex Mono (all data, timestamps, topics, JSON) |

No status bar at the bottom. The dropped counter is an amber chip at the far right of the
header, hidden while the count is zero.

### Colors

| Role | Hex |
| --- | --- |
| Page ground | `#0B0D10` |
| Panel (header, rail, table header) | `#0E1115` |
| Hairline border | `#1C222A` |
| Control border | `#232A33` |
| Row hover / selected | `#151A20` / `#172028` |
| Text primary / secondary / tertiary | `#E6EAF0` / `#8A94A3` / `#6A7684` |
| Topic tail / topic head (dimmed prefix) | `#B7C0CC` / `#4E5866` |
| Accent (selection bar, active control) | `#6FD3C7`, filled `#163B38` |
| Warning (drops, deltas ≥ 1 min) | `#E8A860`, filled `#2A1F12`, border `#6B4A22` |
| JSON key / string / number / boolean / null / punctuation | `#8FB8FF` / `#86C98A` / `#E8B36A` / `#C79BE8` / `#6A7684` / `#5D6773` |

### Behavior the screenshot does not show

- Rows are newest-first. Delta is this row's timestamp minus the row *below* it; the oldest
  row on screen shows an em dash. Deltas of a minute or more render amber.
- Topics are split: the shared prefix (`codeit/boliden/DDATA/odda/foundry/fvl/`) is dimmed,
  the last three segments are bright. Without this the identifying part of the topic is off
  the right edge.
- Turning Auto scroll off holds new rows in a buffer and shows a
  "N new messages, click to show" bar under the table header, instead of prepending them
  and compensating the scroll offset. This is a change from section 7 — it removes the
  scroll-compensation bug class entirely and the pending count is useful on its own.
- The cogwheel opens the path editor inline below the search box: a textarea, one path per
  line, order significant, with Cancel / Save.
- Clicking a recent correlation key filters the table to that sequence and fills the search
  box with the full key.
- The payload pane header carries the timestamp, byte size, encoding, QoS, the full topic,
  and the correlation key — or "none (no configured path resolved)" when extraction found
  nothing, which is how a noise topic reads.

---

## 14. Tests worth writing

There is no test project today. The point is not coverage, it is catching the specific
things in this design that fail silently. Four earn their keep immediately; the rest are
worth having.

### The four

1. **Correlation extraction** (`xunit`, pure, table-driven). Every message goes through it
   and it must never throw. Cases: path present; path nested but absent; path resolving to
   an object or array rather than a scalar (must yield null, not a stringified object);
   malformed JSON; non-JSON bytes; empty payload; uppercase GUID; candidate ordering, where
   the first path misses and the second hits; a payload larger than the bail-out threshold.
2. **MQTT topic matching**, run as ONE table against BOTH implementations — the C# matcher
   used by the ring buffer and the `mqtt_match` UDF used by SQL. If live and history
   disagree about what `+` means, the bug shows up as "the filter works until I scroll".
   Cases from the spec: `sport/+` does not match `sport/tennis/player1`; `sport/#` matches
   `sport` itself; `#` matches everything; `+/tennis/#`; a filter must not match a topic
   beginning with `$`.
3. **The paging invariant**, as a property test. Interleave inserts with `afterId` polls at
   random points; assert the union of returned ids equals the set of inserted ids, with no
   duplicates and no gaps. This is the test that fails loudly if anyone reintroduces
   `ORDER BY ts`, which is otherwise a heisenbug that only appears under load.
4. **`fmtDelta` boundaries** (`vitest`). See the correction below — the naive version is
   already wrong.

### Worth having

- **Writer batching** against a real SQLite file in a temp directory, not in-memory: WAL
  behaves differently. Assert all N rows land, ids are contiguous, and one malformed
  payload does not abort the other 499 in the batch.
- **Id seeding across a restart**: write 100 rows, dispose, recreate, assert the next id is
  101. Rule 5 failing is silent corruption.
- **Backfill**: rows with mixed payloads (valid JSON with the path, valid JSON without,
  malformed, binary), run the path change, assert the right keys and the right nulls. This
  is the test that would have caught the BLOB-as-JSONB trap.
- **Endpoint contracts** via `WebApplicationFactory`: the list endpoint never returns
  payload bytes; `/api/messages/{id}` returns base64 for a non-UTF-8 payload.
- **Retention**: chunked delete removes the right rows, leaves the rest, holds no long lock.
- **Frontend**: `fmtTime` zero-padding (`.042`, never `.42`); delta recomputed against the
  previous row *in the filtered view*; the pending-rows buffer preserving order and not
  duplicating rows already on screen; the JSON tokenizer on nested, empty, deep, and
  non-object payloads.
- **One** Playwright smoke test, not a suite: load against a seeded database, assert rows
  render and clicking one shows its payload.

The load-test harness in `loadtest/` is already the performance regression suite. Give it a
threshold against the `wal-enabled` baseline in `history.jsonl` so a regression fails rather
than just being recorded.

### Correction to the delta formatter (section 7)

Choosing the unit before rounding is wrong at every boundary: 59 999 ms is below the 60 000
cutoff, so it formats as `60.0s` instead of `1m`, and 999.6 ms formats as `1000ms` instead
of `1.0s`. Round first, then pick the unit, and promote when rounding crosses the
threshold. The mockup still contains the naive version.

---

## 15. Commit discipline

**Commit often — every green checkpoint, not every phase.** A commit is the unit of
progress here and the only cheap way back.

- One commit per coherent step: a service, an endpoint, a component. If the message needs
  the word "and", it is two commits.
- Never commit red. `make check` (section 16) passes first.
- Work on a branch per phase (`phase-0-foundations`), merge to `main` when the phase's
  checklist is complete.
- The schema migrations and the id-assignment change are the risky ones. Commit those
  alone, so a revert is one command and does not take the frontend with it.
- A commit is also what makes the loop below recoverable: without frequent commits an
  unattended agent has no rollback point and a bad iteration costs the whole session.

---

## 16. Running this as a loop

An agent looping over this plan needs three things. Two do not exist yet.

**1. A goal gate** — one command, exit code is the whole signal:

```make
check:
	dotnet build src/TraceMQ.Api
	dotnet test
	cd web && npm run lint && npx vitest run && npm run build
```

The loop is only as good as this gate. A gate that passes on an empty test suite will let
an agent loop happily while producing nothing — which is the real argument for section 14.

**2. Durable state** — a checklist in this file, since each iteration may start with a
fresh context. Phases 0 to 8 become checkboxes, ticked only after `make check` is green and
the work is committed. The file is the memory; the context window is not.

**3. The loop itself** — `/loop` in Claude Code, either on an interval or self-paced:

```
/loop Read PLAN.md, take the first unchecked item, implement it, run `make check`,
commit when green, tick the box. Stop and report if the gate fails twice on the same item.
```

Omitting an interval lets the model pace itself, which suits this better than a fixed
timer — the work is uneven.

Watch the first few iterations before leaving it. The failure mode is not a crash, it is an
agent ticking boxes with a gate too weak to notice it has built the wrong thing.
