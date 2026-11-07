# Setting up the Quartermaster relay

Online games go through a small relay program that you run on any server you control (a VPS, a home
server, a spare PC). Players connect out to the relay, so nobody has to open ports on their own router.
The relay lists lobbies per module and passes encrypted game messages between players; it cannot read
games that have a password.

What you need:

- A machine that is reachable from the internet (a public IP address or a domain name).
- One open TCP port. The default is **47820**.
- Docker (recommended) or the .NET 10 runtime.

Players enter the relay's address in the launcher's **Online lobby** (`Relay:` field), for example
`relay.example.org:47820`.

---

## 1. Run it with Docker (recommended)

### 1.1 Install Docker

Install Docker Engine with the Compose plugin: <https://docs.docker.com/engine/install/>.
On Windows or macOS, Docker Desktop works too. Check with:

```sh
docker --version
docker compose version
```

### 1.2 Get the relay folder

Copy the Quartermaster source to the server (clone the repository or copy the folder). The Docker
files are in `src/Quartermaster.Relay/`:

```sh
cd Quartermaster/src/Quartermaster.Relay
```

The image is built from the repository root, so keep the folder structure intact (`Directory.Build.props`,
`src/Quartermaster.Net` and `src/Quartermaster.Relay` are needed).

### 1.3 Create the data folder

The relay keeps its certificate (and, later, turn-file mailboxes) in `./data`, mounted at `/data` in the
container. The container runs as a non-root user with user id **1654**, so give that user the folder:

```sh
mkdir -p data
sudo chown 1654:1654 data
```

(Docker Desktop on Windows/macOS does not need the `chown`.)

### 1.4 Optional: use your own certificate

By default the relay creates a **self-signed certificate** on its first start and prints its fingerprint.
Players' copies remember that fingerprint the first time they connect and warn loudly if it ever changes.
This is enough for playing with friends.

