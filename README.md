# terminal-btcpay

A BTCPay Server plugin that installs and manages [Lightning Terminal](https://github.com/lightninglabs/lightning-terminal)
(`litd`) on a btcpayserver-docker deployment, and replaces litd's own web UI.

Under **Plugins → Lightning Terminal**, a server administrator can see whether litd is installed and
healthy, install it against BTCPay's bundled LND, pair a browser with the node through
[Terminal on the web](https://terminal.lightning.engineering), download litd's logs, and remove it
again — keeping its data or wiping it.

Installing this plugin does **not** install litd. That is a separate, deliberate step.

## How litd gets installed

BTCPay Server runs litd through btcpayserver-docker's
[`opt-add-lightning-terminal`](https://docs.btcpayserver.org/Docker/lightning-terminal/) fragment.
Installing it is one command, run as root on the BTCPay Server host:

```bash
btcpay-fragments add opt-add-lightning-terminal
```

That fragment runs litd headless (`--disableui`) and mounts its data volume read-only into the BTCPay
Server container, which is what lets this plugin read litd's certificate and macaroon and reach it
over TLS gRPC on `lnd_lit:8443`. There is no web UI, so there is no password to store or rotate and
nothing published on your public BTCPay hostname — you manage the node from this plugin, or from
Terminal on the web over Lightning Node Connect.

This plugin generates no fragment of its own. How litd is run, and which release it pins, are decided
in `docker-fragments/opt-add-lightning-terminal.yml` in btcpayserver-docker.

### If litd was installed the old way

Earlier versions of that fragment served litd's web UI at `/lit/` behind a generated password, and did
not mount litd's data volume into the BTCPay Server container. That leaves the plugin with no
`lit.macaroon` to read and no TLS port it can reach, so it cannot talk to litd at all.

The plugin detects this and says so on its own page rather than offering to install litd a second
time. The signal is free: the superseded fragment registers its web UI in `BTCPAY_EXTERNALSERVICES`
and the current one registers nothing, so that entry without the mount means exactly one thing. The
fix is to update BTCPay Server:

```bash
btcpay-update.sh
```

Both versions declare litd's data volume under the same name, so updating replaces the fragment
without touching litd's accounts, sessions or history.

## How it talks to litd

The fragment mounts litd's data volume read-only into the BTCPay Server container:

```yaml
services:
  btcpayserver:
    volumes:
      - "lnd_lit_datadir:/lit:ro"
```

That one line is what the whole plugin rests on. The volume holds a directory per litd-family daemon
— `.lit`, `.loop`, `.pool`, `.faraday`, `.tapd` — and from `.lit` the plugin reads litd's TLS
certificate, its macaroon and its log file, then speaks gRPC to `lnd_lit:8443` over the deployment's
internal Docker network — the same trust anchors `litcli` uses, from inside the same network. So status,
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
so there is no way around it either. The plugin therefore renders the exact command for you to paste
into a root shell on the host, and detects the result afterwards. If btcpayserver-docker ever exposes
fragment management through `btcpay-host`, this becomes one click.

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

## Accounts

An account caps how much can be spent through it — a ceiling, not a transfer, so nothing leaves the
node until the account actually pays something. Pair one with a Custodial session to hand someone a
budget rather than your node.

**Create new account** takes a label (optional, unique when set), a starting balance in satoshis, and
an expiry date that defaults to never. Each account's page shows what is left of its balance, its
payment history and the hashes of any invoices it created.

**Reveal macaroon** shows an account's hex macaroon. litd hands one over exactly once, in
`CreateAccountResponse`, and has no RPC to fetch it again — so this re-derives it rather than storing
it, following litd's own recipe: bake against a root key derived from the account's ID with litd's
account permission set (`lncli bakemacaroon`), then narrow the result with the
`lnd-custom account <id>` caveat (`lncli restrictmacaroon`). The bake goes to the bundled LND over
gRPC; btcpayserver-docker already mounts its data directory into the BTCPay container, so no fragment
change is needed.

> litd's own `BakeSuperMacaroon` RPC takes the same root key suffix and looks like the shortcut for
> this. It is not. It bakes with `permsMgr.ActivePermissions` and no caveats, and an account is scoped
> by its caveat alone — so it returns a full-node super macaroon that litd accepts happily.

Two things litd's own semantics dictate in that view:

- A payment's **reserved** amount includes the fee limit it set aside, and litd notes the actual debit
  is usually lower — so it is not labelled as the amount paid.
- litd records **only the payment hash** for an account's invoices: no amount, no state.

These are **admin** sessions: whoever holds the phrase can move funds and manage channels. The whole
page is gated on `CanModifyServerSettings` for that reason.

## Uninstall and wipe

- **Uninstall** deselects the fragment and regenerates the stack without litd. Nothing removes a named
  Compose volume, so `lnd_lit_datadir` — the macaroon, accounts, sessions, Loop/Pool/Faraday history —
  survives, and re-installing later picks it straight back up.
- **Wipe** keeps the fragment selected but destroys that volume and brings litd back empty. The
  container has to go first — a volume cannot be removed while something is attached to it — and
  `btcpay-up.sh` recreates litd afterwards. Irreversible.

Neither touches LND itself, its channels, or its funds: those live in a different volume.

## Development

```bash
git submodule update --init --recursive   # pins BTCPay Server, built against as a ProjectReference
dotnet build                              # compiles C# and Razor views
dotnet test                               # host-command, pairing-URL, path, backend-detection and plugin-convention tests
./scripts/plugin-register.sh              # load the plugin in a local BTCPay debug session
./scripts/build-plugin.sh                 # package a .btcpay
./scripts/check-proto-drift.sh            # fail if the vendored protos have gone stale
```

### Layout

| Path | What it is |
| ---- | ---------- |
| `Services/LitdFragment.cs` | The host commands: install, update, uninstall, wipe |
| `Services/LitdClient.cs` | gRPC to litd: status, LND state, sessions |
| `Services/LitdPaths.cs` | Resolving the certificate, macaroon and log inside the mount |
| `Services/LightningBackendDetector.cs` | The install gate — is this the bundled LND? |
| `Services/TerminalConnect.cs` | litd's pairing-link encoding, reproduced |
| `Protos/` | Vendored `.proto` files; see `Protos/VENDORED_COMMIT` |

### The fragment lives in btcpayserver-docker

This plugin generates no Compose fragment. litd is installed by selecting btcpayserver-docker's own
`opt-add-lightning-terminal`, which runs litd headless and mounts its data volume into the BTCPay
Server container — everything this plugin needs. Changes to how litd is run, and the litd release
itself, belong in `docker-fragments/opt-add-lightning-terminal.yml` there, and are validated against
that repository's own compose generator:

```bash
cd btcpayserver-docker/docker-compose-generator
BTCPAYGEN_CRYPTO1=btc BTCPAYGEN_REVERSEPROXY=nginx BTCPAYGEN_LIGHTNING=lnd \
  BTCPAYGEN_ADDITIONAL_FRAGMENTS=opt-add-lightning-terminal \
  dotnet run --project src/docker-compose-generator.csproj --no-launch-profile
```

What this plugin pins instead are the paths and names it reads through that fragment —
`TerminalOptions.DataVolumeName`, `ContainerName`, `LitDataDirectory` and `FragmentName`. If the
fragment renames any of them, the plugin stops finding litd, so they are covered by tests here.

### Bumping litd

The release is pinned in btcpayserver-docker, not here.

Note that litd 0.17 migrates its database from bbolt to SQL on first start, and that migration is not
reversible. Back up `lnd_lit_datadir` before bumping across it.
# terminal-btcpay
