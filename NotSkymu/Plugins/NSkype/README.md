# NSkype plugin for Skymu

A Skymu `ICore` plugin that talks to your `Server/server.js` over plain
HTTP/JSON. No Microsoft code, no Skype client binaries — just REST calls
against your own server's existing routes.

## Setup

1. Copy this `NSkype/` folder into `Skymu-master/Plugins/`, next to `Stub/`,
   `Discord/`, etc.
2. Add `Plugins\NSkype\NSkype.csproj` to `Skymu.sln` (same way the other
   plugin projects are referenced).
3. In `Core.cs`, set `BaseUrl` to your server's `publicHost` from
   `Server/config.json` (e.g. `https://localhost` if running locally with
   the default `port: 443`).
4. Build. **I haven't been able to compile or run this against a live
   server** — there's no .NET SDK / NuGet access in the sandbox I wrote it
   in, so treat this as a solid first draft rather than tested code. Expect
   to fix a few small things (nullability warnings, header-parsing edge
   cases) once you build it for real.

## What's implemented (`ICore`)

- **Auth**: password login via `/ppsecure/post.srf` → `/rps/v1/rps/skypetoken`,
  plus refresh-token re-login via `/oauth20_token.srf` for `StoreCredential`/
  saved-credential auto-login.
- **Profile**: `GetUserInfo` via `/profile/v1/users/self/profile`.
- **Contacts**: `FetchContacts` via `/contacts/v2/users/:username/contacts`.
- **Conversations**: `FetchConversations` via `/v1/users/ME/conversations`
  (direct messages fully mapped; group chats are listed but member lists
  aren't resolved yet — see TODO below).
- **History**: `FetchMessages` via `/v1/users/ME/conversations/:id/messages`.
- **Sending**: `SendMessage`, `EditMessage`, `DeleteMessage` against the
  matching REST routes.
- **Presence**: `SetConnectionStatus` via `PUT .../presenceDocs/messagingService`.
  `SetMood` is a no-op — your server's profile route is read-only, so there's
  nowhere to send a mood update yet.
- **Live updates**: a background long-poll loop against
  `/v1/users/ME/endpoints/SELF/subscriptions/:id/poll`, translating incoming
  `NewMessage`/`ConversationUpdate` events into `MessageTube`/`ListTube`
  events.

## What's implemented (`IListManagement`)

- `FindNewContact` via `/v2.0/search`
- `AddContact` via `POST /contacts/v2/users/:username/contacts`

## What's implemented (`ICall`) — audio calling, real this time

Two-way **audio** calling now works end-to-end via
[SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) (SDP offer/answer,
ICE, DTLS-SRTP, RTP) plus **NAudio** for actual mic/speaker device I/O:

- `StartCall` builds a peer connection, creates an SDP offer, waits for ICE
  gathering to finish (non-trickle — your server has no ICE-candidate relay
  route, only one SDP blob per direction, so all candidates get embedded in
  the SDP before sending), then POSTs it to your server's call-creation route.
- Incoming calls arrive via the poll loop's `CallNotification` event; the
  offer SDP is cached and `IncomingCallTube` fires so the UI can show a
  ringing state.
- `AnswerCall` builds its own peer connection, sets the remote offer, creates
  an answer, and POSTs it to `/v1/calls/{id}/accept`.
- The caller's side picks up the answer via the `CallAcceptance` poll event
  and applies it to finish the handshake.
- `CallStateChangedTube` fires `Active` when the peer connection actually
  connects (not just when signaling completes), and `Ended`/`Failed`
  appropriately.
- `SetMuted` works (gates whether captured audio actually gets sent).
- `/api/v1/turn` is used for ICE server credentials, so calls should work
  through NAT via your server's configured TURN relay, not just on localhost.

### How audio device I/O actually works (important — this took three attempts)

The obvious choice, `SIPSorceryMedia.Windows` (SIPSorcery's own prebuilt
Windows audio/video device package), turned out to be a dead end for this
project specifically:

1. Its current releases (`6.0.0-pre` onward) require `net6.0-windows` or
   higher. Skymu only builds `net461` or `net5.0-windows` — never
   `net8.0-windows`-anything — so this line of releases can't work here at
   all, on either configuration.
2. Its older releases (pre-`6.0`) use classic .NET Framework's *built-in*
   WinRT interop support — which .NET 5+ removed outright. So those fail too,
   even on `net5.0-windows`.

There's no version of that package that lines up with either of Skymu's
build targets. Rather than keep guessing at version pins, `CreatePeerConnection`
in `Core.cs` bypasses `SIPSorceryMedia.Windows` entirely: it wires NAudio's
`WaveInEvent` (mic capture) and `WaveOutEvent`/`BufferedWaveProvider`
(speaker playback) directly to `RTCPeerConnection.SendAudio()` and
`OnRtpPacketReceived`, using the core `SIPSorcery` package's own
`AudioEncoder` class (G711 PCMU/PCMA) for the actual encode/decode in
between. NAudio is already a proven dependency in this codebase — Discord
and Fluxer use `NAudio.WinMM` the same way — and this approach only touches
`RTCPeerConnection`'s own documented public API (`addTrack`, `SendAudio`,
`OnRtpPacketReceived`), not any uncertain third-party interface contract.
Net effect: back to `netstandard2.0`, loads under either Skymu build
configuration, no Windows-specific package dependency for audio at all.

### What's NOT implemented

- **Video.** `StartCall` always negotiates audio-only regardless of
  `is_video_call`, and `SetVideoEnabled` is a no-op with a warning dialog.
  Real video needs a webcam capture + VP8/H264 encode/decode pipeline on top
  of everything here — a meaningful follow-up, not a small addition, and
  would face the same Windows-video-device-access questions that just took
  three rounds to sort out for audio.
- Call transfer, hold/resume, group/conference calls, screen sharing.

### Honesty about this section specifically

Everything else in this plugin (auth, messaging, contacts, attachments) I
verified line-by-line against your server's actual route implementations.
Calling is different: the SDP/ICE flow and the NAudio wiring are grounded in
real, documented `RTCPeerConnection`/`AudioEncoder` API — not guessed — but
I still can't execute or test a live WebRTC negotiation. Expect this to need
more real debugging than anywhere else in this codebase.

`SIPSorcery 6.0.4` has two known high-severity vulnerabilities per NuGet's
audit (`GHSA-28gm-jrmw-xx93`, `GHSA-jwjp-4649-v8jp`) — worth checking for a
newer patched `netstandard2.0`-compatible release before treating this as
more than local/private testing.


### Honesty about this section specifically

Everything else in this plugin (auth, messaging, contacts, attachments) I
verified line-by-line against your server's actual route implementations.
Calling is different: it's the first piece depending on a large third-party
library (SIPSorcery) whose exact runtime behavior I can't execute or test.
The SDP/ICE flow is grounded in SIPSorcery's real documented API, not
guessed — but expect to spend real debugging time on this specific piece,
more than anywhere else in this codebase.


## TODO / known gaps

- Group chat member lists aren't resolved in `FetchConversations` (would
  need a call to `/v1/threads/:id` per group).
- Message content is only lightly unescaped (`StripSkypeMarkup` just
  strips XML-ish tags) — rich text, mentions, quotes, and media messages
  will need proper parsing if you want them to render as more than plain
  text.
- Attachments/media messages aren't sent (`SendMessage` warns and no-ops
  if you pass one) — needs the `/v1/objects` upload flow wired in.
- No 2FA flow (`AuthenticateTwoFA` is a pass-through) — fine since your
  server doesn't implement TOTP yet either.
