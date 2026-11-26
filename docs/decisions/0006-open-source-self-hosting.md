# ADR 0006: Open source and self-hosted

**Status:** accepted, November 2026, with the spikes below still to run. The questions this record first left open were decided by the owner in November 2026 ([Decided by the owner](#decided-by-the-owner)). Nothing in it is built yet. **Decides:** how anyone installs and runs Passel on their own server: how one app and one extension find and trust any server, how users sign in through a central OAuth project, how each server chooses its push, how the API and the clients stay compatible across versions, how the app is built and distributed outside the Play Store, and how a self-hoster deploys. **Supersedes:** ADR 0005's Firebase setup and its reliance on FCM alone, the Play Store work in ADR 0002 and the roadmap, and the parts of ADR 0004 listed under [What this supersedes](#what-this-supersedes).

## Context

Passel was built as one service run by its owner:

- **Everything server-specific is compiled in.** The mobile app reads every setting as a compile-time dart-define (`lib/config/app_config.dart`), the API's address (`API_BASE_URL`) and the Google client ids (`SERVER_CLIENT_ID`, `APP_CLIENT_ID`) included. The extension's build writes the API's origin into the manifest's `host_permissions` and compiles in its client id (`build.mjs`). Only the web app reads its values at startup, from `/app/config.json` ([ADR 0004](0004-hosting-on-docker-compose.md)).
- **The API trusts Google directly.** The JWT bearer authority is `https://accounts.google.com`, and the accepted audiences are the web client id and the extension's (`Extensions/HostWebExtensions.cs`). Users are keyed by the token's `sub` alone (`ClaimTypes.NameIdentifier`). The clients send Google ID tokens, which last an hour, as their bearer tokens. First-party sessions (roadmap 1.B1) were planned, not built.
- **Push is FCM through one Firebase project.** The sender is on `main` but switched off: `compose.push.yaml`, the `Push:Fcm:*` settings, `PushWorker` and `FcmSender` ([ADR 0005](0005-push-notifications-on-android.md)). A device's push is a union, `DevicePush.Fcm`, with a `WebPush` case modelled in the registry but not sent. ADR 0005 planned for Atom to write the Firebase config into the app at build time.
- **The app has two flavors,** `development` (`com.invex.passel.mobileapp.dev`) and `production` (`com.invex.passel.mobileapp`), chosen in Atom with `MobileAppFlavor` (`dev` or `prd`). The plan was a Play listing through internal testing (roadmap 2.A1), with Play App Signing (ADR 0005's setup, step 5) and a separate pre-prod app flavor beside them.
- **The extension's id is fixed** at `aikjdmilnbdacmaeahelifppjjgppemj` by the `key` in its manifest, so its Google redirect URI, `https://aikjdmilnbdacmaeahelifppjjgppemj.chromiumapp.org/`, is the same for every build.
- **The repository is MIT-licensed and already public** (checked 26 November 2026). The deploying guide has the GHCR images made private ([Server setup](../guides/deploying.md#server-setup), step 8), and they are built for `linux/amd64` only.

The owner wants anyone to be able to run Passel: clone the repository, deploy the API and the web app to their own server, and install the app and the extension, each step easy. A Play Store listing is not wanted, so the app is sideloaded.

### Android developer verification

As read on developer.android.com/developer-verification and its guides on 26 November 2026. The pages disagree with each other in places, noted below.

- **Timeline.** The pages give 30 September 2026 as the date protections begin in Brazil, Indonesia, Singapore and Thailand, on certified devices running Android 8 or later. They are still written in the future tense and don't say whether enforcement has started. Verification "will be expanded globally" in 2027.
- **What the first phase covers.** The FAQ says it covers installs from participating stores only: an app that users sideload "won't" be covered "yet". The limited distribution guide reads more strictly. It says package names not registered by 30 September "will no longer be installable on certified Android devices in those regions".
- **ADB.** Installs over ADB need no verification.
- **Unregistered apps.** These install only through the "advanced flow". The user turns on developer mode, waits a day once, and confirms. Every install then shows an "unverified developer" warning. The open-source guide calls registering optional for that reason: without it, users go through the advanced flow.
- **The free account.** A limited distribution account costs nothing and needs no government ID, but it is for devices the end user has authorised by QR code or link, not for public distribution. The overview and the FAQ say 20 devices. The limited distribution guide says 50.
- **The full account.** It costs $25, and the owner's identity is verified. Registering a package means proving you hold its signing key, with the key's SHA-256 fingerprint. A store that re-signs an app needs its key registered too.
- **The 29 September change.** No change dated 29 September 2026 was found on these pages. The FAQ's answers carry stamps up to 10 November 2026, but the pages don't say what changed.

For Passel this means: an APK that the public installs from GitHub Releases needs its stable and beta package names registered against the release key, which needs a full account. That has to happen before the global rollout in 2027. Until then, only users in the four countries can be affected, and only if the stricter reading holds. Development builds are installed over ADB and are exempt.

## Decision

### Open source and self-hosted

Anyone can clone the repository and deploy the API and the web app to their own server, and install the app and the extension themselves. Each of those steps must be easy, and the [self-hosting guide](#repository-and-docs) is where that is measured. There is no Play Store listing. The app is sideloaded from GitHub Releases ([App distribution](#app-distribution)).

### One app and one extension for every server

One prebuilt APK per channel and one extension work with any Passel server. Nothing server-specific is compiled in: no API address, no Google client id, no Firebase options. The user enters the server's URL at first start, or scans a QR code the web app shows (Settings, "Add a device"). The client then reads the server's [discovery document](#discovery) and configures itself from it.

`AppConfig` keeps its compile-time values for development only: `DEV_USER`, `TUNNEL_ACCESS_TOKEN`, and a default server URL that a development build may prefill. The client ids move into the build as constants of the central OAuth project ([Sign-in](#sign-in)), which are the same for every server.

Switching servers is a sign-out from the old server, then a fresh start against the new one. The local cache, the device registration and the push setup all belong to one server. Whether the app has to restart its process for that, because Firebase can't be started twice with different options, is a [spike](#spikes).

### Sign-in

Sign-in goes through a central OAuth project that the owner runs. Google comes first. Microsoft and Apple come later, and the design leaves room for them. Every self-hosted server trusts the central project's client ids as audiences. The project's ids are public values, compiled into the clients and set as the server's defaults. A self-hoster can override them.

**Token exchange.** A client sends the provider's ID token once and gets Passel's own tokens back:

1. The client asks the server for a nonce: `POST /api/auth/nonce`. The nonce is random, single-use, and expires after a few minutes.
2. The client puts the nonce in the provider's sign-in request and gets back an ID token that carries it.
3. The client sends the ID token to `POST /api/auth/exchange`. The server checks five things: the signature, the issuer, the audience (one of the trusted client ids), the expiry, and that the nonce is one it issued and has not used yet. Then it returns a short-lived access token and a rotating refresh token, both its own, tied to a device. This is roadmap 1.B1, which was already planned.
4. From then on, the provider's ID token is never sent again.

Every server shares the same audiences. Without the nonce, an ID token that one server received could be replayed to another. The nonce stops that.

**Binding the nonce to the server.** A nonce alone doesn't stop a server the user signs in to from relaying. That server can fetch a nonce from another server and hand it to the client as its own. So the client doesn't send the server's nonce to the provider directly. It sends SHA-256 of the server's origin, as the client itself reached it, joined to the nonce. The server recomputes the hash from its own `PUBLIC_URL`. A token obtained through a relaying server then carries that server's origin, and is refused anywhere else. This record added the binding, and the owner accepted it with the rest. Spike 4 checks that the clients can carry it.

**User identity.** Users are keyed by the issuer and subject pair, not by email. Two providers can then link to one account later. Users who exist today are keyed by Google's `sub` alone. A migration gives each of them Google's issuer, and their ids don't change.

**Registration control.** Registration is closed by default. The self-hoster opens it in one of three ways:

- an allowlist of email addresses
- an allowlist of domains
- invite codes, which an existing user creates in the web app

An email allowlist matches only a verified address (`email_verified`) from the provider, and only when an account is first created. After that, the account is its issuer and subject pair. The `.env` setup script asks for the owner's email and puts it on the allowlist, so the first sign-in works.

**The web app and the extension.**

- **The extension** can use a central client. Its Google redirect, `https://<extension id>.chromiumapp.org/`, is fixed by the manifest key, so one extension client in the central project serves every server. That sets it apart from the web app, because its redirect doesn't depend on the server. QR pairing works for it too, as a fallback.
- **The web app** can't use a central client. Google's web clients list their JavaScript origins, and the central project can't list every self-hoster's. There are two routes:
  - **QR pairing**, the preferred route. The web app shows a QR code, the signed-in phone scans it and approves, and the server hands the browser its own session. Nothing is needed from Google.
  - **The self-hoster's own Google web client.** It is set on the server as one more trusted audience, and discovery offers it to the web app.

  QR pairing is the default, and the self-hoster's own client is an option ([Decided by the owner](#decided-by-the-owner)).

**Microsoft** will be a multi-tenant Entra app that also accepts personal accounts (the `common` authority). Its issuer names the tenant (`https://login.microsoftonline.com/{tid}/v2.0`), so the server checks the issuer against that template rather than against a fixed string. Users are keyed by that issuer and `sub`, like everyone else.

**Apple** needs a developer account, at $99 a year. Its web flow sends the result to return URLs that are registered with Apple and have verified domains. So it probably needs a central callback at a domain the owner runs, which would then hand the result to the user's own server. Apple waits until there is an iOS app ([Decided by the owner](#decided-by-the-owner)), and the [spike](#spikes) runs before the account is paid for.

### Push, chosen per server

Each server chooses one push mode and reports it in discovery. The app sets itself up from what it reads there.

| Mode | How it works |
|---|---|
| `none` | No push. The app refreshes when it is opened, and the event stream keeps it live while it is visible. This is the default |
| `fcm` | The server uses its own Firebase project. The self-hoster registers Passel's package names in it, and the server publishes the project's public options (API key, app id, project id, sender id) in discovery. The app starts Firebase at runtime from those options, with no `google-services.json`. The existing FCM sender sends, with the self-hoster's service account key |
| `unifiedpush` | UnifiedPush through a distributor such as ntfy, which the user installs on the phone. The app gets an endpoint from the distributor and registers it as a Web Push subscription. The server sends with the Web Push protocol (RFC 8030, with RFC 8291 encryption and VAPID) |
| `relay` | Optional, and possibly never built: a relay the owner runs, holding one Firebase project for everyone. It is not run for now ([Decided by the owner](#decided-by-the-owner)) |

**Web Push serves the browsers too.** The extension and the web app subscribe with Chrome's own Push API. Their subscriptions are `DevicePush.WebPush`, the case the registry already has, and the same Web Push sender delivers to them whatever the server's mode. Chrome's push service is run by Google, but it needs no Firebase project from the self-hoster.

**VAPID keys.** The server generates its key pair on first start and keeps it beside its data. It never changes the key pair, because every browser and UnifiedPush subscription is tied to the public key. Discovery publishes that key.

**The Web Push sender** posts to whatever endpoint a client registered. As the [device registry](../components/web-api.md#device-registry) already says, it must check the addresses an endpoint resolves to, as the link preview fetcher does.

**The payload.** A UnifiedPush or Web Push message is data only: nothing shows it unless the app or the service worker does. So the app builds the notification itself, with `flutter_local_notifications`, from a payload that carries the same fields as today. That brings back the data-only path ADR 0005 set aside. It also closes the window ADR 0005 accepted after a sign-out, because the app can check which account is signed in before showing anything. In `fcm` mode the server may keep sending notification messages, or move to data only so that both modes behave the same. Phase 3 settles which. The payload is a contract, so it carries a version ([Versioning](#versioning)).

**Spikes.** Three questions are tested before any of this is built ([Spikes](#spikes)):

- starting FlutterFire at runtime, including in the background isolate
- registering one package in several Firebase projects
- restarting the app when the server changes

### Discovery

A new unauthenticated endpoint, `GET /.well-known/passel`, describes the server to the clients:

```json
{
  "version": "1.4.0",
  "apiVersions": ["1", "2"],
  "minClientVersion": "1.2.0",
  "signIn": {
    "providers": [{"id": "google", "issuer": "https://accounts.google.com", "clientIds": {"android": "…", "extension": "…", "web": "…"}}],
    "pairing": true,
    "registration": "invite"
  },
  "push": {"mode": "fcm", "fcm": {"apiKey": "…", "appId": "…", "projectId": "…", "senderId": "…"}, "webPush": {"vapidPublicKey": "…"}},
  "features": ["parcels", "linkPreviews", "search"]
}
```

The field names are a sketch, and phase 1 fixes them. The document holds only public values. Clients cache it and fetch it again on each start. The QR code that the web app shows holds the server's URL and, when pairing, a one-time code. It never holds a token.

### Versioning

**API versions.** A breaking change creates a new `api-version`, and an additive one does not. The server keeps the previous version for a stated window, 12 months unless a release note says otherwise. Discovery lists the versions the server supports.

**Clients:**

- They use the highest version both sides support.
- They decide what to show from the feature list, not from the server's version number.
- They ignore fields and enum values they don't recognise. The feed scenarios already require that of the `packet-updated` event.
- They say "update your server" or "update your app" only at the hard floors: no common API version, or a client older than `minClientVersion`.

**CI:**

- Every pull request runs an oasdiff check of `contracts/openapi.json` against `main`. A breaking change must come with a new version.
- The contract tests run against the server images of the last one or two releases, so a new client is checked against servers that are already deployed.

**Other contracts.** Push payloads, the pairing handshake and any relay protocol carry versions too.

**Migrations** run forward only. That is already the rule: they add, and never drop ([ADR 0004](0004-hosting-on-docker-compose.md)). A rollback runs the older code on the newer schema, as it does today.

### Release channels instead of flavors

The app's flavors become release channels: `dev`, `beta` and `stable`. Atom's `MobileAppFlavor` becomes the channel parameter, and its values `dev` and `prd` become `dev`, `beta` and `stable`.

| Channel | Package | Built by | Signed with |
|---|---|---|---|
| `dev` | `com.invex.passel.mobileapp.dev` | Developers, locally | Their debug key. Development builds sign in through the `Dev` scheme or the developer's own Google project, as today |
| `beta` | `com.invex.passel.mobileapp.beta` | The release workflow, from `main` | The release key |
| `stable` | `com.invex.passel.mobileapp` | The release workflow, from a version tag | The release key |

Beta has its own package, so it installs beside stable. Pre-prod stops being something the app knows about. It is just another server, and the owner's beta install points at it, but nothing in the beta build names it. The `dev` package keeps the cleartext network configuration for `localhost` and the emulator host. Beta and stable require `https`.

The central OAuth project registers Android clients for the beta and stable packages, each with the release key's SHA-1. The release key is the only signing key: there is no Play App Signing and no upload key. A lost key means no update can reach anyone who installed the app, and without the key no package can be registered for developer verification. So the key and its passwords are backed up, in two copies kept apart.

### The extension

The extension asks for its server's origin at runtime, through `optional_host_permissions` (`https://*/*`, and `http://localhost/*` for development) and `chrome.permissions.request` when the user enters a server. That replaces the `host_permissions` that `build.mjs` generates today.

The manifest sets no content security policy today, so Manifest V3's default applies, and that doesn't restrict `connect-src`. If a policy is added, its `connect-src` must allow the server's origin. A manifest can't name an origin chosen at runtime, so that means allowing `https:` in general. The runtime host permission is what narrows the extension to one server.

The extension is published on the Chrome Web Store, keeping its fixed id. Loading it unpacked from a release's zip is the fallback.

### Packaging for self-hosters

**Images.** The images are multi-arch, `linux/amd64` and `linux/arm64`, and public on GHCR. Each is tagged with its version and with `stable`, which moves to each new stable release. A self-hoster can follow `stable`. The owner's stacks deploy by digest.

**Setup.** Each release attaches a compose bundle to its GitHub Release. The bundle holds three things:

- a script that generates the `.env`: it asks for the server's URL and the owner's email, and generates the passwords, the Sqids alphabet and the VAPID keys
- an optional overlay with Caddy for automatic TLS
- the push overlays

**Configuration** starts from one base setting, `PUBLIC_URL`. The server derives everything else from it: its allowed host, its origins, the links it writes and the nonce binding. The web app is served from the same origin as the API. The edge sends `/api/*`, `/.well-known/passel` and `/health` to the API, and everything else to the web image. The browser then calls its own origin, so CORS is needed only for a self-hoster who splits the two, and the web image's `API_ORIGIN` comes from `PUBLIC_URL`.

**Runtime:**

- **Migrations run on startup:** `docker compose up` migrates before the API starts. Read with ADR 0004's database roles, that means `db-migrate` stays a one-shot service, and the API waits for it to finish (`depends_on` with `service_completed_successfully`). The API then still never changes the schema itself and keeps its DML-only role ([ADR 0004](0004-hosting-on-docker-compose.md#decision)). A self-hoster doesn't run the migration as a separate step.
- **Backups:** the self-hosting guide documents a backup step before each upgrade (`backup.sh`).
- **Limits:** quotas and file size limits are configurable, as they are now.
- **Telemetry:** there is none. The Aspire dashboard stays optional, behind the `telemetry` profile, and nothing leaves the server.

### App distribution

Signed APKs are attached to each GitHub Release: stable from tags, beta from `main` as pre-releases. Users can install and update them with Obtainium, or by hand.

The stable and beta package names are registered for Android developer verification against the release key before the 2027 rollout ([Context](#android-developer-verification)).

F-Droid and IzzyOnDroid are for later. IzzyOnDroid ships the developer's own APK, so its signature stays the release key's. The main F-Droid repository rules out Firebase and builds and signs apps itself. So an F-Droid build would need a variant without Firebase (UnifiedPush only), and its key registered too.

### Repository and docs

**Docs:**

- A self-hosting guide becomes the front door of the docs. The developer docs (getting started, components, guides, reference) sit apart from it.
- The owner's server, gaia, becomes a worked example of the guide. It covers the existing Caddy, two stacks, and the deploy from GitHub Actions. The [deploying guide](../guides/deploying.md) mostly becomes that example.

**CI:**

- CI on forks runs without secrets: the build, tests, docs and image builds all work without them, and nothing is pushed.
- The deploy workflows stay separate from build and release.
- A release workflow builds the APKs, the extension zip, the compose bundle and the multi-arch images, then publishes the GitHub Release.

**Checks for a public repository.** The repository is already public, so these are overdue rather than a gate:

- Scan the whole git history for secrets, with gitleaks for example, and rotate anything it finds.
- Confirm that Atom and its modules are on public NuGet. The repository has no `nuget.config`, and CI restores from nuget.org alone, so Atom 3.7 already is. Check that their licences allow a public build.

### Details added by this record

The owner made the decisions above. This record added the details below to make those decisions concrete, and the owner accepted them in November 2026. The phase that builds each one may still refine it:

- **Names:** the endpoint names `POST /api/auth/nonce` and `POST /api/auth/exchange`, the discovery fields, and the beta package `com.invex.passel.mobileapp.beta`.
- **Sign-in:**
  - The nonce bound to the server's origin ([Sign-in](#sign-in)).
  - A self-hoster may override the central client ids.
  - Invite codes are created in the web app.
  - The setup script puts the owner's email on the allowlist.
  - The extension uses a central client, and keeps QR pairing as its fallback.
- **Defaults and windows:** `none` is the default push mode, and the support window is 12 months (the decision gives 12 months as an example).
- **Builds:** beta is built from `main` and published as pre-releases.
- **Distribution:** the stable and beta packages are registered for Android developer verification.
- **Packaging:** migrations stay a one-shot `db-migrate` service, run by `compose up`.

### What this supersedes

- **[ADR 0005](0005-push-notifications-on-android.md):**
  - **FCM as the only transport:** Firebase becomes one push mode among several.
  - **The Firebase config written by Atom at build time:** the app starts Firebase at runtime from discovery instead.
  - **One Firebase project for both packages:** each self-hoster has their own project, or none.
  - **The owner's setup steps** that assume Play App Signing.
  - **The message type:** UnifiedPush and Web Push need data-only messages.

  The API half of 0005 stands: the sender, the queue, `SourceDeviceId`, the answers from FCM and the overlay. So does most of the app's registration flow.
- **The MIT license:** the repository moves to AGPL-3.0 ([Decided by the owner](#decided-by-the-owner)). Roadmap S4.A4 makes the change.
- **[ADR 0002](0002-product-decisions.md):** the store listings, meaning Play. The Chrome Web Store listing stays.
- **The Play Store work:** roadmap 2.A1's Play internal testing and listing, Play App Signing (ADR 0005's setup, step 5), and the separate pre-prod app flavor planned beside them.
- **Anything that compiles the Google client ids, the API's address or the Firebase options into a client:**
  - the mobile app's `API_BASE_URL`, `SERVER_CLIENT_ID` and `APP_CLIENT_ID` dart-defines, for release builds
  - the extension's `API_URL`, its generated `host_permissions`, and its client id as a build value
  - `BuildMobileAppAndroid`'s and `BuildChromeExtension`'s parameters for them
- **[ADR 0004](0004-hosting-on-docker-compose.md):**
  - "There is no `latest`": the images gain a moving `stable` tag.
  - The `linux/amd64`-only images, and the private packages that the [deploying guide](../guides/deploying.md#server-setup) sets up.
  - The separate API and web hosts (`API_HOST`, `WEB_HOST`), replaced by `PUBLIC_URL` and one origin.
  - The web image configured with a Google client id that the server must supply.

  The rest of 0004 stands: the compose stack, the database roles, the filesystem store, the add-only migrations and the rollbacks. Its manual-only deploy is superseded too, for the owner's stacks. Pre-prod deploys automatically from `main`, and production from tags with the owner's approval. Both deploy by digest, with the runner reaching the gate over Tailscale ([Decided by the owner](#decided-by-the-owner)).

## Consequences

**Users and self-hosters:**

- A self-hoster needs a server, a domain and Docker, and nothing from Google, unless they want FCM push or their own web client. The central OAuth project serves everyone.
- The owner's OAuth project becomes infrastructure that other people's servers depend on. If it is suspended, or its consent screen loses verification, sign-in stops on every server. Google is likely to require verification of the consent screen, since the project serves the public. That is to be confirmed when the project is set up, and the owner keeps it in good standing.
- A self-hoster can override the trusted client ids with their own, so a server doesn't have to depend on the owner's project. Their users then need clients that carry those ids, which means their own builds.
- Users' existing Google sign-ins keep working through the exchange. Existing user ids gain Google's issuer in a migration.

**The clients:**

- The app and the extension get a first-run "Connect to a server" screen and a server switcher, and they handle servers older and newer than themselves.
- The first-party sessions of 1.B1 are now required, not optional. The extension and the web app no longer have to sign in again every hour.

**Push:**

- Push becomes three code paths in the app: none, FCM and UnifiedPush.
- The app gains a data-only notification path, which ADR 0005 avoided.
- A server in `unifiedpush` mode depends on a distributor that the user installs.

**Signing and distribution:**

- The release key is the only signing key and must not be lost.
- Developer verification needs a full account ($25, identity verified) and the release key's SHA-256 registered before 2027.
- Without registration, users outside ADB go through the advanced flow, with its one-day wait and a warning on every install.

**Compatibility and CI:**

- Every API change is weighed as additive or breaking, and CI enforces the call.
- The previous API version stays for its window, which is code to keep.
- CI gains a multi-arch image build, a release workflow, an oasdiff check and contract tests against older images.

**Docs:**

- The docs that describe the owner's single deployment ([Google OAuth Clients](../getting-started/google-oauth.md), [Releasing and Deploying](../guides/deploying.md), [Configuration](../reference/configuration.md), the component pages) describe today's build until each phase lands. Each affected section links here, as do the mobile app's flavor note in [Running Locally](../getting-started/running-locally.md#mobile-app), the release builds in [Build Targets](../reference/build-targets.md#release-builds), and [Continuous Integration](../guides/ci.md#workflows).

## Decided by the owner

This record first left these questions open, each with a recommendation. The owner took every recommendation in November 2026.

| Question | Options | Decision |
|---|---|---|
| **License** | Keep MIT; move to AGPL-3.0 | **AGPL-3.0**, for the whole repository. MIT lets someone run a closed, hosted fork. AGPL makes anyone who offers a modified server over the network publish their changes. The point matters here: such a fork could ride on the owner's central OAuth project and the owner's app. The owner is the only author in the history, so relicensing needs no one else's consent. Releases already published under MIT stay MIT |
| **Deploying to gaia from GitHub Actions** | Tailscale; gaia polling GHCR or the releases from a systemd timer that runs `deploy.sh`; an HMAC-signed webhook through the existing Caddy; Cloudflare Tunnel. Not a self-hosted runner, since the repository is public | **Tailscale.** The runner joins the tailnet as a short-lived node tagged `tag:ci`. The access rules let `tag:ci` reach only port 22 on gaia, where the existing SSH forced-command gate does the rest. Nothing new listens on the internet, and the gate stays the one place that decides. GitHub Environments control what runs: `preprod` deploys automatically from `main`, and `production` deploys from tags with the owner's approval, both by image digest |
| **Running a push relay** | Run one; never | **Not now.** A relay would hold the Firebase credential for every server's users, see every server's traffic, and be a service to keep up. `unifiedpush` covers a self-hoster without Firebase. Revisit if self-hosters ask for it |
| **How the web app signs in** | QR pairing from the phone; a Google web client per self-hoster | **QR pairing as the default**, with the self-hoster's own client as an option. Pairing needs nothing from Google or the self-hoster, and it works for any origin. The option suits someone who uses only the web app |
| **Whether Apple is feasible** | Build it, with a central callback; defer | **Defer until there is an iOS app.** The app is Android only, so Apple sign-in would serve only the web app and the extension. That costs $99 a year and adds a central callback that sees every Apple sign-in. Run the spike before paying |

## Spikes

1. **FlutterFire without `google-services.json`.** Start Firebase with `Firebase.initializeApp(options: ...)` from discovery's options, with neither the Google services Gradle plugin nor its file. Check three things:
   - that the token works with the self-hoster's sender
   - that the background message handler, in its own isolate, starts Firebase the same way from options the app has stored
   - that a release build (minified) still receives a push
2. **One package in several Firebase projects.** Register `com.invex.passel.mobileapp` in two projects. Check that each gets working tokens and that the API key restrictions (package plus SHA-1) allow it.
3. **Switching servers.** Find out whether Firebase can be torn down and started again with other options in one process. If it can't, find the cleanest restart. Clear the cache, the device and the push token without leaving the old server's messages to arrive.
4. **A server's nonce in a Google ID token.** Confirm that `google_sign_in` 7, through Credential Manager on Android, takes a nonce per sign-in and returns it in the ID token. Confirm the same for the extension's `launchWebAuthFlow`. The token exchange depends on it.
5. **UnifiedPush end to end.** Use the Flutter UnifiedPush connector with ntfy as the distributor. Register the endpoint as a Web Push subscription, send an encrypted message from the API, and show it from the background.
6. **Web Push in the extension.** Subscribe from the service worker with the server's VAPID key, and confirm that a push wakes it and that it can show a notification.
7. **Apple through a central callback.** Register one Services ID with a return URL on a domain the owner runs. Hand the result to an arbitrary self-hosted server safely, without the callback keeping anything.

## Order

The [roadmap](../evaluation/roadmap.md#phase-s-self-hosting) carries the phases: discovery and a runtime server URL first, then the token exchange, then the push modes, then packaging, then the release channels.
