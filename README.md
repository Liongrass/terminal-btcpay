# terminal-btcpay

A BTCPay Server plugin that installs and manages [Lightning Terminal](https://github.com/lightninglabs/lightning-terminal)
(`litd`) on a btcpayserver-docker deployment, and replaces litd's own web UI.

Under **Plugins → Lightning Terminal**, a server administrator can see whether litd is installed and
healthy, install it against BTCPay's bundled LND, pair a browser with the node through
[Terminal on the web](https://terminal.lightning.engineering), download litd's logs, and remove it
again — keeping its data or wiping it.

Installing this plugin does **not** install litd. That is a separate, deliberate step.

## What makes this different from the stock install

BTCPay Server can already run litd through btcpayserver-docker's
[`opt-add-lightning-terminal`](https://docs.btcpayserver.org/Docker/lightning-terminal/) fragment.
That fragment serves litd's web UI at `/lit/` on your public BTCPay hostname, behind a 64-character
password kept in `secrets/lit_password` on the host.

This plugin generates its own fragment instead: the same one, with `--disableui`.

|                        | `opt-add-lightning-terminal` | this plugin's fragment |
| ---------------------- | ---------------------------- | ---------------------- |
| litd web UI            | served at `/lit/`            | off (`--disableui`)    |
| UI password            | generated, stored on host    | none — nothing to protect or rotate |
| Public HTTP surface    | `/lit/` plus the `lnrpc.` / `looprpc.` / `poolrpc.` / `litrpc.` gRPC-web endpoints | none |
| How you manage the node | litd's web UI                | this plugin, and Terminal on the web over LNC |
| litd image             | `…-path-prefix` rebuild (needed only to serve the UI under `/lit/`) | the plain Lightning Labs release image |
| litd's listener        | `--insecure-httplisten` on `:8080`, for nginx to proxy | `--httpslisten` on `:8443`, no plaintext port |
| Bitcoin Core data dir  | mounted into litd, for Faraday's `connect_bitcoin` | not mounted |
| Container name         | Compose-derived (`generated-lnd_lit-1`) | `btcpayserver_litd` |

The remote-LND wiring, the `lnd_lit_datadir` volume and `rpcmiddleware.enable=true` on LND are
identical to upstream's, deliberately — the two fragments share a volume name, so you can move
between them without losing litd's data.

Two further trims beyond turning the UI off:

- **No plaintext listener.** Upstream needs `--insecure-httplisten` because nginx proxies `/lit/` to
  it. This plugin speaks TLS gRPC, so a plaintext port carrying macaroons is pure attack surface.
  `--httpslisten` has to be set explicitly all the same: litd defaults it to `127.0.0.1:8443`, which
  is loopback inside litd's own container and unreachable from BTCPay's.
- **No Faraday bitcoind wiring.** `connect_bitcoin` defaults off. Leaving it off costs the handful of
  Faraday endpoints that need chain data, and in exchange litd has no mount into Bitcoin Core's data
  directory. Add the four `--faraday.bitcoin.*` flags and the `bitcoin_datadir` mount back if you
  want those endpoints.

On **regtest**, the fragment also passes `--autopilot.disable`. litd resolves the Lightning Labs
Autopilot server address from its `--network` and, for anything but mainnet or testnet, returns
`no autopilot server address specified` — which aborts startup rather than degrading one sub-server,
so litd would never come up at all. Mainnet and testnet keep Autopilot. (btcpay-setup.sh only accepts
mainnet, testnet and regtest, so those are the only three networks this can see; the rule is written
as litd's own, which also covers signet and testnet4 should BTCPay ever allow them.)

`LIT_AUTO_MIGRATE_TO_SQL` is not set either: litd has defaulted to SQLite since v0.17 and only
prompts when it finds legacy kvdb files, so a fresh install never sees that prompt. If you are
switching from a long-lived upstream install that predates v0.17, litd will stop at the migration
prompt on first start — its log says exactly which flag approves it.

If you already run the stock fragment, the plugin detects it and offers to switch.

## How it talks to litd

The generated fragment mounts litd's data directory read-only into the BTCPay Server container:

```yaml
services:
  btcpayserver:
    volumes:
      - "lnd_lit_datadir:/lit:ro"
```

That one line is what the whole plugin rests on. From it the plugin reads litd's TLS certificate,
its macaroon and its log file, and then speaks gRPC to `lnd_lit:8443` over the deployment's internal
Docker network — the same trust anchors `litcli` uses, from inside the same network. So status,
sub-server health, LNC sessions and logs all work with **no** access to the host.

litd's certificate is self-signed and its SANs never cover the Compose service name, so the plugin
pins the certificate by value rather than validating it by name.

## Why installing is a copy-paste step

Adding or removing a service rebuilds the Docker Compose stack, which restarts BTCPay Server itself.
The host already has the right tool for it — `btcpay-fragments add|remove|show` — but a plugin cannot
reach it.

Since BTCPay Server 2.4.4, a plugin's only channel to the host is `btcpay-host`, and its host-side
script accepts a fixed whitelist:

```bash
allowed_commands=(env help changedomain update clean restart)
```

There is no fragment command in it, and the SSH key the container holds is pinned to a forced command,
so there is no way around it either. The plugin therefore renders the exact commands — fragment YAML
included — for you to paste into a root shell on the host, and detects the result afterwards. If
btcpayserver-docker ever exposes fragment management through `btcpay-host`, this becomes one click.

## Install

Grab a `.btcpay` from the releases, or build one:

```bash
git clone --recurse-submodules https://github.com/lightninglabs/terminal-btcpay
cd terminal-btcpay
./scripts/build-plugin.sh
```

Then upload `packaged/BTCPayServer.Plugins.LightningTerminal/<version>/BTCPayServer.Plugins.LightningTerminal.btcpay`
via **Server Settings → Plugins → Upload**, or drop it into your BTCPay data directory's `Plugins/`
folder, and restart BTCPay Server.

The package is large (~18 MB) because `CopyLocalLockFileAssemblies` drags BTCPay's whole dependency
graph into the publish output. Those assemblies are already loaded by the host; dropping them is a
size optimisation nobody has made safe yet.

### Requirements

- BTCPay Server **2.4.4 or newer**, on a btcpayserver-docker deployment.
- `BTCPAYGEN_LIGHTNING=lnd` — the bundled LND. The plugin refuses to install litd against Core
  Lightning, Eclair, phoenixd, an external LND, or no Lightning node at all, because litd is wired to
  `lnd_bitcoin:10009` on the internal Docker network and cannot reach anything else.

## Connecting to Terminal

**Connect to Terminal** mints a fresh admin Lightning Node Connect session and opens Terminal on the
web with the pairing phrase already filled in. Node and browser meet through litd's mailbox server, so
your node never has to be reachable from the internet. The phrase travels in the URL *fragment*, so it
stays in the browser and is never sent to Terminal's web server.

**Generate pairing phrase** is the manual counterpart: pick the label, type (Admin, Read-Only or
Custodial), lifetime and mailbox server, and it hands you the phrase for any LNC client. A Custodial
session is scoped to one of litd's accounts, which the form lists from litd directly. Custom sessions
are not offered — litd rejects that type unless the request carries explicit macaroon permissions, so
it would need a permissions editor to be worth anything.

A phrase pairs one client, once. Sessions are listed on the plugin page with their state, and can be
revoked there.

These are **admin** sessions: whoever holds the phrase can move funds and manage channels. The whole
page is gated on `CanModifyServerSettings` for that reason.

## Uninstall and wipe

- **Uninstall** deselects the fragment and regenerates the stack without litd. Nothing removes a named
  Compose volume, so `lnd_lit_datadir` — the macaroon, accounts, sessions, Loop/Pool/Faraday history —
  survives, and re-installing later picks it straight back up.
- **Wipe** removes litd and then destroys that volume. Irreversible.

Neither touches LND itself, its channels, or its funds: those live in a different volume.

## Development

```bash
git submodule update --init --recursive   # pins BTCPay Server, built against as a ProjectReference
dotnet build                              # compiles C# and Razor views
dotnet test                               # fragment, pairing-URL, path, backend-detection and plugin-convention tests
./scripts/plugin-register.sh              # load the plugin in a local BTCPay debug session
./scripts/build-plugin.sh                 # package a .btcpay
./scripts/check-proto-drift.sh            # fail if the vendored protos have gone stale
```

### Layout

| Path | What it is |
| ---- | ---------- |
| `Services/LitdFragment.cs` | The generated Compose fragment and the host commands around it |
| `Services/LitdClient.cs` | gRPC to litd: status, LND state, sessions |
| `Services/LitdPaths.cs` | Resolving the certificate, macaroon and log inside the mount |
| `Services/LightningBackendDetector.cs` | The install gate — is this the bundled LND? |
| `Services/TerminalConnect.cs` | litd's pairing-link encoding, reproduced |
| `Protos/` | Vendored `.proto` files; see `Protos/VENDORED_COMMIT` |

### Validating a change to the fragment

A broken fragment does not fail the build — it fails somebody's deployment. `dotnet test` covers its
structure; to check it end-to-end against the real generator:

```bash
git clone --depth 1 https://github.com/btcpayserver/btcpayserver-docker /tmp/bpsd
# write LitdFragment.Yaml to:
#   /tmp/bpsd/docker-compose-generator/docker-fragments/opt-add-lightning-terminal-headless.custom.yml
cd /tmp/bpsd/docker-compose-generator
BTCPAYGEN_CRYPTO1=btc BTCPAYGEN_REVERSEPROXY=nginx BTCPAYGEN_LIGHTNING=lnd \
  BTCPAYGEN_ADDITIONAL_FRAGMENTS=opt-add-lightning-terminal-headless.custom \
  dotnet run --project src/docker-compose-generator.csproj -c Release --no-launch-profile
docker compose -f /tmp/bpsd/Generated/docker-compose.generated.yml config --quiet
```

### Bumping litd

`LitdFragment.Image` pins the release. Keep it in step with btcpayserver-docker's own pin in
`opt-add-lightning-terminal.yml`, dropping the `-path-prefix` suffix — that rebuild exists only to
serve the web UI, which this fragment turns off.

Note that litd 0.17 migrates its database from bbolt to SQL on first start, and that migration is not
reversible. Back up `lnd_lit_datadir` before bumping across it.
# terminal-btcpay
