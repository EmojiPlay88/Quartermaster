# Hosting a Relay

Online games in Quartermaster go through a small program called the **relay**. Somebody has to run it, and that somebody is you if you are reading this page. If a friend already runs one for your group, you do not need this page: you only need their address (see [Playing With Others](Playing-With-Others)).

The relay lists lobbies per module and passes encrypted game messages between players; it cannot read games that have a password. Players connect **out** to the relay, so nobody except the person running the relay has to touch a router. The relay program runs on Windows, macOS and Linux (the Quartermaster desktop app itself is Windows-only today).

Players type the relay's address into the launcher's **Online lobby** (`Relay:` field), for example `relay.example.org:47820`.

## 1. What you need

- A computer that stays on while people play: a rented server (a "VPS"), a home server, a spare PC or a Raspberry Pi 4/5.
- A way for players to reach it from the internet: a public IP address or a domain name.
- **One open TCP port.** The default is **47820**.
- Either Docker (recommended on Linux servers), or just the downloadable relay program, which needs nothing else installed.

### Home PC or rented server?

- **Rented server (VPS):** has a public address already, stays on, no router to configure. Any small plan is enough. Easiest for players.
- **Home PC or home server:** free, but you must set up **port forwarding** on your router (section 4), and your home IP address may change from time to time (a free "dynamic DNS" name solves that). The relay only works while the computer is on.

Either way, only the one relay port needs to be reachable from outside.

## 2. Pick the download

Each release on <https://github.com/EmojiPlay88/Quartermaster/releases> has the relay as a separate download from the app. Pick the one for the server (replace `0.1.0` with the release you want):

| System | File |
| --- | --- |
| Linux, 64-bit Intel/AMD | `QuartermasterRelay-0.1.0-linux-x64.tar.gz` |
| Linux, ARM (Raspberry Pi 4/5, ARM VPS) | `QuartermasterRelay-0.1.0-linux-arm64.tar.gz` |
| Windows, 64-bit | `QuartermasterRelay-0.1.0-win-x64.zip` |
| macOS, Apple silicon | `QuartermasterRelay-0.1.0-osx-arm64.zip` |

Each file has a matching `.sha256` next to it. The checksum only guards against a corrupt download; the files are not code-signed. Check it on Linux with `sha256sum -c QuartermasterRelay-0.1.0-linux-x64.tar.gz.sha256`, on Windows with `Get-FileHash <file> -Algorithm SHA256` (compare with the text in the `.sha256` file).

The builds are self-contained: no .NET needs to be installed on the server. If you prefer Docker instead, skip to section 6.

## 3. Run the downloaded relay

### Linux

```sh
sudo mkdir -p /opt/quartermaster-relay /var/lib/quartermaster-relay
sudo chown "$USER": /var/lib/quartermaster-relay
sudo tar -xzf QuartermasterRelay-0.1.0-linux-x64.tar.gz -C /opt/quartermaster-relay
sudo chmod +x /opt/quartermaster-relay/Quartermaster.Relay
Relay__DataDir=/var/lib/quartermaster-relay /opt/quartermaster-relay/Quartermaster.Relay
```

### Windows

Unzip the archive, set `Relay__DataDir` first (see section 5), and run `Quartermaster.Relay.exe`. In PowerShell, for example:

```powershell
$env:Relay__DataDir = "C:\QuartermasterRelay\data"
New-Item -ItemType Directory -Force $env:Relay__DataDir
.\Quartermaster.Relay.exe
```

To keep it running after you log out, register it as a service (for example with `sc.exe create` or a tool such as NSSM) or as a scheduled task that starts at boot.

### macOS

Unzip, run `chmod +x Quartermaster.Relay` and start `./Quartermaster.Relay`. The first start may need System Settings, Privacy & Security, Open Anyway because the file is unsigned.

### Check that it started

The relay prints two lines you care about: `Relay certificate fingerprint: ...` and `Relay listening on port 47820`. Section 8 says what to do with the fingerprint.

The relay listens on all interfaces by default; set `Relay__BindAddress=loopback` to accept only local connections (for example behind a reverse proxy), see the settings table in section 5. For a service, use the systemd unit in section 7 with `ExecStart` pointing at the extracted `Quartermaster.Relay` (keep the `chmod +x` above, or the service fails with "permission denied"). The data folder must belong to the user that runs the relay: in the example above that is you, and section 7 gives it to `qmrelay` instead.

