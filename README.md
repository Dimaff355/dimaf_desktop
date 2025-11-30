# P2P Remote Desktop Application (Windows) Specification

This repository tracks the requirements for a peer-to-peer remote desktop solution similar to TeamViewer or RustDesk. The application targets Windows and must support unattended access, including Session 0 isolation, UAC prompts on the secure desktop, and the logon screen.

## MVP Constraints
- Video and input control only (no audio).
- No file transfer, clipboard sync, or text chat.
- Single active operator session; subsequent operators receive a "Host is busy" error without interrupting the current session.

## Technology Stack
- **Language:** C# (.NET 8).
- **Architecture:** Windows Service (core) + User-mode GUI.
- **Networking:** WebRTC (video + DataChannel), signaling over WSS.
- **STUN defaults:** `stun:stun.l.google.com:19302`, `stun:stun1.l.google.com:19302` with TURN placeholders in config for future use.
- **Codecs:** H.264 preferred (hardware-accelerated) or VP8; library must be stable inside Session 0 without a UI thread.

## Quality Targets (SLA)
- Resolution: 720p minimum (1080p preferred).
- Frame rate: 25–30 FPS static, ≥15 FPS under motion.
- End-to-end latency: < 500 ms under 10+ Mbps bandwidth and RTT < 70 ms.

## System Architecture
### Signaling Server
- Hosted remotely with dynamic URL discovery (polling every 5–10 minutes).
- On URL changes, clients reconnect WebSocket cleanly and preserve P2P sessions when possible.
- Apply exponential backoff on resolver failures.

### Host Side
- **Core Service:** Runs as LocalSystem, handles WebRTC, DXGI capture, and input injection. Uses WTS APIs for session detection and must capture secure desktops (UAC/logon). Config files must restrict ACLs to SYSTEM and Administrators.
- **Configurator GUI:** User-mode app communicating via secured named pipes to set the password and display the host ID.

### Client Side
- WPF application that renders the video stream, handles input, and provides a monitor selection control for multi-monitor environments.

## Functional Requirements
### Display & Control (Multi-Monitor)
- Enumerate all screens (DXGI EnumOutputs) and switch capture sources on demand without breaking the WebRTC connection. GDI fallback must also support selecting a display device.
- Normalize pointer coordinates per active monitor, respecting per-monitor DPI scaling.
- Streaming is single-monitor at a time (no simultaneous multi-stream walls).

### Security
- Password stored as a hash (Argon2/BCrypt); host ID is a randomly generated GUID stored in config.
- Host-side brute-force protection: block connections for 5 minutes after 5 consecutive failures. Signaling server should rate-limit requests per IP.

### Reliability & Reconnection
- Automatically attempt to reconnect WebRTC after ICE failures (3–5 attempts with backoff).
- WebSocket signaling reconnects indefinitely with reasonable intervals without crashing the service.

### Logging
- File logging with rotation (max 10 MB per file).
- Log WTS session switches, auth success/failure and lockouts, network events (WebSocket connect/disconnect, ICE state changes), and capture/WebRTC errors.

## User Flow
1. Installation sets up the service and GUI; generates a GUID ID.
2. User configures the password via GUI.
3. Service runs in the background and updates status to the signaling server.
4. Operator connects; can switch monitors via UI; UAC prompts are captured through secure desktop handling.

## Development Milestones
1. **Stage 1 – Technical POC:** Service streams captured content via WebRTC in Session 0 to validate the library choice.
2. **Stage 2 – Core & Multi-Monitor:** Implement signaling/resolver, GUI + IPC, monitor switching, and basic mouse/keyboard input (tied to active monitor).
3. **Stage 3 – Advanced Integration:** WTS session switching, UAC/logon capture, secure input including Ctrl+Alt+Del.
4. **Stage 4 – Polish:** Password hashing, config ACLs, logging with rotation, reconnection logic, DPI handling, and resolver updates.

## Additional Documentation
- [Architecture](docs/ARCHITECTURE.md): detailed component responsibilities, configuration model, data channel protocol, reconnect flows, and operational notes.

