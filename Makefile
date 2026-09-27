# TraceMQ build commands. Developed on macOS, deployed on Windows.
# Run `make` on its own for the list.

API      := src/TraceMQ.Api
WEB      := web
DIST     := dist
WWWROOT  := $(API)/wwwroot

# osx-arm64 on Apple Silicon, osx-x64 on Intel.
MAC_RID  := osx-$(shell uname -m | sed 's/x86_64/x64/')
WIN_RID  := win-x64

.DEFAULT_GOAL := help
.PHONY: help run watch web frontend build sim demo check clean \
        publish-win publish-win-fd publish-mac publish-all

help: ## Show this list
	@grep -hE '^[a-z-]+:.*##' $(MAKEFILE_LIST) \
	  | sed 's/:.*##/\t/' \
	  | awk -F'\t' '{printf "  \033[36m%-16s\033[0m %s\n", $$1, $$2}'
	@echo ""
	@echo "  windows RID: $(WIN_RID)    mac RID: $(MAC_RID)"

# --- develop ----------------------------------------------------------------

run: $(WWWROOT)/index.html ## Run the API at http://localhost:5027 (serves the last built frontend)
	dotnet run --project $(API)

watch: $(WWWROOT)/index.html ## Same, with hot reload on C# changes
	dotnet watch --project $(API)

web: $(WEB)/node_modules ## Vite dev server at http://localhost:5173, proxying /api to 5027
	cd $(WEB) && npm run dev

frontend: $(WEB)/node_modules ## Force a frontend rebuild into the API's wwwroot
	cd $(WEB) && npm run build

build: ## Debug build of the API (does not touch the frontend)
	dotnet build $(API)

# --- traffic ----------------------------------------------------------------
# tools/sim.py plays an assembly line in real time, ~3 minutes, via mosquitto_pub.
# `make sim` feeds whatever is running; `make demo` is a clean instance of its own,
# on its own port and database, with neutral topics — for screenshots and showing off.

DEMO_PORT := 5028
DEMO_DB   := $(API)/demo.db

sim: ## Play a simulated line into the local broker under codeit/sim/ (~3 min)
	python3 tools/sim.py

demo: $(WWWROOT)/index.html ## Fresh instance at http://localhost:5028 on factory/#, filled by the sim
	@rm -f $(DEMO_DB) $(DEMO_DB)-wal $(DEMO_DB)-shm
	@Storage__DbPath=$(abspath $(DEMO_DB)) Urls=http://localhost:$(DEMO_PORT) \
	  Mqtt__ClientId=tracemq-demo Mqtt__Topics__0='factory/#' \
	  dotnet run --no-launch-profile --project $(API) >/dev/null & pid=$$!; \
	trap 'kill $$pid 2>/dev/null' EXIT INT TERM; \
	until curl -sf http://localhost:$(DEMO_PORT)/api/status >/dev/null; do \
	  kill -0 $$pid 2>/dev/null || { echo "demo API failed to start"; exit 1; }; sleep 0.5; \
	done; \
	echo "==> http://localhost:$(DEMO_PORT) - set the topic filter to factory/#"; \
	python3 tools/sim.py --root factory/line2 && \
	echo "==> sim done; the demo stays up until Ctrl+C" && wait $$pid

# --- verify -----------------------------------------------------------------
# One gate. The exit code is the whole signal, for a human and for an agent loop
# (PLAN.md section 16). Steps with nothing to run yet SKIP loudly rather than
# passing quietly: a gate whose holes you cannot see is worse than no gate.

check: $(WEB)/node_modules ## Build, lint and test everything; the goal gate
	@echo "==> dotnet build"
	@dotnet build $(API) --nologo -v quiet
	@echo "==> dotnet test"
	@if find tests -name '*.csproj' 2>/dev/null | grep -q .; then \
		dotnet test --nologo -v quiet; \
	else \
		echo "    SKIPPED - no test project under tests/ (PLAN.md section 14)"; \
	fi
	@echo "==> oxlint"
	@cd $(WEB) && npm run --silent lint
	@echo "==> vitest"
	@if find $(WEB)/src -name '*.test.ts' -o -name '*.test.tsx' 2>/dev/null | grep -q .; then \
		cd $(WEB) && npx --no-install vitest run; \
	else \
		echo "    SKIPPED - no frontend tests yet (PLAN.md section 14)"; \
	fi
	@echo "==> frontend build"
	@cd $(WEB) && npm run --silent build
	@echo "==> check passed"

# --- publish ----------------------------------------------------------------
# Release implies the csproj's BuildFrontend target, so npm runs first.

publish-win: ## Windows exe, self-contained, one file (~50 MB, no runtime needed)
	dotnet publish $(API) -c Release -r $(WIN_RID) -o $(DIST)/$(WIN_RID)
	@ls -lh $(DIST)/$(WIN_RID)

publish-win-fd: ## Windows exe, framework-dependent (~5 MB, needs ASP.NET Core 10 on the box)
	dotnet publish $(API) -c Release -r $(WIN_RID) --self-contained false \
	  -p:PublishSingleFile=true -o $(DIST)/$(WIN_RID)-fd
	@ls -lh $(DIST)/$(WIN_RID)-fd

publish-mac: ## macOS binary, self-contained, one file — for trying the real artifact locally
	dotnet publish $(API) -c Release -r $(MAC_RID) -o $(DIST)/$(MAC_RID)
	@ls -lh $(DIST)/$(MAC_RID)

publish-all: publish-win publish-mac ## Both of the above

# --- housekeeping -----------------------------------------------------------

clean: ## Remove build output, dist, and the generated frontend
	dotnet clean $(API) >/dev/null 2>&1 || true
	rm -rf $(DIST) $(WWWROOT) $(API)/bin $(API)/obj

# --- generated prerequisites -------------------------------------------------
# The SPA is served from embedded resources, so wwwroot must exist before the API
# will start. These rules build it once, then stay out of the way.

$(WEB)/node_modules: $(WEB)/package-lock.json
	cd $(WEB) && npm ci
	@touch $@

$(WWWROOT)/index.html: $(WEB)/node_modules
	cd $(WEB) && npm run build
