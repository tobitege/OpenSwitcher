![OpenSwitcher](assets/openswitcher-banner.png)

# OpenSwitcher

A small .NET 10 WinUI 3 application that reads locally installed LLMs from LM Studio and Ollama and synchronizes the selected models with the corresponding OpenCode model catalog.

## Behavior

- LM Studio: `GET <server>/api/v1/models`, limited to entries of type `llm`; non-empty `loaded_instances` are marked as loaded.
- Ollama: `GET <server>/api/tags`; running models from `GET <server>/api/ps` are marked as loaded. If `/api/ps` fails, times out, or returns invalid data, the installed models are still listed.
- `<server>` is the scheme, host, and port of `provider.<provider>.options.baseURL` in the OpenCode configuration, falling back to `http://127.0.0.1:1234` (LM Studio) and `http://127.0.0.1:11434` (Ollama).
- Vision and tool support are read from the `capabilities` metadata returned by LM Studio or Ollama and displayed after the model name. Vision-capable models are written to OpenCode with `attachment: true` and text/image input modalities. Missing metadata is not guessed.
- If a local provider is unavailable or returns invalid data, its section collapses, a warning icon appears next to its heading with the full message as a tooltip, and a refresh button retries only that section.
- OpenCode configuration: `%USERPROFILE%\.config\opencode\opencode.json`, otherwise `opencode.jsonc`. This global file is the only file that is read and edited. If `OPENCODE_CONFIG` is set, OpenCode merges that file on top of the global one; OpenSwitcher shows its path in the footer but neither reads nor edits it.
- On load, exactly those local models that are present in the provider's effective OpenCode catalog are selected (`models`, intersected with `whitelist` when present).
- Applying a selection completely replaces `provider.<provider>.models` and `whitelist` with the selected local models. Non-local and deselected entries are removed, while settings for models that remain selected are preserved.
- An empty selection cannot be applied.
- The global OpenCode default model remains unchanged unless it belongs to the synchronized provider and was deselected. In that case, a selected loaded model—or otherwise the first selected model—becomes the replacement.

## Writing the configuration

- Only the changed properties are rewritten. Comments (`//`, `/* */`), formatting, property order, and all other settings of a JSON or JSONC file stay as they are; line endings and a UTF-8 BOM are preserved.
- Before every change, a timestamped backup `<config>.openswitcher-yyyyMMdd-HHmmss-fff.bak` is created next to the configuration. The newest 10 backups are kept.
- Saving without any change does not write the file and does not create a backup.

## OpenCode providers

- Lists every provider from `provider` in the OpenCode configuration and every provider stored by `opencode auth login` in `%USERPROFILE%\.local\share\opencode\auth.json`.
- Only the kind of key is shown: no key, environment variable (`{env:…}`), file reference (`{file:…}`), plain text in the configuration, or the `auth.json` type (API key, OAuth, well-known token).
- Key values are never displayed or converted to strings. Only provider IDs, names, and the `type` field of `auth.json` are read. Parse errors report only the line number.

## MCP servers

- Lists the entries under `mcp` in the OpenCode configuration. The dot in front of the name is green when the server is enabled and, for local servers, the program can be found; the tooltip explains the status (disabled, file not found, not found on the PATH of this app, relative path that cannot be verified, unset `{env:NAME}` variable).
- `Add …`, `Edit …`, and `Delete …` open a modal dialog. `Cancel` leaves the configuration unchanged; `Save` or `Delete` writes it immediately. If saving fails, the dialog stays open and shows the error.
- Local servers: program (with a file picker for `.exe` files), one argument per line, `NAME=value` environment entries. Remote servers: URL and `Name: value` headers. Both support `enabled` and an optional `timeout`.
- Editing writes only the fields that were changed in the dialog. Unknown settings of the entry (for example `oauth`), non-string values, and its position are kept; renaming is supported.
- `Import from Codex, Claude or Cursor …` reads `%USERPROFILE%\.codex\config.toml` (`mcp_servers`), `%USERPROFILE%\.claude.json` (user and project `mcpServers`), or `%USERPROFILE%\.cursor\mcp.json` (`mcpServers`). Selected servers are written to OpenCode; an existing entry with the same name is replaced and marked as such in the dialog.
- Variable references are converted to the OpenCode syntax: Claude `${NAME}` and Cursor `${env:NAME}` become `{env:NAME}`. Codex `bearer_token_env_var` and `env_http_headers` become headers with `{env:NAME}`; `startup_timeout_sec` becomes `timeout`.

Existing OpenCode chats retain their saved model selection. Synchronization controls the catalog OpenCode offers in its model selector.

## Build

```powershell
.\build-openswitcher.ps1
```

Complete output is written to `logs\build.log`.

The build is fixed to Windows Desktop x64 (`win-x64`). The generated application contains only English (`en-US`) runtime resources; the build script also enforces English .NET CLI output and cleans the previous application output before every build.

## Tests

```powershell
dotnet test tests\OpenSwitcher.Core.Tests\OpenSwitcher.Core.Tests.csproj
```

The MSTest project covers the configuration editor (comments, delete and add cases, edit round trip, backups), the model catalogs, the provider inspector, the importers, and the locators. It uses real temporary files and runs sequentially.

## Run

```powershell
.\run-openswitcher.ps1
```

LM Studio and Ollama only need to be running for their respective model query. If one server is unavailable, the other list remains usable and `Refresh` retries the query.