## Code Layout
- `src/Shared`: reusable contracts for configuration, messaging, monitor descriptors, and password hashing (BCrypt by default).
- `src/Service`: Windows Service host that bootstraps configuration, enforces lockout policy, enumerates monitors, captures frames from the active display (GDI fallback), and streams them over signaling for the prototype.
- `src/SignalingServer`: lightweight WebSocket relay that pairs a single host with one operator for local/headless testing without a cloud signaling layer.
- `src/OperatorConsole`: CLI operator that speaks the signaling/data-channel envelopes to exercise the host flow end-to-end.
- `src/Configurator`: Windows WPF GUI that talks to the secured named pipe to fetch host status and set the password or resolver URL.

## Running the headless host prototype
- Restore tools and run the worker service: `dotnet run --project src/Service`.
- On successful resolver + WebSocket connection, the service immediately advertises the host ID, monitor list, and active monitor over signaling.
- Operators begin by sending `{ "type": "operator_hello", "session_id": "<guid>" }` followed by `{ "type": "auth", "password": "<plaintext>" }`.
  - The host enforces the single-operator rule; concurrent session IDs receive `host_busy`.
  - Monitor list and switch responses reuse the data-channel message shapes (`monitor_list`, `monitor_switch`, `monitor_switch_result`).
  - Local configuration and password management are available via the named pipe `\\.\pipe\P2PRD.Config` (JSON per line). Requests include `{ "type": "status" }`, `{ "type": "set_password", "password": "..." }`, and `{ "type": "set_resolver", "resolver_url": "wss://..." }`. Only SYSTEM/Administrators can connect on Windows.

## Using the Windows configurator
- The WPF configurator (`Configurator.exe` after publish) connects to `\\.\pipe\P2PRD.Config` as an elevated user (SYSTEM/Administrators) to manage the host.
- UI actions:
  - **Refresh status**: reads the host ID, resolver URL, and whether a password is set.
  - **Save password**: hashes and stores the provided password, clearing lockout state.
  - **Save resolver**: updates the resolver URL used by the host's signaling reconnect loop.
  - **Save ICE**: overrides STUN servers and optional TURN credentials used when composing WebRTC peer connections.
- Run the configurator on the host machine while the service is active; the tool shows connection errors if the pipe is unavailable or insufficient rights are present.

## Running the local signaling relay + operator CLI
1. Start the signaling relay (defaults to port 5000):
   - `dotnet run --project src/SignalingServer`
   - Health probe: `curl http://localhost:5000/health`
2. Point the host at the relay WebSocket endpoint (bypass the HTTP resolver by setting an absolute URL):
   - Update `config.json` (generated at `%ProgramData%/P2PRD/config.json` on first run) to set `signaling_resolver_url` to `ws://localhost:5000/ws`.
   - Run the host worker: `dotnet run --project src/Service`.
3. Launch the operator CLI with the same endpoint and host ID (GUID from the host config or logs):
   - `dotnet run --project src/OperatorConsole -- ws://localhost:5000/ws <host-id> <optional-password>`
4. The operator sends `operator_hello`, requests the monitor list, performs authentication automatically when a password is provided, and begins saving incoming PNG frames under `./frames` (WebRTC data-channel or signaling fallback) alongside VP8 video-track snapshots under `./frames/video`.
  - Enter `monitor_switch` commands interactively to change the active monitor on the host during the session (capture follows the active monitor).
  - Input commands are available: `mouse <x 0..1> <y 0..1>` to move the cursor on the active monitor, `click <left|right|middle>` to tap buttons, `wheel <delta>` for scroll, `key <scanCode> <down|up>` for keyboard scan codes, and `cad` to send Ctrl+Alt+Del (secure attention sequence) when running elevated on Windows.
  - When the WebRTC control data channel opens, the operator and host automatically migrate control traffic to it, falling back to signaling if the channel drops.

## Building Windows executables
- Use PowerShell to publish single-file builds (Windows runtime by default, skips trimming):
  - `pwsh ./scripts/publish.ps1 -Runtime win-x64 -Configuration Release -SelfContained`
- Or use Bash with the same defaults:
  - `SELF_CONTAINED=true ./scripts/publish.sh`
