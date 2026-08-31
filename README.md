# Asmo FRBox Telegram Bot

A Telegram bot version of the Asmo FRBox Downloader: search a firmware
model, get back the FRBox share link + extraction code, and optionally
unlock the share to grab a time-limited direct download link for any file
inside it (no huge files ever pass through Telegram itself).

Built in C#/.NET 8 on top of the same core logic as the WinForms app
(`FrBoxService.cs`, `LocalCatalogService.cs`, `RemoteZipReader.cs`,
`Models.cs`, `IFirmwareCatalog.cs` — copied over unchanged, just moved into
this project's namespace) plus [Telegram.Bot](https://github.com/TelegramBots/Telegram.Bot)
(v22.x API) for the Telegram side.

## How it works

1. User sends a model name, e.g. `CN6`, or `/search CN6`.
2. Bot searches the local firmware catalog (same JSON source/embedding
   scheme as the desktop app — see below) and shows matching versions as
   tappable buttons, paginated.
3. Tapping a version shows its details plus the FRBox share link and
   extraction code (copy-paste ready).
4. Optional: tap **"🔓 Unlock & list files"** to have the bot unlock the
   share via Transsion's official FRBox/Aliyun PDS API and list every file
   inside, each as a button.
5. Tapping a file mints a **secure, expiring (1h) direct download URL** for
   just that file and sends it to the user — the bot never downloads or
   re-uploads the firmware itself, so there's no file-size limit to worry
   about on Telegram's side.

The bot never calls any third-party catalog API — same "no third-party
catalog dependency" design as the desktop app. Only the firmware download
step still talks to Transsion's own infrastructure (`frbox.transsion.com` /
Aliyun PDS), which is unavoidable since that's where the files live.

## Setup

### 1. Get a bot token

Talk to [@BotFather](https://t.me/BotFather) on Telegram, run `/newbot`,
follow the prompts, and copy the token it gives you.

### 2. Provide a firmware catalog

Same three-tier scheme as the desktop app (`LocalCatalogService.cs`):

1. `firmware_catalog.json` next to the running app (or wherever
   `CATALOG_SOURCE` env var points), or
2. baked into the assembly at build time if `firmware_catalog.json` exists
   at the project root when you `dotnet publish`/`docker build`, or
3. nothing — bot still starts, searches just return 0 results.

See `firmware_catalog.sample.json` for the schema. You can also point
`CATALOG_SOURCE` at an `https://` URL you host yourself instead of a local
file.

**Don't commit your real `firmware_catalog.json`** if it contains private
share links/extraction codes — it's already in `.gitignore`.

### 3. Run it

```bash
export TELEGRAM_BOT_TOKEN=123456:ABC-your-token-here
# optional:
export CATALOG_SOURCE=/path/to/firmware_catalog.json   # or an https:// URL

dotnet restore
dotnet run
```

The bot uses long polling, so it doesn't need a public URL, HTTPS
certificate, or open port — it just needs outbound internet access to
`api.telegram.org`.

## Deploying

Long polling means you just need *any* host that can keep the process
running continuously — a small VPS, a free-tier box on Fly.io/Railway/
Render, a Raspberry Pi, etc. GitHub itself doesn't run long-lived
processes (Actions runners aren't meant for this), so plan to host the
running bot somewhere else and just keep the **code** in this GitHub repo.

### Docker

```bash
docker build -t asmo-frbox-bot .
docker run -d --name asmo-frbox-bot \
  -e TELEGRAM_BOT_TOKEN=123456:ABC-your-token-here \
  -e CATALOG_SOURCE=https://your-host/firmware_catalog.json \
  --restart unless-stopped \
  asmo-frbox-bot
```

### systemd (bare VPS)

```ini
# /etc/systemd/system/asmo-frbox-bot.service
[Unit]
Description=Asmo FRBox Telegram Bot
After=network.target

[Service]
WorkingDirectory=/opt/asmo-frbox-bot
ExecStart=/usr/bin/dotnet /opt/asmo-frbox-bot/AsmoFrBoxTelegramBot.dll
Environment=TELEGRAM_BOT_TOKEN=123456:ABC-your-token-here
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```

```bash
dotnet publish -c Release -o /opt/asmo-frbox-bot
sudo systemctl enable --now asmo-frbox-bot
```

### Fly.io / Railway / Render

Any of these can run the `Dockerfile` as-is as a "worker"/background
service (not a web service — it doesn't listen on a port). Set
`TELEGRAM_BOT_TOKEN` (and optionally `CATALOG_SOURCE`) as secrets/env vars
in their dashboard.

## Pushing this to GitHub

```bash
git init
git add .
git commit -m "Initial commit: Asmo FRBox Telegram bot"
git branch -M main
git remote add origin https://github.com/<you>/asmo-frbox-telegram-bot.git
git push -u origin main
```

`.github/workflows/build.yml` runs `dotnet build` on every push/PR so you
get a compile check for free.

## Project layout

```
Program.cs                   entry point, config, polling loop
BotService.cs                all Telegram command/callback handling
SearchSessionStore.cs        per-chat in-memory search state
IFirmwareCatalog.cs          catalog abstraction (unchanged)
LocalCatalogService.cs       default catalog source (unchanged)
Models.cs                    FirmwareEntry / ShareFile / SearchResult / SecureDownloadLink
FrBoxService.cs               Transsion FRBox / Aliyun PDS client (unchanged)
RemoteZipReader.cs            reads ZIP central directory over HTTP Range (unchanged)
firmware_catalog.sample.json  schema reference
Dockerfile
.github/workflows/build.yml
```

## Notes / limitations

- Search matches brand, project name, and version substrings (same logic
  as the desktop app's `LocalCatalogService`).
- Results and file lists are capped (6 results/page, first 25 files) to
  stay well under Telegram's message/keyboard size limits — refine your
  search text if you don't see what you need.
- Session state (current search/unlocked share per chat) lives in memory
  only; restarting the bot just means users search again.
- This repo does **not** attempt to push firmware files (boot.img,
  vbmeta.img, full ZIPs) *through* Telegram — it hands out expiring direct
  URLs instead, since Telegram's upload limits would be a poor fit for
  multi-GB firmware ZIPs. If you specifically want the bot to extract and
  send `boot.img`/`vbmeta.img` as Telegram documents when they're small
  enough, that's a straightforward addition on top of
  `RemoteZipReader.ExtractEntryAsync` — ask and it can be added.
