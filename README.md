# Sprite2World

**The AI designs. Sprite2World engineers.**

Sprite2World is a containerized AI-assisted level-design tool for importing pre-sliced PNG sprites, creating a semantic room blueprint with OpenAI, engineering it into a deterministic tile map, validating and repairing that map, and playtesting the result immediately in the browser.

## How Codex & GPT-5.6 were used

This project was built end-to-end with **Codex as the primary engineering collaborator** and uses the **GPT-5.6 model family as the creative intelligence inside the product**. Under human direction, Codex supported nearly every layer of the build—from product framing and architecture to implementation, debugging, browser QA, Docker packaging and submission documentation.

### Codex during development

Codex was used as an active engineering partner throughout the complete development loop:

- **Product and architecture:** turned the initial concept and visual references into a scoped MVP, a two-service architecture and a clear boundary between AI intent and deterministic game logic.
- **Full-stack implementation:** created and refined the C# domain, application, infrastructure, worker and Blazor web layers; implemented the Canvas editor, persistence, import pipeline, generator, validator, repair flow and exports.
- **UI and interaction design:** translated design feedback into the three-column editor, onboarding, project and sprite libraries, inspectors, dialogs, responsive behavior and accessibility improvements.
- **Debugging and verification:** built containers, inspected logs, tested the application in a real browser and diagnosed release-only problems such as missing Blazor static assets in the optimized Docker build.
- **Quality and release work:** added deterministic tests, security checks for PNG/ZIP imports, health checks, Docker Compose setup, architecture notes and clean-clone verification from the public GitHub repository.

Codex did not merely generate a code sample. It was used iteratively to inspect real results, identify failures, edit the repository, rebuild the system and verify the finished behavior.

### GPT-5.6 inside Sprite2World

Sprite2World integrates GPT-5.6 through the **OpenAI Responses API** for three focused workflows:

| Workflow | What GPT-5.6 contributes | What deterministic code retains |
| --- | --- | --- |
| Sprite classification | Interprets imported sprite images and assigns semantic roles using vision input | Stable asset IDs, manual overrides, validation and persistence |
| World design | Converts a natural-language request into a strict, schema-constrained semantic blueprint | Room coordinates, corridors, walls, collisions, start/exit placement and seeded generation |
| Feedback iteration | Revises the semantic blueprint from contextual user feedback | Version history, regeneration, validation, repair and export |

The model is deliberately **not** asked to generate thousands of raw tile coordinates. GPT-5.6 handles visual understanding, intent and semantic structure; Sprite2World turns that structure into a reproducible and testable world. This makes the AI contribution both visible and trustworthy.

The model picker exposes the GPT-5.6 family by workload:

- **GPT-5.6 Luna** is the efficient default for cost-sensitive experimentation.
- **GPT-5.6 Terra** offers a balance of capability and cost.
- **GPT-5.6 Sol** provides the highest-quality option for demanding world-design prompts.
- The **GPT-5.6** alias routes to Sol.

Users can also choose low, medium or high reasoning effort. API responses use strict Structured Outputs and `store=false`, and an unavailable AI request falls back to an explicit deterministic demo blueprint so the core workflow remains testable.

This division of labor is the project's central idea:

> **GPT-5.6 designs the intent. Sprite2World engineers the playable result.**

Learn more in OpenAI's [GPT-5.6 model guide](https://developers.openai.com/api/docs/guides/model-guidance?model=gpt-5.6).



## MVP capabilities

- polished Blazor Web App editor with a three-column, desktop-first dark UI
- ZIP, multi-PNG and browser-supported folder import with PNG validation, stable content IDs and ZIP-slip protection
- adaptive asset library with search, role filter, exclusion, folder hints, manual overrides and OpenAI vision classification
- OpenAI Responses API with strict Structured Outputs for classification, blueprint creation and feedback revision
- deterministic `TopDownRooms` generator with rooms, orthogonal corridors, loops, walls, collisions, objects, start and exit
- independent pathfinding/validation and bounded obstacle repair
- Canvas rendering, pan/zoom/grid, minimap and keyboard playtest (WASD or arrow keys)
- version history and contextual feedback iteration
- complete project JSON and PNG preview export
- JSON-file persistence in a named Docker volume; no database
- a licensed seven-sprite starter pack plus procedural demo tiles for a sub-three-minute walkthrough

