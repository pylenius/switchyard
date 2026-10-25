# Switchyard

A local entry point for a set of HTTP services that also builds and runs them. Each service is
either **local** (the gateway builds it, runs it and routes to it) or goes to a **backup
environment** (dev, staging, prod: whatever you configure). Run only the service you are working
on and get the rest from a shared environment, behind one local address whose paths match the
deployed ones:

```
http://localhost:8088/api/orders/...   ──▶  orders, built and run from your checkout
http://localhost:8088/api/catalog/...  ──▶  https://dev-api.example.com/api/catalog/...
```

- Project types: .NET (`dotnet`), Vite frontends (`vite`), containers (`docker`) and any other
  program (`command`).
- Compile errors from MSBuild and tsc as `file:line:col`, in the text UI and from the command line.
- Each service can run from the main checkout or any git worktree.
- A text UI, an HTTP API and commands, so scripts and coding agents can build, restart and read logs.
- macOS, Windows and Linux; needs the .NET 10 runtime (the SDK for `dotnet` services).

Built on [YARP](https://github.com/dotnet/yarp).

## Install and run

```bash
dotnet tool install -g Switchyard
```

Put a `switchyard.json` in your repository (start from
[`examples/switchyard.json`](examples/switchyard.json)) and run the gateway in it, or anywhere
below it:

```bash
switchyard                          # text UI
switchyard --env Staging            # dotnet services run as this ASP.NET environment
switchyard --workspace my-feature   # services run from that git worktree (remembered)
switchyard --headless               # no UI; use the commands below
switchyard --config path/to/switchyard.json
dotnet run --project src/Switchyard --  # from source
```

With no command it attaches to a gateway that is already running. If none is, it starts one
in-process; quitting then asks before it stops the processes. One gateway runs per state folder
(per `Name`): a second one for the same project exits instead of taking over its processes. Projects
with different names and ports run side by side. `switchyard --help` lists the flags and commands.

`--no-processes` makes it route only, for when something else runs the services.

## Text UI

```
  shop · switchyard  http://localhost:8088   as Development   checkout main   backup  dev   staging   prod

      NAME       ROUTE         GOES TO  STATE                    BUILD         CHECKOUT
  services
  > [x] orders     /api/orders   local    ● running 12m            ✗ 2 errors    my-feature
    [ ] catalog    /api/catalog  dev      ○ off                                  main
  infra
    [x] db                       -        ● running 12m                          main
  frontend
    [x] web                      -        ● running 12m                          main
```

| Key | |
|---|---|
| `↑↓` / `j k` | move |
| `space` | local on/off: builds and starts it, or stops it and routes to the backup environment |
| `enter` / `l` | log view: `/` filter, `d` debug lines on/off, `f` follow, PgUp/PgDn, `esc` back |
| `e` | the last build's errors; `enter` on one shows it in the log |
| `b` / `B` | build this one / every local one; a .NET service restarts on success, and keeps running the old build on errors |
| `r` / `s` / `x` | restart / start / stop. A stopped service stays local, so you can run it from the IDE instead (it shows as `external`) |
| `w` / `W` | checkout for this service / for all of them; a running service restarts from the new one |
| `←→` | backup environment |
| `a` / `n` | every routed service local / to the backup environment |
| `q` | quit |

An environment with `"Warn": true` (production) shows a warning while it is the backup: toggled-off
services then read and write its live data.

## Commands

Against a running gateway; useful for scripts and for coding agents:

```bash
switchyard status
switchyard build orders           # build output + errors as file:line:col; exit 1 on errors; restarts it if it runs
switchyard build all --no-restart
switchyard errors orders
switchyard logs orders -n 100 -f
switchyard restart orders catalog
switchyard stop orders            # stays local but stopped (run it from the IDE)
switchyard setup web              # rm -rf node_modules && npm ci
switchyard workspace                      # list the checkouts
switchyard workspace my-feature orders    # orders from that worktree ("default" follows the default)
switchyard which                  # config, state folder, gateway URL and ports for this folder; no gateway needed
```

## Processes

| Type | Setup | Build | Runs | Ready when |
|---|---|---|---|---|
| `dotnet` | (restore is part of the build) | `dotnet build`, before every start too | the built dll, with the first `Project` profile of `launchSettings.json` | `Health` answers 2xx, else the port |
| `vite` | `npm ci` when `node_modules` is missing or older than `package-lock.json` | `vue-tsc` / `tsc --noEmit` (the dev server compiles by itself) | `npm run dev` | its `Port` answers |
| `docker` | removes a leftover container of the same name | | `docker run --rm` in the foreground; stopped with `docker stop` | `ReadyExec` succeeds in the container |
| `command` | | | the `Command` as given | still running after a moment |

- A .NET service runs as `--env` (else `$SWITCHYARD_ENV`, else `RunAs`), with
  `ASPNETCORE_ENVIRONMENT` set to match.
- `DependsOn` waits for a routed dependency only while it is local (otherwise the backup environment
  serves it), and starts an unrouted one (a database) along with it.
- A process that exits on its own is restarted up to three times, also when it exits before it is
  ready.
- On a fresh state (no state file yet) every routed service is local. One without a `LocalUrl` (a
  tunnel, a frontend nobody routes to) stays off until you start it once (`switchyard start ngrok web`)
  or a running service `DependsOn` it; the state file remembers it from then on.
- Toggling a service off also stops its process, so its own port goes down along with the route.
- `docker` containers run with `--rm`: keep data in a named volume (`"Volumes": [ "shop-db:/var/lib/postgresql" ]`).
  Without one, the image's `VOLUME` gets an anonymous volume, and `--rm` deletes it on every stop.
- Output is kept per process (the last 5000 lines) and written to `logs/{name}.log` in the state
  folder. Running processes' pids are in `pids.json` there, so a gateway that was killed stops its
  leftovers on the next start.

## Checkouts

A service runs from the main checkout or from any git worktree of it (`git worktree list`). There is
one default for all services, and each can have its own. Builds happen in the checkout the service
runs from, so a new worktree needs nothing set up: the first start restores, builds and runs `npm ci`
there.

## Configuration

`switchyard.json` is found in the current folder or the nearest one above it (or `--config`,
or `$SWITCHYARD_CONFIG`). Every key is documented in [`GatewayOptions.cs`](src/Switchyard/GatewayOptions.cs).

| Key | Default | |
|---|---|---|
| `Name` | the repository's folder name | shown in the UI; names the state folder |
| `Port` | `8088` | the gateway's port |
| `RoutePrefix` | `/{name}` | path a service is routed on; a service's `PathPrefix` overrides it |
| `RunAs` | `Development` | ASP.NET environment for `dotnet` services |
| `StateFile` | `~/.config/switchyard/{Name}/state.json` | routing, backup environment and checkouts, kept across restarts |
| `ProcessEnv` | | environment variables for every process |
| `Environments` | | `Name`, `Upstreams` (`{ "api": "https://..." }`), `Color`, `Warn` |
| `Services` | | `Name`, `LocalUrl` (empty: run but not routed), `Port` (own listener, every path), `Upstream`, `Health`, `Group`, `Process` |

`Process` holds the type and its settings (`Project`, `Dir`, `Command`, `Args`, `Image`, ...), `Env`,
`DependsOn` and `ShutdownSeconds`. `Env` values expand `${VAR}`, `${VAR:-default}`, `{env}` and
`{user}`. Two sources fill a variable that is not already set:

- `EnvFromUserSecrets`: a key from the project's `dotnet user-secrets`.
- `EnvFromCommand`: the output of a command, e.g. a vault CLI:
  `["az", "keyvault", "secret", "show", ...]`, `["op", "read", "op://..."]`. Only the command is logged.

Your own overrides go in `switchyard.user.json` next to the config (keep it out of git). Any
key can also be given on the command line, e.g. `--Port=9000`.

## Recipes

[`examples/monolith.json`](examples/monolith.json) puts most of these together: one backend for
every path, native redis, postgres in docker, a tunnel and a frontend.

**One backend for every path.** `"PathPrefix": ""` routes everything to it rather than `/{name}/...`.

**A program installed on the machine.** A `command` with a `Port` is ready once the port answers, so a
service that `DependsOn` it waits for it:

```json
{ "Name": "redis", "Group": "infra",
  "Process": { "Type": "command", "Command": "redis-server", "Args": [ "/opt/homebrew/etc/redis.conf" ], "Port": 6379 } }
```

**A tunnel for webhooks and phones.** Point it at the gateway, not at a service: what comes through
it then follows the local/backup switch like everything else.

```json
{ "Name": "ngrok", "Group": "tunnel",
  "Process": { "Type": "command", "Command": "ngrok", "Args": [ "http", "--url=myapp.ngrok.io", "8088" ], "DependsOn": [ "backend" ] } }
```

**Tests that must not reach a shared environment.** End-to-end tests that go through the gateway or
the tunnel hit the backup environment while a service is toggled off, writes included. Check first:

```bash
curl -fsS localhost:8088/_gw/state | node -e '
  const s = JSON.parse(require("fs").readFileSync(0));
  process.exit(s.services.find((x) => x.name === "backend").local ? 0 : 1)' \
  || { echo "backend is not local: toggle it on in switchyard"; exit 1; }
```

**Telling a coding agent.** A section like this in the repository's `CLAUDE.md` or `AGENTS.md`:

```markdown
## Dev environment
switchyard runs the services (switchyard.json). Against a running gateway:
- `switchyard status` first: what runs, where it is routed, the last build.
- Build with `switchyard build backend` (errors as file:line:col, exit 1 on errors; restarts it), not
  `dotnet build` or `dotnet run`: a second instance competes for the port, and a plain build leaves
  the running one on the old code.
- `switchyard logs backend -n 200`, `switchyard restart frontend`.
- `switchyard which` says which config and ports this folder uses, even with no gateway running.
```

**Coming from process-compose (or a start script).** Each process becomes a service: an API you route
to gets a `LocalUrl` (`dotnet` or `command`), the rest become unrouted `command`, `docker` or `vite`
services, and `depends_on` becomes `DependsOn`. Keep the ports they had, so the apps' own config stays
as it is. Before the first start, check where a database container keeps its data (`docker inspect`):
if it is an anonymous volume, copy it into a named one first, or `--rm` deletes it.

## API

The UI and the commands are clients of this HTTP API:

```bash
curl localhost:8088/_gw/state
curl -X PUT localhost:8088/_gw/backup                  -H 'content-type: application/json' -d '{"env":"staging"}'
curl -X PUT localhost:8088/_gw/services/orders         -H 'content-type: application/json' -d '{"local":true}'
curl -X PUT localhost:8088/_gw/services                -H 'content-type: application/json' -d '{"local":false}'
curl -X PUT localhost:8088/_gw/workspace               -H 'content-type: application/json' -d '{"workspace":"main"}'
curl -X PUT localhost:8088/_gw/services/orders/workspace -H 'content-type: application/json' -d '{"workspace":null}'
curl -X POST 'localhost:8088/_gw/processes/orders/build?restart=true'   # also start, stop, restart, setup
curl localhost:8088/_gw/processes/orders                                # state, pid, last build summary
curl localhost:8088/_gw/processes/orders/build                          # last build with its errors
curl 'localhost:8088/_gw/processes/orders/logs?after=0&tail=200'
```

The API answers only requests whose Host is `localhost:{Port}` or `127.0.0.1:{Port}`. Expose the
gateway through a tunnel (frp, ngrok, cloudflared) for webhooks and the requests reach your services,
but not `/_gw`.

## Windows

- Each process runs in its own Job Object. Stopping it ends the whole job, so nothing a tool started
  in between survives (`npm run dev` is cmd → npm → cmd → node). If the gateway itself dies, Windows
  ends its processes too.
- Processes are killed rather than asked to stop (console processes there have no SIGTERM), so a
  service's shutdown hooks do not run.
- A running .NET service is stopped *before* its rebuild, because Windows locks a loaded dll. A build
  with errors therefore leaves it down until the next successful build (elsewhere the old build keeps
  serving).
- `npm`, `npx` and other `.cmd` tools are started through `cmd.exe`.

## Building

```bash
dotnet build src/Switchyard
./publish.sh pack       # artifacts/nupkg: dotnet tool install -g Switchyard --add-source artifacts/nupkg
./publish.sh binaries   # framework-dependent builds per platform in artifacts/{rid}/
```

## Releasing

CI builds and smoke-tests every push on Linux, macOS and Windows. A version tag also packs that
version and publishes it to nuget.org:

```bash
git tag v0.1.0 && git push origin v0.1.0
```

Publishing uses [NuGet trusted publishing](https://aka.ms/nuget/trusted-publishing): a nuget.org
policy (owner `pylenius`, repository `pylenius/switchyard`, workflow `ci.yml`, package `Switchyard`)
lets that workflow exchange its GitHub OIDC token for a short-lived API key. No key is stored.

