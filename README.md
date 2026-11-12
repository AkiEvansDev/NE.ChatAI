# NE.ChatAI

A personal web front end for role-play chats with a language model, built on [NE.Standard](https://github.com/AkiEvansDev/NE.Standard).

It connects to a local model server (`koboldcpp`), keeps characters, personas, worlds, presets, scenarios and chats as plain files under one `data/` folder, and adds token and context charts alongside node-graph scenarios that drive an auxiliary model behind the scenes (summaries, scene reports, impersonation).

## Quick start

Download the launcher for your platform from the [latest release](https://github.com/AkiEvansDev/NE.ChatAI/releases/latest) into the folder where you want `NE.ChatAI` to live and run it:

- **Windows (x64)** — [`ChatAI.cmd`](https://github.com/AkiEvansDev/NE.ChatAI/releases/latest/download/ChatAI.cmd)
- **Linux (x64)** — [`chatai.sh`](https://github.com/AkiEvansDev/NE.ChatAI/releases/latest/download/chatai.sh) (`chmod +x chatai.sh && ./chatai.sh`)

On the first run the launcher downloads the latest self-contained build into `app/` and opens `http://localhost:5008` in your browser. On every subsequent run it checks GitHub Releases and updates `app/` in place when a newer version is available, leaving your `data/` folder untouched.

### Launcher options

```text
ChatAI.cmd [--port <port>] [--no-browser] [--no-update]
./chatai.sh [--port <port>] [--no-browser] [--no-update]
```

| Option | Description |
| --- | --- |
| `--port <port>`, `-p <port>` | Port to listen on on all interfaces (`0.0.0.0`). Defaults to `CHATAI_PORT` or `5008`. |
| `--no-browser` | Start the server without opening a browser window. |
| `--no-update` | Skip the GitHub release check and start the installed build directly. |

## Configuration

Settings and content live in `data/` next to the application by default. Environment variables override defaults:

| Variable | Description |
| --- | --- |
| `CHATAI_PORT` | Default port used by `ChatAI.cmd` and `chatai.sh` (`5008` when unset). |
| `ChatAI__DataPath` | Path to the folder where characters, personas, worlds, presets and chats are stored. |
| `ChatAI__KoboldCppAddress` | Initial `koboldcpp` server URL (`http://localhost:5001/`) used when creating the default connection on an empty data folder. |
| `ChatAI__Secrets__<name>` | API key override for the named secret over `secrets.json`. |

## Building from source

Requires the .NET 11 SDK pinned in [`global.json`](global.json).

```sh
dotnet build --configuration Release
dotnet run --project src/NE.ChatAI.Web
```

## Contributing

This repository is a **read-only mirror**. Development happens in a private repository and everything here is generated from it, so pull requests are switched off here — anything merged would be erased by the next release.

Issues are open and welcome — bugs, questions and feature requests all belong here.
