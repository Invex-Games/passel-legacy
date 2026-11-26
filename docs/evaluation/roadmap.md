# Roadmap

Everything from the evaluation and the [feature ideas](features.md), cut into work packages that can be picked up independently. Each package names the area it touches so that packages in different areas can run at the same time (in parallel sessions or by different people) without stepping on each other.

## How to read it

- **Lanes** are areas of the repository: **A** platform, operations and docs, **B** the API, **C** the extension, **D** the mobile app, **E** the web app, **F** product and design decisions.
- **Phases** are the suggested order. Inside a phase, packages in different lanes run in parallel; packages in the same lane that touch the same files are listed in the order they should land.
- **Size:** S under a day, M a few days, L about a week, XL several weeks.
- **Source** links the report that motivates the package: [Performance](performance.md), [Security](security.md), [UX](ux.md), [UI](ui-design.md), [Health](codebase-health.md), [DX](developer-experience.md), [Features](features.md), [Web app](web-app.md).

Decisions that gate packages are listed in the [overview](index.md); make them in the first week.

[ADR 0006](../decisions/0006-open-source-self-hosting.md) (November 2026) moves Passel to an open-source, self-hosted model, and [Phase S](#phase-s-self-hosting) carries its work. Packages it overtakes are marked where they stand. The phases before it are kept as they were planned.

## Phase 0: safe to ship (one to two weeks)

Goal: the API deployed with CI in front of it, basic abuse protection, the storage model settled, and the fixes that would be embarrassing in front of users.

| Package | Lane | What | Size | Depends on | Touches | Source |
|---|---|---|---|---|---|---|
| 0.X | all | **Hygiene sweep, done first and alone:** `.gitattributes` with LF, `.editorconfig` to LF, strip BOMs, tool manifest for Atom and the README fix for target discovery, delete the stray keystore from the tree, untrack `.flutter-plugins`, commit `devtools_options.yaml`, trim the ignore file, vitest 5 and esbuild patch, align Aspire versions, Node-only npm scripts, `pubspec.yaml` cleanup, remove `avoid_print`, delete `CreateFilePacket` and the unused endpoint helpers, keep the personal device id out of `launch.json` | S to M | | Small edits across every area, so it lands before parallel work starts | DX 4, 5, 7, 11; Health 5, 11; Security 8, 9 |
| 0.A1 | A | CI through Atom's GitHub workflows module: `Test` on pull requests, `Build*` and docs publish on `main`, Dependabot for NuGet, npm, pub and actions, vulnerability scans; branch protection; CONTRIBUTING, LICENSE, CODEOWNERS, pull-request template, CHANGELOG from conventional commits; central package management and SDK, Node and Flutter pins | M | 0.X | `.github/`, `_atom/`, root files | DX 1, 2, 12; Health 7 |
| 0.A2 | A | Deploy the API: container image from `dotnet publish` with GitVersion tags, managed PostgreSQL, secrets from the host's store with the keys documented, health checks wired, OpenTelemetry to a real backend, backups, a `DeployWebApi` target and workflow | L | 0.A1 | `src/webapi`, `_atom/`, infrastructure | DX 1; Security 5 |
| 0.A3 | A | Schema and startup for production: `AutoCreate.None` with migrations applied by the pipeline, a DML-only database role, static code generation (`TypeLoadMode.Static`, `codegen write` in CI), `ApplyAllDatabaseChangesOnStartup` | S | 0.A2 | `Program.cs`, pipeline | Security 5; Performance 7 |
| 0.A4 | A | Dev tunnel: `WithExplicitStart()` now, `adb reverse` documented as the default, tunnel token from a dart-define when a remote device is needed, `RequireHost("localhost")` on the dev endpoints; remove the anonymous-dashboard variable | S | | `AppHost.cs`, `HostDevExtensions.cs`, `docs/` | Security 3, 7 |
| 0.B1 | B | Abuse controls and hygiene: rate limiter partitioned by user, per-user packet and byte quotas, caps on event streams per user and per instance, streams that end at token expiry, Kestrel connection limits, remove CORS until the web app needs it, HSTS, `AllowedHosts`, forwarded headers, fix the OIDC nonce setting and cookie flags | S | | `HostWebExtensions.cs`, `PacketEventHub.cs`, `GetPacketEvents.cs` | Security 2, 4, 6, 7 |
| 0.B2 | B | Storage model: ADR, then packets as plain Marten documents with `CreatedAt` and `DeletedAt`, true deletes, `CreatedAt` in the list and create responses and the SSE payload; or, if event sourcing is kept, `ArchiveStream` on delete with partitioning and a purge job | M | 0.B1 (same files) | `Domain/`, `Endpoints/`, `Program.cs`, tests, `docs/` | Health decision; Security 1; Performance 6; UX 3 |
| 0.B3 | B | Query and live-update tuning: composite index on user and id (verify the DDL), batched notification dispatch, `Task.WhenAny` heartbeat, exclude the event stream from tracing, spread `PublishToAll` resyncs | S | 0.B2 | `Program.cs`, `PacketNotificationListener.cs`, `GetPacketEvents.cs` | Performance 3, 4, 5, 9 |
| 0.B4 | B | Development authentication scheme (`Authorization: Dev <user>` or `dotnet user-jwts`) selected by the existing policy scheme; AppHost Google parameters optional in Development; a "run without Google" docs section | S | | `HostWebExtensions.cs`, `AppHost.cs`, `docs/` | DX 3 |
| 0.C1 | C | Popup quick fixes: cache capped at the first page, read-then-write in `measureClampedTiles`, animate only live arrivals, one snackbar with no layout shift, `:focus-visible`, signed-out state, rename the image menu item, truncate long tab titles, cap the SSE buffer, full jitter and a delay before the resync fetch | S to M | | `static/`, `popup.ts`, `feed.ts`, `live.ts`, `background.ts`, `sse.ts` | Performance 2, 5; UX 14, 15; Security 10 |
| 0.D1 | D | Mobile quick fixes: cache capped at the first page with debounced writes and no re-parse, route to the feed before Google initialises, `findChildIndexCallback`, ProblemDetails parsing and one `describeError`, clamp long notes, full jitter and a delay before the resync fetch | M | | `packets_provider.dart`, `packet_cache.dart`, `auth_helpers.dart`, `app.dart`, `widgets/` | Performance 1, 5, 8, 10; Health 4; UX 9, 10 |
| 0.F1 | F | Decide vocabulary, ordering and item naming; draft the privacy and terms pages; choose the web app stack and parcel storage | S | | `docs/` | UX 4, 5; overview decisions |

0.B1 to 0.B3 share files, so one person does them in that order; 0.B4 is independent. 0.X lands first and alone. Everything else runs in parallel.

## Phase 1: foundations (three to four weeks)

Goal: a test net under the API, the server-side building blocks every later feature needs, the shared TypeScript core, and the first web app.

| Package | Lane | What | Size | Depends on | Touches | Source |
|---|---|---|---|---|---|---|
| 1.A1 | A | Integration tests: `Testcontainers.PostgreSql` with `WebApplicationFactory`, the test auth scheme, create and list with ownership isolation, paging over 51 packets, delete semantics, SSE end to end through `pg_notify`, schema from an empty database; wired into `atom TestWebApi` | M | 0.A1, 0.B4 | new `Invex.Passel.WebApi.IntegrationTests` | Health 1 |
| 1.A2 | A | Contracts: one wire DTO and one mapper, committed OpenAPI with a staleness test, `contracts/fixtures/` written from the real `JsonOptions` and parsed by both client test suites, `contracts/feed-scenarios.json` run by both feed test suites | M | 0.B2 | `Endpoints/`, `docs/reference/`, both clients' tests | Health 2, 3 |
| 1.A3 | A | Docs: architecture sequence diagram, feed state-machine page, ADRs (storage model, ids, token flow, LISTEN/NOTIFY, tunnel), troubleshooting page, from-zero OAuth walkthrough with `keytool`, release and deploy page | M | 0.B2 | `docs/` | DX 6, 9 |
| 1.A4 | A | *Partly overtaken by S5: the production flavor becomes the beta and stable channels, signed with the release key alone.* `SetupDevEnvironment`: version checks, `devtunnel` login hint, config sync from AppHost user secrets to both `.env` files with one vocabulary, next-steps checklist; keystore written to a random temp path and Gradle without a daemon; keystore secrets required only for the production flavor | M | | `_atom/`, `.env.example` files, `docs/` | DX 8, 10; Health 6; Security 8 |
| 1.B1 | B | *Becomes S2.B1, and is required rather than optional: the exchange takes a server-issued nonce, and the API trusts the central OAuth project's client ids.* First-party sessions: exchange a Google ID token for an access token and a rotating refresh token, revocation per device, API accepts both token kinds during migration | L | 0.A2 | new `Auth` endpoints, `HostWebExtensions.cs` | Features 7 |
| 1.B2 | B | Device registry: register, rename, remove, list; push token storage | M | | new `Devices` endpoints and documents | Features 2 |
| 1.B3 | B | Parcels: `IParcelStore`, Azure Blob with Azurite in the AppHost, upload, download and thumbnail endpoints, the `File` branch of create, quotas, sweep job | L | 0.A2, 0.B2, 0.F1 | new `Parcels` area, `PostPacket.cs`, `AppHost.cs` | Features 1 |
| 1.B4 | B | Link previews: async fetch with SSRF guards, preview fields on the packet, `packet-updated` SSE event | M | 0.B2 | new service, the packet document | Features 5 |
| 1.E1 | E | Shared TypeScript core: npm workspace, `packages/core` from the extension's chrome-free modules with their tests, `packages/ui` from the tokens, `popup.ts` split into feed view, composer and toast modules behind a controller, the popup harness committed; the extension consumes the packages | M | 0.C1 | `src/chrome-extension` moves | Health 8; Web app; DX harness |
| 1.E2 | E | Web app M1: sign-in, read-only live feed, AppHost integration, preview deployment | M | 1.E1 | `apps/web` | Web app |
| 1.E3 | E | Landing page, privacy and terms pages | S | 0.F1 | `apps/web` | Web app |
| 1.C1 | C | Side panel, keyboard commands, omnibox keyword | S to M | 1.E1 | `manifest.json`, `background.ts` | UX 11, 12 |
| 1.C2 | C | Extension unit tests with jsdom and a stubbed `chrome`: auth, api (401 matrix, problem parsing), live backoff, background delete scheduling; coverage in the Atom target | M | 1.E1 | `test/` | Health 9 |
| 1.D1 | D | Android share target and process-text, pre-filled send sheet | S to M | 0.F1 | `AndroidManifest.xml`, `pubspec.yaml`, new handler | UX 1 |
| 1.D2 | D | Settings page skeleton: account, version, theme override | S | | new `settings/` | UX 18 |
| 1.D3 | D | Dependency majors: Riverpod 3 (automated migration), `share_plus` 13, `dynamic_color` 2 | M | 0.D1 | `pubspec.yaml`, `lib/` | Health 7 |
| 1.D4 | D | Widget tests: feed page, list load-more and retry and signed-out rows, send sheet, tiles; coverage in the Atom target | M | 0.D1 | `test/` | Health 9 |
| 1.F1 | F | Tile redesign specification and brand kit (logo, wordmark, icon style, empty-state illustrations) | M | 0.F1 | design assets, `docs/` | UI |

1.B1 to 1.B4 all add to `src/webapi`, mostly in new folders; run them on separate branches and expect small merge conflicts in `Program.cs` and `EndpointMapper.cs`. 1.E1 must land before 1.E2, 1.C1 and 1.C2. 1.D3 and 1.D4 touch `lib/` and `test/` broadly, so land them before 1.D1 and 1.D2 or rebase. Lane A and lane F packages are independent of everything else in the phase.

## Phase 2: parity and parcels (four to six weeks)

Goal: every client gets the redesign, parcels and notifications; the web app reaches parity; the apps are listed. *Since [ADR 0006](../decisions/0006-open-source-self-hosting.md), the app is not listed on Play. It is published on GitHub Releases ([Phase S](#phase-s-self-hosting)), and the extension is still listed on the Chrome Web Store.*

| Package | Lane | What | Size | Depends on | Touches | Source |
|---|---|---|---|---|---|---|
| 2.B1 | B | *The FCM half is built, and it becomes the `fcm` mode. The Web Push half moves to S3.B1.* Push sender: FCM and Web Push, triggered on packet commit, targeting the user's other devices | M | 1.B2 | new service beside the notification listener | Features 2 |
| 2.B2 | B | Search: `?q=` on the list endpoint with a Marten full-text index | M | 0.B2 | `GetPackets.cs`, `Program.cs` | Features 6 |
| 2.B3 | B | Account deletion and export, retention setting with nightly expiry, trash and restore | M | 0.B2 | new endpoints and jobs | Features 10, 11, 14; Security 1 |
| 2.B4 | B | Per-user change log, resumable SSE with event ids, `?since=` delta sync | M | 0.B2, 1.A2 | the packet document, `GetPacketEvents.cs`, both clients' stores | Health 3 |
| 2.C1 | C | Tile redesign, day headers, previews, avatar menu in the extension | M | 0.B2, 1.B4, 1.F1 | `popup/`, `packages/ui` | UI |
| 2.C2 | C | Parcels in the extension: paste, drop, optional host permission for image URLs, thumbnails, viewer | M | 1.B3, 2.C1 | `popup/`, `background.ts`, `manifest.json` | Features 1 |
| 2.C3 | C | *Moves to S2.C1.* Sessions in the extension | S | 1.B1 | `auth.ts`, `api.ts` | Features 7 |
| 2.D1 | D | Tile redesign, detail page, day headers, surface-coloured app bar, one ordering | M | 0.B2, 1.B4, 1.F1 | `widgets/`, `theme/` | UI; UX 4 |
| 2.D2 | D | Parcels on mobile: shared images, picker, camera, viewer, authenticated cache | M | 1.B3, 1.D1, 2.D1 | `packets/data/`, `widgets/` | Features 1 |
| 2.D3 | D | *Moves to S3.D1: Firebase started at runtime, plus UnifiedPush.* Push notifications and device registration | M | 1.B2, 2.B1 | new `push/`, `auth/` | Features 2 |
| 2.D4 | D | *Its sessions move to S2.D1. Account deletion and export stay here.* Sessions, account deletion and export in settings | M | 1.B1, 2.B3, 1.D2 | `auth/`, `settings/` | Features 7, 14 |
| 2.E1 | E | Web app M2 parity: send, delete with undo, offline cache, PWA install, the mobile layout below 600 px and the two-pane layout above 840 px, settings | L | 1.E2, 0.B2, 0.F1 | `apps/web` | Web app |
| 2.E2 | E | *Its Web Push moves to S3.E1.* Web app parcels, share target, Web Push, search | M | 1.B3, 2.B1, 2.B2 | `apps/web` | Web app |
| 2.A1 | A | *Overtaken by S4.A2 and S5: no Play listing, Play App Signing or pre-prod flavor. Signed APKs go on GitHub Releases, and the Chrome Web Store listing stays.* Release pipeline and store listings: extension zip with the version stamped from GitVersion to the Chrome Web Store, app bundle to Play internal testing then production, deletion page linked from the listing | M | 0.A1, 1.E3, 2.B3 | `_atom/`, `.github/` | DX 1 |
| 2.A2 | A | Observability in production: dashboards, alerts on error rate and stream count, error tracking; a load test with a few hundred synthetic users against staging | M | 0.A2 | infrastructure, `tests/load/` | Performance steps |

Within a lane the packages touch different screens and can run in parallel branches; the redesign packages (2.C1, 2.D1) conflict with the parcel packages (2.C2, 2.D2) in the tile widgets, so land the redesign first.

## Phase S: self-hosting

Goal: anyone can run Passel on their own server and install one app and one extension that work with it ([ADR 0006](../decisions/0006-open-source-self-hosting.md)). The sub-phases run in order. Each one leaves `main` working for the owner's own servers. The spikes come first in the phase they unblock.

### S1: discovery and a runtime server URL

| Package | Lane | What | Size | Depends on | Touches | Source |
|---|---|---|---|---|---|---|
| S1.B1 | B | `/.well-known/passel`: the server version, API versions, the lowest client version, the sign-in providers, the push mode with its public options, and the features. `PUBLIC_URL` as the base setting, with the allowed host and the origins derived from it | M | | new endpoint, `HostWebExtensions.cs`, options | ADR 0006 |
| S1.A1 | A | Versioning: `api-version` on the API, oasdiff on `contracts/openapi.json` in `Validate`, and contract tests against the images of the last one or two releases | M | S1.B1 | `contracts/`, `_atom/`, `.github/` | ADR 0006 |
| S1.D1 | D | A first-run server screen (URL or QR), the discovery client, the version and feature checks, and a server switcher. The server URL leaves the release dart-defines | M | S1.B1 | `lib/config/`, new `lib/server/` | ADR 0006 |
| S1.C1 | C | The server origin at runtime through `optional_host_permissions`, with the discovery client from `@passel/core` | M | S1.B1 | `manifest.json`, `build.mjs`, `background.ts` | ADR 0006 |
| S1.E1 | E | The web app reads discovery from its own origin, and Settings shows the "Add a device" QR code | S | S1.B1 | `src/web` | ADR 0006 |

### S2: the token exchange

| Package | Lane | What | Size | Depends on | Touches | Source |
|---|---|---|---|---|---|---|
| S2.X | all | Spike: a server-issued nonce, bound to the server's origin, carried in the Google ID token through `google_sign_in` 7 and the extension's `launchWebAuthFlow` | S | | none | ADR 0006 spike 4 |
| S2.B1 | B | Nonce and exchange endpoints, Passel's access and rotating refresh tokens per device, the central client ids as default audiences, users keyed by issuer and subject with a migration for existing users, and registration control (closed, email or domain allowlist, invite codes). Was 1.B1 | L | S1.B1, S2.X | new `Auth` endpoints, `HostWebExtensions.cs`, `Domain/` | ADR 0006; Features 7 |
| S2.B2 | B | QR pairing: a browser session approved from a signed-in phone | M | S2.B1 | `Auth` endpoints | ADR 0006 |
| S2.C1, S2.D1, S2.E1 | C, D, E | Each client signs in through the exchange and drops the hourly Google token. The web app pairs, or uses a self-hoster's web client. Was 2.C3 and 2.D4's sessions | M each | S2.B1, S2.B2 | the sign-in code of each client | ADR 0006 |

### S3: push modes

| Package | Lane | What | Size | Depends on | Touches | Source |
|---|---|---|---|---|---|---|
| S3.X | D | Spikes: FlutterFire from runtime options with no `google-services.json` (background isolate and release build included), one package in several Firebase projects, switching servers, and UnifiedPush with ntfy | M | | none | ADR 0006 spikes 1, 2, 3, 5 |
| S3.B1 | B | The push mode setting and its discovery block. The Web Push sender (RFC 8030 and 8291, VAPID keys generated on first start and kept, endpoint addresses checked like the preview fetcher's) beside the existing FCM sender. A versioned, data-only payload | M | S1.B1 | `Services/`, options, `compose.push.yaml` | ADR 0006; was 2.B1's Web Push |
| S3.D1 | D | Device registration, Firebase started at runtime in `fcm` mode, UnifiedPush in `unifiedpush` mode, notifications built in the app, and the permission sheet. Was 2.D3 | L | S3.X, S3.B1, S2.D1 | new `push/` | ADR 0005, 0006 |
| S3.C1, S3.E1 | C, E | Web Push from the extension's service worker and the web app's. Was part of 2.E2 | M | S3.B1 | `background.ts`, `src/web` | ADR 0006 spike 6 |

### S4: packaging

| Package | Lane | What | Size | Depends on | Touches | Source |
|---|---|---|---|---|---|---|
| S4.A1 | A | Public, multi-arch (`amd64`, `arm64`) images on GHCR, tagged by version and `stable`. One origin: the edge routes `/api`, `/.well-known/passel` and `/health` to the API. `db-migrate` runs on `compose up`. CI on forks without secrets | M | S1.B1 | `_atom/`, `deploy/`, `.github/` | ADR 0006 |
| S4.A2 | A | A release workflow: a GitHub Release with the compose bundle, the `.env` generator, the Caddy overlay, the extension zip and the signed APKs. The deploy workflows stay apart from it | M | S4.A1 | `_atom/`, `.github/`, `deploy/` | ADR 0006 |
| S4.A3 | A | Docs: the self-hosting guide as the front door, gaia as its worked example, and the developer docs apart. The pages flagged with ADR 0006 notes are brought up to date | M | S4.A1 | `docs/`, `index.md` | ADR 0006 |
| S4.A4 | A | The public-repository checks: a gitleaks scan of the history, and the licences of Atom on public NuGet. Then the move to AGPL-3.0: `LICENSE`, and every license field and notice that says MIT | S | | root files, package manifests | ADR 0006 |
| S4.A5 | A | Deploying the owner's stacks: pre-prod from `main` and production from tags with approval, by digest. The runner joins the tailnet as a short-lived `tag:ci` node that reaches only port 22 on gaia, where the existing forced-command gate takes over | S to M | S4.A1 | `_atom/`, `.github/`, gaia, the tailnet's access rules | ADR 0006 |

### S5: release channels

| Package | Lane | What | Size | Depends on | Touches | Source |
|---|---|---|---|---|---|---|
| S5.D1 | D | The `dev`, `beta` (`com.invex.passel.mobileapp.beta`) and `stable` channels in place of the flavors. Atom's `MobileAppFlavor` becomes the channel. Signed APKs from the release workflow, the central project's Android clients for beta and stable with the release key's SHA-1, and the key backed up | M | S4.A2 | `android/`, `_atom/` | ADR 0006 |
| S5.A1 | A | Android developer verification: a full account, and the beta and stable packages registered against the release key before the 2027 rollout. Obtainium instructions. The Chrome Web Store listing | S | S5.D1 | external | ADR 0006 |

The lanes inside each sub-phase run in parallel once the B package they depend on has landed. Microsoft sign-in, Apple (spike 7 first), a push relay and F-Droid or IzzyOnDroid come after Phase S, if at all.

## Phase 3: delight (ongoing)

In no particular order, each independent unless noted: send to a specific device (needs 1.B2 and 2.B1), pin and archive, home-screen widget and quick-settings tile, app shortcuts, Android App Links for `passel.link/p/{id}`, collections (needs parcels), offline send queue, read state, Markdown and code notes, public API with keys and a CLI (needs 1.B1), webhooks, iOS with Sign in with Apple, a desktop app, an end-to-end encryption design study.

## Critical paths

- **Ship:** 0.X, 0.A1, 0.A2, 0.A3, with 0.B1 and 0.B2 landing before the first public deployment.
- **Parcels:** 0.B2, 1.B3, then 2.C2, 2.D2 and 2.E2 in parallel.
- **Design:** 0.F1, 0.B2, 1.F1, then 2.C1, 2.D1 and 2.E1 in parallel.
- **Web app:** 0.C1, 1.E1, 1.E2, 2.E1; public launch also wants 1.B1 (so the web session doesn't expire hourly) and 2.A1. *Since [ADR 0006](../decisions/0006-open-source-self-hosting.md), those two are S2 (with QR pairing) and S4.*
- **Notifications:** 1.B2, 2.B1, then S3.B1, followed by S3.D1, S3.C1 and S3.E1.
- ~~**Store listings:** 0.F1, 1.E3, 1.D2, 2.B3, 2.D4, 2.A1.~~ Replaced by **Self-hosting:** S1, S2, S3, S4 and S5, in that order ([ADR 0006](../decisions/0006-open-source-self-hosting.md)).

## What can run at the same time

| While working on | Safe to run in parallel | Will conflict |
|---|---|---|
| Any lane B package | Every A, C, D, E, F package | Other lane B packages that edit `Program.cs` or `EndpointMapper.cs` (rebase; conflicts are small) |
| 1.E1 (core extraction) | Lanes A, B, D, F | Every lane C package: do 1.E1 first, or rebase C onto it |
| 2.C1 or 2.D1 (redesign) | Everything outside their lane | 2.C2 or 2.D2 in the tile widgets: land the redesign first |
| Lane D | Everything else | Nothing: the mobile app shares no files with the other lanes |
| Lane A docs and tests | Everything | 1.A2 edits both clients' test folders lightly; coordinate with 1.C2 and 1.D4 |
| Lane F | Everything | Nothing, but its decisions gate 1.B3, 1.D1, 1.E3, 2.C1, 2.D1, 2.E1 |

Phase 0's 0.X is the exception: it touches every area lightly and should be merged before anything else starts.

## If one person does it all

Quick wins first, then ship, then the thing users notice most, then the web app, then parcels: 0.X, 0.F1, 0.D1, 0.C1, then 0.B1, 0.B2, 0.B3, 0.B4, then 0.A1, 0.A2, 0.A3, 0.A4, then 1.D1, then 1.A1, 1.E1, 1.E2, 1.E3, then 1.B3 followed by 2.C1, 2.C2, 2.D1, 2.D2, then 1.B2, 2.B1, 2.D3, then 1.B1, 2.C3, 2.D4, then 2.E1, 2.B3, 2.A1, and the rest as demand dictates. *Since [ADR 0006](../decisions/0006-open-source-self-hosting.md), from where things stand: Phase S in order (S1, S2, S3, S4, S5), with 2.E1, 2.B3 and the rest of phase 2 fitted around it. S2 replaces 1.B1, 2.C3 and the sessions of 2.D4, S3 replaces 2.D3, and S4 and S5 replace 2.A1.*