## 4. Let players reach it: router and firewall

Skip the router part if you rent a server. Do the firewall part on every machine.

### Port forwarding on a home router (generic steps)

Your router normally blocks connections coming in from the internet. Port forwarding tells it to pass one port to your relay computer. Every router's menu looks different, but the steps are the same:

1. Give the relay computer a fixed address on your home network, so the forwarding does not break when it reboots. Look for "DHCP reservation" or "static lease" in the router, or set a fixed address on the computer.
2. Open the router's admin page in a browser (often `192.168.0.1` or `192.168.1.1`; the address and password are often printed on the router).
3. Find the section called "Port forwarding", "Virtual server", "NAT" or "Applications and gaming".
4. Add a rule: protocol **TCP**, external port **47820**, internal port **47820**, internal address = the relay computer's address from step 1.
5. Save. Then give your players your home **public** IP address (search "what is my IP" in a browser) or a dynamic-DNS name, plus the port, for example `203.0.113.7:47820`.

If forwarding does not seem to work, your internet provider may share one public address between many customers (called CGNAT). Ask them for a public address, or rent a small server instead.

### Firewall

Allow **inbound TCP 47820** on the relay machine, and open only that port. On a rented server also check your hosting provider's own firewall or "security group" page.

Linux with `ufw`:

```sh
sudo ufw allow 47820/tcp
```

Linux with `firewalld`:

```sh
sudo firewall-cmd --permanent --add-port=47820/tcp
sudo firewall-cmd --reload
```

Windows (Windows Defender Firewall), in an administrator PowerShell:

```powershell
New-NetFirewallRule -DisplayName "Quartermaster relay" -Direction Inbound -Protocol TCP -LocalPort 47820 -Action Allow
```

To use another public port, change `Relay__Port` (and, with Docker, the left side of `ports:`, section 6.5) and give players that port, for example `relay.example.org:443`.

## 5. Settings

Settings are read from `appsettings.json` next to the program, or from environment variables (`Relay__Port`, `Relay__BindAddress`, `Relay__DataDir`, `Relay__CertificatePath`, `Relay__CertificateKeyPath`, `Relay__CertificatePassword`, `Relay__MaxConnections`, `Relay__HostMessagesPerSecond`, `Relay__HostBurst`, `Relay__MailboxMaxBytes`, `Relay__MailboxMaxItems`, `Relay__MailboxExpiryDays`). The same variables work in `docker-compose.yml` under `environment:`.

| Setting | Default | Meaning |
| --- | --- | --- |
| `Port` | `47820` | TCP port to listen on (TLS) |
| `BindAddress` | `any` | Interface to listen on: `any` (all interfaces), `loopback` (127.0.0.1 only) or an IP address; env var `Relay__BindAddress` |
| `DataDir` | `/data` | Folder for the generated certificate and the turn mailbox (must be writable) |
| `CertificatePath` | empty | Own certificate (`.pem` or `.pfx`); empty = self-signed |
| `CertificateKeyPath` | empty | Key file for a `.pem` certificate |
| `CertificatePassword` | empty | Password of a `.pfx` file |
| `MaxConnections` | `2000` | Most connections at once, from all addresses together |
| `HostMessagesPerSecond` | `400` | Message rate of a room's host connection (never below the 50 guests get) |
| `HostBurst` | `1000` | Burst size of a room's host connection (never below 200) |
| `MailboxMaxBytes` | `1073741824` (1 GiB) | Most bytes the turn mailbox keeps, all games together (a turn counts at least 4 KB and each box 8 KB more); when full, new turns are refused |
| `MailboxMaxItems` | `10000` | Most turns the mailbox keeps, all games together; when reached, new turns are refused |
| `MailboxExpiryDays` | `30` | Days a turn waits in the mailbox before it is deleted |

Outside Docker, set `DataDir` to a real folder, e.g. `/var/lib/quartermaster-relay` or `C:\QuartermasterRelay\data`.

## 6. Run it with Docker (recommended on Linux servers)

### 6.1 Install Docker

Install Docker Engine with the Compose plugin: <https://docs.docker.com/engine/install/>. On Windows or macOS, Docker Desktop works too. Check with:

```sh
docker --version
docker compose version
```