- Outputs land under `artifacts/<runtime>/` and include `P2PRD.Service.exe`, `OperatorConsole.exe`, `Configurator.exe`, and `SignalingServer.exe` for installation or distribution on Windows hosts/operators.

### Installing/uninstalling the Windows service
- After publishing, install the host as a Windows service (run PowerShell as Administrator on Windows):
  - `pwsh ./scripts/install-service.ps1 -ExePath "C:/path/to/artifacts/win-x64/P2PRD.Service.exe"`
  - The script stops/removes any prior `P2PRD` instance, recreates it with an automatic start mode, and starts the service unless `-NoStart` is passed.
- To remove the service completely:
  - `pwsh ./scripts/uninstall-service.ps1`

## Current implementation status
- **Working:** Local signaling relay with per-IP connection rate limiting; host handshake flow (hello/auth/monitor list + switch); operator CLI; WebRTC control data channel with ICE trickling/re-offer recovery; dedicated WebRTC frame channel carrying binary PNG payloads (falls back to JSON/base64 when absent); VP8-encoded video pumped over the negotiated WebRTC video track from the live DXGI Desktop Duplication capture loop with GDI fallback; operator captures VP8 video-track frames to `./frames/video` alongside PNG snapshots to validate media delivery; capture and input threads switch to the active input desktop when available to observe UAC/logon screens and inject into the foreground desktop; Windows SendInput-based mouse/keyboard injection bound to the active monitor coordinates with per-monitor DPI awareness enabled at startup plus a Ctrl+Alt+Del (secure attention) command via `SendSAS`; a resolver-driven signaling reconnection loop with exponential backoff when no endpoint is available; a Windows configurator that reaches the secured named pipe to view status and update the password, resolver, or ICE (STUN/TURN) endpoints; and a Windows-only session watcher that logs active console session changes.
- **Not yet implemented:** Hardware-accelerated H.264 encoding, a Windows service install/ACL-hardening story, GUI polish/installer integration, and fully validated UAC/logon desktop handling. A hardened named-pipe configuration surface now exists for SYSTEM/Administrators to set the password and resolver locally, with the WPF configurator consuming it.
- **Implication:** The prototype exercises connection/session logic, capture (including switching to the input desktop where permitted), and input dispatch, negotiates a video track, and pushes frames through WebRTC when possible. It still does not provide low-latency encoded video transport or full production hardening.

## Delivery TODO (tracked in-repo)
- [x] WebRTC transport: stand up a peer connection with STUN, offer/answer exchange over signaling, ICE candidate trickling, and a control data channel. **Done:** host/operator negotiate control + frame data channels, fall back to signaling if unavailable, and the host re-offers automatically when the ICE state drops.
- [x] WebRTC media: attach a real video track sourced from the capture pipeline (DXGI/Desktop Duplication when available, GDI fallback), and migrate away from PNG-over-WebSocket streaming. **Done:** VP8 encoding now feeds the negotiated WebRTC video track using the live capture loop, while data-channel and signaling fallbacks remain for inspection/testing.
- [ ] Input path: ship mouse/keyboard injection bound to the active monitor, and route input over the WebRTC data channel. **In progress:** Operator CLI now emits `input` commands (mouse move, clicks, wheel, keyboard scan codes, and Ctrl+Alt+Del), and the host uses SendInput against the active monitor bounds with per-monitor DPI scaling while issuing secure attention (Ctrl+Alt+Del) via `SendSAS`; injection now attempts to run on the active input desktop for UAC/logon visibility, with further validation still pending.
- [ ] Desktop integration: add GUI configurator + IPC, secure install/ACLs, UAC/logon capture, and reconnection resilience per the spec. **In progress:** Host now writes rolling logs under `%ProgramData%/P2PRD/logs` (10MB per file, up to 10 files) and emits to console when run interactively; resolver polling drives WebSocket reconnects with exponential backoff when endpoints are unavailable; capture threads now attempt to switch onto the active input desktop for UAC/logon visibility; and a WPF configurator fronts the secured named pipe for password/resolver management. Installer/ACL polish and validated UAC/logon capture remain.