If you have a certificate for your domain (for example from Let's Encrypt), copy the files into `./data`
and set them in `docker-compose.yml`:

```yaml
    environment:
      Relay__CertificatePath: "/data/fullchain.pem"
      Relay__CertificateKeyPath: "/data/privkey.pem"
```

A `.pfx`/`.p12` file works too: set `Relay__CertificatePath: "/data/relay.pfx"` and
`Relay__CertificatePassword` to its password. Make sure user id 1654 can read the files.

### 1.5 Start it

```sh
docker compose up -d
```

The relay restarts automatically after a crash or a reboot (`restart: unless-stopped`).

### 1.6 Open the port

Allow TCP port 47820 in the server's firewall and in your hosting provider's firewall/security group.
For example with `ufw`:

```sh
sudo ufw allow 47820/tcp
```

To use another public port, change the left side of `ports:` in `docker-compose.yml`
(e.g. `"443:47820"`) and give players that port.

### 1.7 Read the fingerprint

```sh
docker compose logs relay
```

Look for the line:

```
Relay certificate fingerprint: C08394C70A80F345...
```

Send this fingerprint to your players through a channel you trust (chat, e-mail). When their copy
connects for the first time it shows the fingerprint; they should check it matches.
The fingerprint stays the same as long as `./data` is kept.

### 1.8 Update

After getting a newer Quartermaster version (e.g. `git pull` in the repository):

```sh
docker compose build
docker compose up -d
```

If you publish the image to your own registry instead of building on the server, use
`docker compose pull` followed by `docker compose up -d`.

### 1.9 Other CPU types

The image runs on `linux/amd64` and `linux/arm64` (e.g. ARM VPS, Raspberry Pi 4/5). Building on the
server itself picks the right one. To build both at once on another machine:

```sh
docker buildx build --platform linux/amd64,linux/arm64 \
  -f src/Quartermaster.Relay/Dockerfile -t your-registry/quartermaster-relay:latest --push .
```

(run from the repository root).

---

## 2. Run it as a plain program (without Docker)

### 2.1 Publish

On any machine with the .NET 10 SDK, from the repository root:

```sh
dotnet publish src/Quartermaster.Relay/Quartermaster.Relay.csproj -c Release \
  -r linux-x64 --self-contained true -o relay-out
```

Use `-r linux-arm64`, `-r win-x64` or `-r osx-arm64` for other systems. A self-contained publish needs
no .NET installed on the server. Copy the `relay-out` folder to the server, e.g. to `/opt/quartermaster-relay`.

### 2.2 Settings

Settings are read from `appsettings.json` next to the program, or from environment variables
(`Relay__Port`, `Relay__DataDir`, `Relay__CertificatePath`, `Relay__CertificateKeyPath`,
`Relay__CertificatePassword`, `Relay__MaxConnections`, `Relay__HostMessagesPerSecond`, `Relay__HostBurst`):

| Setting | Default | Meaning |
| --- | --- | --- |
| `Port` | `47820` | TCP port to listen on (TLS) |
| `DataDir` | `/data` | Folder for the generated certificate (must be writable) |
| `CertificatePath` | empty | Own certificate (`.pem` or `.pfx`); empty = self-signed |
| `CertificateKeyPath` | empty | Key file for a `.pem` certificate |
| `CertificatePassword` | empty | Password of a `.pfx` file |
| `MaxConnections` | `2000` | Most connections at once, from all addresses together |
| `HostMessagesPerSecond` | `400` | Message rate of a room's host connection (never below the 50 guests get) |
| `HostBurst` | `1000` | Burst size of a room's host connection (never below 200) |

Outside Docker, set `DataDir` to a real folder, e.g. `/var/lib/quartermaster-relay`.

### 2.3 Linux: systemd service

Create a user and a data folder:

```sh
sudo useradd --system --no-create-home --shell /usr/sbin/nologin qmrelay
sudo mkdir -p /var/lib/quartermaster-relay
sudo chown qmrelay:qmrelay /var/lib/quartermaster-relay
```

Create `/etc/systemd/system/quartermaster-relay.service`:

```ini
[Unit]
Description=Quartermaster relay
After=network-online.target
Wants=network-online.target

[Service]
User=qmrelay
WorkingDirectory=/opt/quartermaster-relay
ExecStart=/opt/quartermaster-relay/Quartermaster.Relay
Environment=Relay__Port=47820
Environment=Relay__DataDir=/var/lib/quartermaster-relay
Restart=always
RestartSec=5
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ReadWritePaths=/var/lib/quartermaster-relay

[Install]
WantedBy=multi-user.target
```

Then:

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now quartermaster-relay
sudo journalctl -u quartermaster-relay | grep fingerprint
```

Open the port in the firewall as in 1.6.

### 2.4 Windows

Publish with `-r win-x64`, set `Relay__DataDir` (e.g. `C:\QuartermasterRelay\data`) and run
`Quartermaster.Relay.exe`. To keep it running, register it as a service (for example with
`sc.exe create` or a tool such as NSSM) or as a scheduled task that starts at boot. Allow the port in
Windows Defender Firewall:

```powershell
New-NetFirewallRule -DisplayName "Quartermaster relay" -Direction Inbound -Protocol TCP -LocalPort 47820 -Action Allow
```

---

## 3. What the relay does and does not keep

- **Lobby listings** are public: lobby name, host name, module title and hash, side names, free sides,
  player counts, locked flag and version. Nothing else from the game.
- **Game messages** are end-to-end encrypted with a key derived from the lobby password; the relay only
  passes them on and never stores or logs them. **Open lobbies (no password)** use a key anyone who
  knows the lobby code can derive, so the relay could read those games.
- All guests of a game share one game key. Each guest still receives only the view of their own
  sides from the host, but a guest-to-guest message is only kept private by the relay routing it
  honestly. Run your own relay and you control that.
- Plainly: **a guest who also runs the relay can read the other guests' views** (they hold the shared
  game key and see every message pass). Only play with a relay run by the host or by someone none of
  the guests is.
- Messages are compressed before they are encrypted, and the game log the host sends contains both
  chat (which players type) and private data. Someone who can chat in the game *and* watch the size of
  the encrypted messages on the relay could, in theory, learn a little about the private data from how
  well it compresses. This is a theoretical side channel, not a practical one; a relay you trust (or run
  yourself) closes it.
- The relay stores a hash of the password check value, never the password. The host's copy also
  holds a private **host key** (random, never shown); only the copy with that key can take the host's
  place again after a dropped connection, so a guest who knows the password cannot take over the game.
- Limits:
  - messages up to 8 MB (4 KB before a connection has introduced itself; a guest's view of the game is
    compressed and may unpack to up to 64 MB as long as it fits in one message; a larger one pauses that
    guest with "The game is too large to send."), 50 messages per second per
    connection (bursts of 200). A room's host gets a larger budget because it sends a snapshot to
    every guest: 400 per second, bursts of 1000 (`HostMessagesPerSecond`, `HostBurst`). Anyone who
    creates a room gets the host budget, not only the game's real host; a relay you run for friends
    is fine with that. A connection that does not introduce itself within 10 seconds is closed;
  - 32 connections at once per address and 2000 in total (`MaxConnections`);
  - 20 lobbies per address. At the limit, the oldest lobby from that address whose host has gone is
    closed to make space; if every one still has its host, a new lobby is refused;
  - 7 places per game, the host's place included (it stays reserved while the host is away). A full game
    and the lobby limit are answered with a message; the connection stays open;
  - lobby names longer than 40 characters are cut to 40.
- A lobby disappears from the list 90 seconds after its host stopped refreshing it, or when the host
  disconnects. A game whose host is away for 24 hours closes.
- Per-address limits treat an IPv6 address by its /64 network (one home or one server).
- If the relay sits behind a reverse proxy, all players appear to come from the proxy's address and
  share the per-address limits; connecting directly is recommended.