The first release intentionally supports one reliable grammar, `TopDownRooms`. Overworld, city, platformer, auto-tiling, animation and engine-specific exports are extension points, not claimed features.

## Screenshots

The application opens directly into the deterministic demo workspace. Add current screenshots from `http://localhost:3000` here when preparing the hackathon submission.

## Quick start with Docker

The only prerequisite is a running Docker Desktop installation or Docker Engine with Docker Compose. .NET, Node.js, a database and an OpenAI key are **not** required to start the application.

```bash
git clone https://github.com/RichardsWelt/sprite2world.git
cd sprite2world
docker compose up -d --build --wait
```

Open [http://localhost:3000](http://localhost:3000). On the first visit, onboarding asks for language and an optional personal OpenAI API key. The editor also starts without AI.

The first build downloads the .NET container images and can take a few minutes. Later builds reuse Docker's dependency cache and are considerably faster.

If port 3000 is already in use, choose another one without editing a file:

```bash
SPRITE2WORLD_PORT=3100 docker compose up -d --build --wait
```

Then open `http://localhost:3100`.

Stop without deleting project data:

```bash
docker compose down
```

Deliberately delete all saved projects:

```bash
docker compose down -v
```

## OpenAI configuration

For the shortest and safest setup, enter a personal API key in the first-start onboarding. The key is validated directly with OpenAI and is never committed to the repository, persisted in projects, exported or logged.

Server administrators can alternatively copy `.env.example` to `.env.local` and set `OPENAI_API_KEY` before starting Docker. Both `.env` and `.env.local` are ignored by Git.

Configuration variables:

- `OPENAI_API_KEY` — project API key
- `OPENAI_DEFAULT_MODEL` — defaults to the cost-sensitive `gpt-5.6-luna`; common compatible OpenAI models are selectable from grouped dropdowns in Settings and the Inspector
- `OPENAI_REASONING_EFFORT` — `low`, `medium` or `high`

If an API request fails, the error is presented in user-safe form and the generator uses an explicit deterministic demo blueprint so the workflow remains testable offline.

## Three-minute demo

1. Open the app; the licensed seven-sprite starter pack, procedural demo tiles and three example worlds are preloaded.
2. Import a PNG/ZIP pack or inspect the categorized demo assets.
3. Choose **AI classify** to classify non-overridden assets with image input.
4. Edit the world description and choose **Generate world**.
5. Inspect **Semantic Blueprint** and **Validation**.
6. Open **Playtest**, choose **Start**, focus the canvas, and move with WASD/arrows to the red exit.
7. Enter feedback such as “Needs another loop” and choose **Improve**.
8. Restore a version from the right panel or export JSON/PNG from the action rail.

Manual classifications always win over later AI classification. Pixel-art previews use nearest-neighbor browser rendering.

## Import limits and security

- PNG and ZIP only; sprite sheets are not sliced
- 250 assets by default
- 10 MB per upload/extracted file and 100 MB total
- normalized relative paths, duplicate rejection, path traversal protection and PNG signature/IHDR validation
- uploads are never executed

This is a local developer tool without authentication. Do not expose it directly to the public internet without authentication, HTTPS, request throttling and a deployment-specific security review.

## Tests

With a local .NET 10 SDK:

```bash
dotnet test Sprite2World.sln
```

With Docker only:

```bash
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test Sprite2World.sln
```

Tests cover blueprint serialization, seeded determinism, non-overlap, connectivity/reachability, start/exit, collisions, missing roles, validation, repair, ZIP traversal, PNG encoding and export completeness.

## Troubleshooting

- `localhost:3000` is unavailable: run `docker compose ps` and `docker compose logs`.
- Worker remains unhealthy: rebuild with `docker compose build --no-cache` and check the worker log.
- Asset write errors: ensure the named volume is writable, then recreate containers without `-v`.
- OpenAI authentication/model errors: verify the key/project access and select an available model in Settings.
- Do not paste the output of `docker compose config` into issues: Compose expands values from `env_file`, including secrets.
- Reset only the current browser project with Settings; remove every persisted project only with `docker compose down -v`.

## Hackathon

The demo highlights a transparent boundary: GPT returns compact semantic intent using a strict schema; all coordinates, collision rules, repairs, pathfinding, rendering and playability are owned by deterministic C# code.

See [ARCHITECTURE.md](ARCHITECTURE.md) for design decisions and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for dependency licenses.
