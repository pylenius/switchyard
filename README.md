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
switchyard claim --as feat-b --wait -- npm run test:e2e   # hold the gateway for one command (see Several agents)
switchyard claim --as feat-b --wait   # ...or until you release it
switchyard release --as feat-b
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

```bash
git worktree add -b my-feature ../shop-my-feature   # the checkout is named after the folder: shop-my-feature
switchyard workspace                                # lists it (worktrees are re-read every few seconds)
switchyard workspace shop-my-feature orders         # orders now builds and runs from there; the rest stay
switchyard build orders                             # works from either folder: both find the same switchyard.json
switchyard workspace default orders                 # back to the default checkout, before `git worktree remove ../shop-my-feature`
```

- Switching stops the service, then builds and starts it from the new checkout. `switchyard status`
  shows the checkout each service runs from.
- `default` undoes a switch: the service follows the default checkout again. Naming a checkout
  (`main`) pins it there, even when the default changes later.

- The first start in a new worktree is a full restore and build, so it takes longer.
- A worktree has only what git tracks: files the repository ignores (local secrets such as
  `appsettings.Local.json`, `.env`) and uncommitted changes in the main checkout are not there. Copy
  what the service needs.
- The commands inside a worktree reach the same gateway, because its `switchyard.json` has the same
  `Name` and `Port`. Do not start a second gateway there; it is refused while one runs.
- The gateway is shared: switching a service changes what everything behind it sees, the browser and
  the tunnel included.
- `switchyard workspace my-feature` without names moves the default, so every service that has no
  checkout of its own follows it.

## Configuration

`switchyard.json` is found in the current folder or the nearest one above it (or `--config`,
or `$SWITCHYARD_CONFIG`). Every key is documented in [`GatewayOptions.cs`](src/Switchyard/GatewayOptions.cs).

| Key | Default | |
|---|---|---|
| `Name` | the repository's folder name | shown in the UI; names the state folder |
| `Port` | `8088` | the gateway's port |
| `RoutePrefix` | `/{name}` | path a service is routed on; a service's `PathPrefix` overrides it |
| `RunAs` | `Development` | ASP.NET environment for `dotnet` services |
| `ClaimIdleMinutes` | `10` | a claim whose holder sends nothing for this long lapses |
| `StateFile` | `~/.config/switchyard/{Name}/state.json` | routing, backup environment and checkouts, kept across restarts |
| `ProcessEnv` | | environment variables for every process |
| `Environments` | | `Name`, `Upstreams` (`{ "api": "https://..." }`), `Color`, `Warn` |
| `Services` | | `Name`, `LocalUrl` (empty: run but not routed), `PathPrefix` or `PathPrefixes` (`""`: every path), `Port` (own listener, every path), `Upstream`, `Health`, `Group`, `Process` |

`Process` holds the type and its settings (`Project`, `Dir`, `Command`, `Args`, `Image`, ...), `Env`,
`DependsOn` and `ShutdownSeconds`. `Env` values expand `${VAR}`, `${VAR:-default}`, `{env}` and
`{user}`. Two sources fill a variable that is not already set:

- `EnvFromUserSecrets`: a key from the project's `dotnet user-secrets`.
- `EnvFromCommand`: the output of a command, e.g. a vault CLI:
  `["az", "keyvault", "secret", "show", ...]`, `["op", "read", "op://..."]`. Only the command is logged.

Your own overrides go in `switchyard.user.json` next to the config (keep it out of git). Any
key can also be given on the command line, e.g. `--Port=9000`.

## Several agents

Agents working on features in their own worktrees share one gateway: one set of ports, one routing, one
running copy of each service. So keep apart what can run side by side from what cannot:

- **Compile and unit-test in your own worktree** with the usual tools (`dotnet build`, `dotnet test`,
  `npm run test:unit`). Each worktree has its own `bin/` and `obj/`, so agents can build at the same
  time. `switchyard build` is not for this: it builds the checkout the service runs from, which may be
  another agent's.
- **Claim the gateway for anything that runs against it** (end-to-end tests, a browser, a phone).
  Give it the command, so the claim cannot outlive it:

```bash
cd ../shop-feat-b
switchyard claim --as feat-b --wait -- npm run test:e2e
```

  That waits behind whoever holds the gateway, moves the dotnet and vite services to this worktree,
  waits until they run from it, runs the command in the foreground (renewing the claim meanwhile),
  and releases when it exits: also when it fails, on Ctrl-C or on SIGTERM. Its exit code is the
  command's. Several steps: `-- sh -c 'curl -fsS localhost:8088/health && npm run test:e2e'`.

  Without a command, the claim is held until `switchyard release --as feat-b`: for poking at it in a
  browser. Run that `claim --wait` in the foreground, not through a pipe (`| tail` shows nothing until
  it ends), so you know when you hold it.

