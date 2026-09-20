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
.PHONY: help run watch web frontend build clean \
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
