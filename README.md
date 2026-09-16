# Athena Desktop

Athena Desktop is the Windows desktop client for Athena, written in C#. It is part of a solo semester and personal project to build a Raspberry Pi-hosted assistant for smart-home and PC control.

## Architecture

Athena runs on the Raspberry Pi and handles voice transcription and command routing. The desktop client will receive structured commands from Athena and execute supported Windows actions.

- **Athena:** Decides what action to perform and which backend should handle it.
- **Athena Desktop:** Executes supported PC actions and returns their results.
- **Home Assistant:** Handles smart-home actions separately from the desktop client.

The initial focus is deterministic commands. LLM fallback is a later Athena feature and is not required for desktop control.

## Current Progress

This repository is in its initial setup stage. No desktop actions or communication protocol have been implemented yet.

Planned capabilities include launching applications and arranging windows. The communication protocol, supported command set, and .NET version are still to be decided.

## Development

The client targets Windows and C#. Build, run, and test instructions will be added when the first implementation is available.

Development proceeds in small, testable steps with incremental commits.

## Related Repository

- [Athena](https://github.com/b-stha/athena): Raspberry Pi assistant and command router.