While a claim is held, anyone else's `start`, `stop`, `restart`, `build`, `workspace` and routing changes
are refused with who holds it and until when (`switchyard status` shows it too); reads (`status`, `logs`,
`errors`) stay open and need no name. Every command the holder sends, reads included, keeps it alive. A
claim lapses at the end of `--for` (default 30 min), or sooner when its holder sends nothing for
`ClaimIdleMinutes` (default 10), so an agent that crashed or went quiet cannot block the others. `--force`
overrides it, and the text UI always does: it is you at the keyboard, and its header shows the claim so
you know you would cut into a test. Set `SWITCHYARD_AS` instead of passing `--as` every time.
`--no-switch` claims without moving any service. Right after a release the services show Stopped for a
moment while the next holder's checkout builds.

## Recipes

[`examples/monolith.json`](examples/monolith.json) puts most of these together: one backend for
every path, native redis, postgres in docker, a tunnel and a frontend.

**One backend for every path.** `"PathPrefix": ""` routes everything to it rather than `/{name}/...`.

**A backend on some paths, the frontend on the rest.** The more specific path wins, so a frontend on `""`
gets every path the backend does not list. Toggle either one off and only its paths go to the backup
environment, so a local frontend can run against a deployed backend and the other way round.

```json
{ "Name": "backend", "LocalUrl": "http://localhost:5000", "PathPrefixes": [ "/api", "/hubs", "/.well-known" ],
  "Process": { "Type": "dotnet", "Project": "src/App.Web/App.Web.csproj" } },
{ "Name": "web", "LocalUrl": "http://localhost:5173", "PathPrefix": "",
  "Process": { "Type": "vite", "Dir": "web", "Port": 5173 } }
```

Paths match whole segments and ignore case: `/files` routes `/files/1` but not `/filesystem`. The app has to
call its own origin (`/api/...`, not an absolute URL) for this to work through a tunnel too; vite's
hot reload goes through the gateway as a websocket, and behind a https tunnel it may need
`server.hmr.clientPort: 443`.

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

**Telling a coding agent.** Agents do not know any of this unless the repository says it. Put a section
like this in `CLAUDE.md` or `AGENTS.md`, with your own service names and test command. This one was
tested with three agents working at once, each in its own worktree:

```markdown
## Dev environment (switchyard)
switchyard runs the services (switchyard.json); several agents may share it at once, each in its own
git worktree.
- **Compile and unit-test in your own checkout** with the usual tools (`dotnet build`, `dotnet test`,
  `npm run test:unit`). They run in parallel with other agents. Not `switchyard build`: it compiles the
  checkout the service runs from, which may be another agent's.
- **Run anything against the stack as one claimed command**, in the foreground, from your checkout:
  `switchyard claim --as <your-branch> --wait -- npm run test:e2e`
  It waits behind another agent, runs the backend and frontend from your checkout, runs the command and
  releases when it ends, also on failure; its exit code is the command's. It needs no release. Join
  steps with `&&`: `-- sh -c 'curl -fsS localhost:8088/health && npm run test:e2e'`.
- Never run `claim --wait` in the background or through a pipe, and do not end your turn waiting for it.
- "Claimed by X since ..." means another agent is testing: wait. Never `--force` unless the user says so.
- Never start a second gateway, and never run a service yourself on its port (`dotnet run`).
- Worktrees: `git worktree add -b <branch> ../<repo>-<branch>`. They lack ignored files (local secrets
  such as `appsettings.Local.json`, `.env`); copy what the service needs.
- `switchyard status` shows who holds it and which checkout each service runs from; `switchyard logs
  backend -n 200` its output. Reads need no `--as`.
```

What each line prevents, from that test:

- **Compile in your own checkout**: `switchyard build` from a worktree reports another agent's build as
  yours.
- **One claimed command**: an agent that claims, then tests, then releases as separate steps can leave
  the stack held while it does something else. Two of three did in the first try.
- **Not in the background**: an agent that started `claim --wait` in the background and ended its turn
  was not woken when it returned, and held the gateway idle for minutes. With a command, the claim
  cannot outlive the test whatever the agent does.
- **`&&`**: all three agents wrote `;`, which runs the test even when the check before it fails.
- **Read the file**: subagents may get the `CLAUDE.md` their parent session loaded when it started.
  After changing these instructions, start new sessions, or tell agents to read the file.

A test script can also refuse to run against the wrong checkout: compare the `runningIn` checkout of the
service in `/_gw/state` (its path is in `workspaces`) with the repository the script is in.

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

