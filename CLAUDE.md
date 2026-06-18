# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a Unity 2022.3.62f3 project serving as the frontend/editor tool for the **Vivian Framework** — a system for creating and testing interactive 3D prototypes. The project integrates with a Python backend service via REST API to analyze scenes and generate interaction specifications.

## Setup

1. Clone with submodules: `git clone --recurse-submodules <repo-url>` (or initialize after: `git submodule update --init --recursive`)
2. Open in Unity Hub using Unity 2022.3.62f3 (LTS)
3. Build target: `File > Build Settings > Switch Platform`
4. Asset Bundles (only if needed for remote prototype loading): `Assets > Build Vivian Prototype Bundles`

## Architecture

### Core Components

**`Assets/Editor/VivianBackendWindow.cs`** — The main editor UI (menu: `Vivian/Backend Job Window`). Handles the full workflow:
- Scene scope selection (which GameObjects to analyze)
- Job configuration and start
- Real-time log streaming (polling every 0.75s)
- Status polling (every 0.5s)
- Scene review confirmation UI (chat-like, polls every 1.0s)
- Result display
- Config persisted in `EditorPrefs` under `VivianBackend.ServerUrl` (default: `http://127.0.0.1:8000`)

**`Assets/Scripts/BackendClient/VivianApiClient.cs`** — HTTP REST client (Newtonsoft.Json, 45s timeout). Endpoints:
- `POST /jobs/start` — start job
- `GET /jobs/{id}/status` — poll status
- `GET /jobs/{id}/logs?offset=N` — paginated logs
- `GET /jobs/{id}/scene-review` — get scene confirmation request
- `POST /jobs/{id}/scene-review/decision` — confirm/reject scene
- `GET /jobs/{id}/result` — fetch output
- `POST /jobs/{id}/cancel` — cancel job

**`Assets/Scripts/BackendClient/VivianJobService.cs`** — Stateful job manager wrapping the API client. Emits events (`StatusChanged`, `LogsAppended`, `Completed`, `Failed`). Tracks `CurrentJobId`, `LastKnownStatus`, accumulated `Logs` (capped at 120,000 chars), and `LastSceneReview`.

**`Assets/Scripts/Generated/ApiModels/`** — Auto-generated DTOs. **Do not edit manually.** Regenerate using `backend/Scripts/generate_dtos.py` on the backend side.

### Job State Machine

```
QUEUED → RUNNING → SUCCEEDED / FAILED / CANCELLED

Phases:
QUEUED → PREPARING_INPUT → ANALYZING_SCENE → AWAITING_SCENE_CONFIRMATION
       → GENERATING_SPECS → VALIDATING_OUTPUT → COMPLETED / FAILED / CANCELLED
```

### Vivian Prototype Structure

Each prototype lives in a folder:
```
PrototypeName/
├── Model.prefab (or .fbx/.blend)
└── FunctionalSpecification/
    ├── InteractionElements.json
    ├── VisualizationElements.json
    ├── States.json
    └── Transitions.json
```

Prototypes are loaded at runtime by the **VivianFramework** prefab (`Virtual Prototype` script) using either:
- Local Resources path (e.g., `Microwave`)
- Asset Bundle path (e.g., `AssetBundles/Windows/coffeemachine`)

The **MouseInteraction** prefab handles mouse-based interaction for desktop targets.

### Packages (Git Submodules)

Located in `Packages/`, referenced by package ID in `Packages/manifest.json`:
- `vivian-core` — core framework
- `vivian-example-prototypes` — example prototype resources
- `vivian-mouse-interaction` — mouse input implementation
- `vivian-autoquest-recording` — AutoQuest event recording
- `autoquest-generic-event-monitor-unity` — event monitoring

### Key Patterns

- All async backend calls use `async/await` with `UnityWebRequest` wrapped in `TaskCompletionSource`
- The editor window drives polling via `EditorApplication.update` callbacks (not coroutines — editor context)
- `VivianApiException` is thrown for non-2xx responses with message extracted from JSON `detail` field
- `GenerateInteractionsWindow.cs` in Assets/Scripts is deprecated; functionality merged into `VivianBackendWindow`
