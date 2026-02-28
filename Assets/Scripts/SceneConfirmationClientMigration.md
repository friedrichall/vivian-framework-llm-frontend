# Scene Confirmation API Migration (Unity Client)

This document defines exactly what the Unity-side client must change after backend scene confirmation moves from file polling to job-scoped API calls.

## Scope

- Remove all usage of `scene_feedback.json`.
- Remove all dependence on `scene_understanding_summary.txt` as the source of truth.
- Use new backend scene-review endpoints tied to `job_id`.

## New Endpoints

### 1) Get current scene review payload

- Method: `GET /v1/jobs/{job_id}/scene-review`
- Purpose: fetch latest scene summary/JSON, current review revision, and whether user input is currently required.

Example response when user action is required:

```json
{
  "job_id": "5f9a1b...",
  "status": "RUNNING",
  "phase": "AWAITING_SCENE_CONFIRMATION",
  "review_state": "PENDING",
  "scene_review": {
    "revision": 2,
    "summary": "Scene: PumpPanel ...",
    "scene_understanding": {
      "scene_id": "PumpPanel",
      "objects": [],
      "relations": [],
      "clusters": [],
      "diagnostics": [],
      "user_feedback": []
    },
    "updated_at": "2026-02-28T14:22:09.113Z"
  },
  "error": null
}
```

Example response before review is available:

```json
{
  "job_id": "5f9a1b...",
  "status": "RUNNING",
  "phase": "ANALYZING_SCENE",
  "review_state": null,
  "scene_review": null,
  "error": null
}
```

### 2) Submit feedback or final confirmation

- Method: `POST /v1/jobs/{job_id}/scene-review`
- Purpose: send either corrective feedback or final confirmation for a specific revision.

Request payload:

```json
{
  "revision": 2,
  "confirmed": false,
  "feedback": "Button A controls Light B, not Light C."
}
```

Rules:

- `revision` is required and must match the latest `scene_review.revision` from GET.
- `confirmed=true` means final approval for current revision.
- `confirmed=false` requires non-empty `feedback`.
- Do not send stale revisions.

Confirm payload:

```json
{
  "revision": 3,
  "confirmed": true,
  "feedback": null
}
```

Example response:

```json
{
  "job_id": "5f9a1b...",
  "status": "RUNNING",
  "phase": "AWAITING_SCENE_CONFIRMATION",
  "review_state": "PROCESSING_FEEDBACK",
  "accepted_revision": 2,
  "message": "Scene review decision accepted."
}
```

Possible non-2xx cases:

- `409`: job is not currently awaiting scene confirmation, revision mismatch, or review is not in `PENDING` state.
- `422`: malformed payload.
- `404`: unknown `job_id`.

## Required Client Behavior Changes

1. Stop writing local confirmation files.
- Delete client logic that writes `scene_feedback.json`.
- Delete file watcher logic for `scene_understanding_summary.txt`.

2. Add explicit scene-review polling.
- Keep existing `/status` and `/logs` polling.
- Add `GET /v1/jobs/{job_id}/scene-review` polling every 1 second while job is active.
- Render review UI only when:
  - `phase == "AWAITING_SCENE_CONFIRMATION"`
  - `review_state == "PENDING"`
  - `scene_review != null`

3. Track revision strictly.
- Cache latest `scene_review.revision`.
- Include that exact revision in POST decisions.
- On `409` revision mismatch, re-fetch GET immediately and refresh UI.

4. Split actions into two buttons.
- `Send Feedback`: POST with `confirmed=false` and required free-text feedback.
- `Confirm`: POST with `confirmed=true`.

5. Handle response transitions.
- After feedback POST accepted, disable buttons until next GET returns `review_state="PENDING"` with incremented revision.
- After confirm POST accepted, keep polling status/logs/result; do not send additional confirmation calls.

6. Cancellation behavior.
- If user cancels job from UI, continue using existing cancel endpoint:
  - `POST /v1/jobs/{job_id}/cancel`
- When cancelled, close scene-review UI and stop scene-review polling.

7. Terminal states.
- On `status in {SUCCEEDED, FAILED, CANCELLED}`, stop scene-review polling.
- Keep existing result/error rendering from `/result` and `/logs`.

## Unity UI State Mapping

Use the mapping below to keep UX consistent with backend phases:

- `QUEUED`: show "Queued..."
- `PREPARING_INPUT`: show "Preparing input..."
- `ANALYZING_SCENE`: show "Analyzing scene..."
- `AWAITING_SCENE_CONFIRMATION + PENDING`: show summary + feedback textbox + confirm button
- `AWAITING_SCENE_CONFIRMATION + PROCESSING_FEEDBACK`: show "Applying feedback..."
- `GENERATING_SPECS`: show "Generating specification..."
- `VALIDATING_OUTPUT`: show "Validating output..."
- `COMPLETED`: show success state
- `FAILED` or `CANCELLED`: show terminal error/cancel state

## Backward-Incompatible Change

The client must no longer depend on filesystem handoff for confirmation. Scene confirmation is now accepted only through the scene-review API endpoints.
