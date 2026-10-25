<!-- markdownlint-disable MD022 MD024 -->
# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0]

- First release: synchronizes the LM Studio and Ollama model catalogs with the global OpenCode configuration.
- Lists the configured OpenCode providers and shows only the kind of key each one uses, never the key itself.
- Adds, edits and deletes MCP servers, and imports them from Codex, Claude or Cursor.
- Edits only the changed parts of the configuration, keeps comments and formatting, and keeps the newest 10 timestamped backups.