### 6.2 Get the relay folder

Copy the Quartermaster source to the server (clone the repository or copy the folder). The Docker files are in `src/Quartermaster.Relay/`:

```sh
cd Quartermaster/src/Quartermaster.Relay
```

The image is built from the repository root, so keep the folder structure intact (`Directory.Build.props`, `src/Quartermaster.Net` and `src/Quartermaster.Relay` are needed).

### 6.3 Create the data folder

The relay keeps its certificate and the turn mailbox (section 10) in `./data`, mounted at `/data` in the container. The container runs as a non-root user with user id **1654**, so give that user the folder:

```sh
mkdir -p data
sudo chown 1654:1654 data
```

(Docker Desktop on Windows/macOS does not need the `chown`.)

### 6.4 Optional: use your own certificate

By default the relay creates a **self-signed certificate** on its first start and prints its fingerprint. Players' copies remember that fingerprint the first time they connect and warn loudly if it ever changes. This is enough for playing with friends.

If you have a certificate for your domain (for example from Let's Encrypt), copy the files into `./data` and set them in `docker-compose.yml`:

```yaml
    environment:
      Relay__CertificatePath: "/data/fullchain.pem"
      Relay__CertificateKeyPath: "/data/privkey.pem"
```

A `.pfx`/`.p12` file works too: set `Relay__CertificatePath: "/data/relay.pfx"` and `Relay__CertificatePassword` to its password. Make sure user id 1654 can read the files.

### 6.5 Start it

```sh
docker compose up -d
```

The relay restarts automatically after a crash or a reboot (`restart: unless-stopped`).

Open the port as described in section 4. To use another public port, change the left side of `ports:` in `docker-compose.yml` (e.g. `"443:47820"`) and give players that port.

### 6.6 Read the fingerprint

```sh
docker compose logs relay
```

Look for the line `Relay certificate fingerprint: C08394C70A80F345...` and see section 8.

### 6.7 Update

After getting a newer Quartermaster version (e.g. `git pull` in the repository):

```sh
docker compose build
docker compose up -d
```

If you publish the image to your own registry instead of building on the server, use `docker compose pull` followed by `docker compose up -d`.

### 6.8 Other CPU types

The image runs on `linux/amd64` and `linux/arm64` (e.g. ARM VPS, Raspberry Pi 4/5). Building on the server itself picks the right one. To build both at once on another machine:

```sh
docker buildx build --platform linux/amd64,linux/arm64 \
  -f src/Quartermaster.Relay/Dockerfile -t your-registry/quartermaster-relay:latest --push .
```

(run from the repository root).

## 7. Linux: systemd service

For the downloaded program, a service starts it at boot and restarts it if it crashes. Create a user and a data folder:

```sh
sudo useradd --system --no-create-home --shell /usr/sbin/nologin qmrelay
sudo mkdir -p /var/lib/quartermaster-relay
sudo chown qmrelay:qmrelay /var/lib/quartermaster-relay
```

The `chown` matters: the service runs as `qmrelay`, which must own the data folder or the relay cannot write its certificate. Create `/etc/systemd/system/quartermaster-relay.service`:

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

Open the port in the firewall as in section 4.

### Build it yourself (optional)

On any machine with the .NET 10 SDK, from the repository root:

```sh
dotnet publish src/Quartermaster.Relay/Quartermaster.Relay.csproj -c Release \
  -r linux-x64 --self-contained true -o relay-out
```

Use `-r linux-arm64`, `-r win-x64` or `-r osx-arm64` for other systems. A self-contained publish needs no .NET installed on the server. Copy the `relay-out` folder to the server, e.g. to `/opt/quartermaster-relay`.

## 8. Check that it works, and share the fingerprint

1. On the relay machine, look for `Relay listening on port 47820` in the log (Docker: `docker compose logs relay`; systemd: `sudo journalctl -u quartermaster-relay`).
2. From **another** network (a phone on mobile data is ideal; not the relay machine, and not your own home Wi-Fi if you host at home), test that the port is reachable. On Windows PowerShell: `Test-NetConnection relay.example.org -Port 47820`; on Linux/macOS: `nc -vz relay.example.org 47820`. "TcpTestSucceeded : True" or "succeeded" means the port is open.
3. In Quartermaster open **Online lobby**, enter `relay.example.org:47820` in the `Relay:` field and connect. The first time, the app shows the relay's certificate **fingerprint**.

The fingerprint is printed in the relay log (`Relay certificate fingerprint: C08394C70A80F345...`). Send it to your players through a channel you trust (chat, e-mail). When their copy connects for the first time it shows the fingerprint; they should check it matches. The fingerprint stays the same as long as the data folder (`./data` or `DataDir`) is kept.

## Keeping it safe

**Security advice, please read:**

- **Use lobby passwords.** Games with a password are end to end encrypted and the relay cannot read them. **Open lobbies (no password) can be read by the relay**, and an open lobby has no password to stop anyone: lobby listings are public, so anyone who can reach your relay can see it in the list and join it. A lobby with a password needs that password to join. Tell your players to set a password on every lobby.
- **Open only the relay port** (47820 or the one you chose). Do not forward or open anything else on that machine, and do not run services you do not need on it.
- **Keep it updated.** Install new relay versions when they are released (section 11) and keep the operating system updated.
- **Keep the data folder** and back it up if you use the mailbox: it holds the certificate your players trust and the waiting turns.
- Run the relay for people you know. Section 9 explains what a relay operator can and cannot see.

## 9. What the relay does and does not keep

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


## 10. Turn mailbox

Players who take turns by turn file (Game → Send Turn…) can let the relay hold the file until the other
player's Quartermaster picks it up, instead of sending it by e-mail or chat.

- **Encrypted end to end.** Each turn is encrypted on the sender's computer with a key made from the game's
  private id (which only the players' copies hold) and the game password, if any. The relay stores the
  encrypted bytes only and cannot read them. The encryption is bound to the box it was sent to: a turn copied
  into another box does not open there.
- **Boxes have opaque names.** A box is named by a 64-character code derived from the same secrets and the
  receiving player; the relay never sees game names, game ids, player names or passwords. Anyone who knows
  a box's code can list, fetch and delete what is in it, which is why the code is derived from secrets.
- **Where it is stored:** `DataDir/mailbox/<box>/` holds one `.bin` file per waiting turn and a small
  `index.json`. On Linux the folders are created readable by the relay's user only (0700) and the files
  0600. The mailbox survives restarts; back up or move `./data` to keep it.
- **Limits:**
  - one turn must fit in one relay message (about 6 MiB once encrypted); a larger one is refused by the game
    before it is sent;
  - 50 waiting turns per box;
  - per address (IPv6 by /64): 30 upload attempts per hour — refused attempts count too — and 100 MiB of
    stored turns per 24 hours; beyond that, "Too many uploads; try again later.";
  - `MailboxMaxBytes` in total (default 1 GiB) and `MailboxMaxItems` turns in total (default 10,000). For the
    byte total every turn counts at least 4 KB, and each box 8 KB more, so many tiny uploads cannot fill
    the disk or the relay's memory. Expired turns are cleared first; if it is still full, new turns are
    refused with "The mailbox is full.";
  - turns are deleted after `MailboxExpiryDays` days (default 30), checked every hour, or when the
    receiving player's copy deletes them after replaying them (only once the replayed game has been
    autosaved; that copy also keeps the turn locally until its player sends the next turn).
- **Size it to your server.** The per-address limits slow a stranger down but do not stop a few addresses
  from filling the mailbox and keeping it full until their uploads expire. Set `MailboxMaxBytes` to what
  the disk can spare, and run the relay for people you know. Players' copies delete turns once they have
  been played, so normal use keeps the mailbox small.
- The relay log never names boxes or sizes per box; it only reports how many turns are kept and how many
  expired.
- To empty the mailbox, stop the relay and delete `DataDir/mailbox`.

## 11. Updating the relay

**Downloaded program:** stop it (for the service: `sudo systemctl stop quartermaster-relay`), download the new archive, extract it over the same folder (keep the data folder, it holds the certificate and the fingerprint players already trust), start it again (`sudo systemctl start quartermaster-relay`).

**Docker:** `docker compose pull && docker compose up -d` if you use a published image, otherwise `docker compose build && docker compose up -d` after pulling the new source (section 6.7).

The app and the relay check that they speak the same network version, and the relay refuses an app with a different one (the message says "update both copies"). That number does not change with every release, so a newer app does not always need a newer relay, but updating both together is the simplest way to stay safe.
