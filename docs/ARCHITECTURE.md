# System Architecture

This document elaborates on the end-to-end design for the Windows P2P remote desktop application, expanding on the high-level requirements in the repository README.

## Component Overview

- **Signaling Server** (Linux/Windows capable)
  - Hosts WSS endpoint for WebRTC offer/answer exchange and NAT traversal candidates.
  - Pulls its public URL from a dynamic resolver source (e.g., private Gist or S3 JSON) that clients and hosts poll every 5–10 minutes.
  - Implements per-IP rate limiting and returns an explicit error if limits are exceeded.
  - Maintains transient session registry: host availability, active session status, and rejected second-operator attempts with a `HostIsBusy` error code.
- **Windows Service (Host Core)**
  - Runs as **LocalSystem** in Session 0; owns WebRTC stack, capture pipeline (DXGI primary with GDI fallback), and input injection.
  - Persists configuration (ID GUID, Argon2/bcrypt password hash, STUN/TURN settings, resolver URL, and logging policy) with ACL restricted to SYSTEM and Administrators.
  - Provides a named-pipe IPC surface (authenticated via SDDL) for the user-mode configurator.
  - Tracks active Windows sessions (WTS) and switches the capture thread onto the active input desktop where possible to observe UAC/logon scenarios.
- **Configurator GUI (User Mode)**
  - Allows password setup/reset and displays host ID.
  - Communicates with the service via named pipes; GUI never handles plaintext passwords beyond immediate hashing and pipe transmission. Pipe ACLs restrict access to SYSTEM and Administrators.
  - Shows connectivity/resolver status and any lockout timer due to brute-force protection.
- **Operator Client (WPF)**
  - Renders remote video, sends input events over a WebRTC data channel, and exposes a monitor picker.
  - Handles automatic reconnects on ICE failure and persists last-known resolver URL for bootstrap.

## Configuration Model

All configuration is serialized to `%ProgramData%/P2PRD/config.json` with restrictive ACLs. Example:

```json
{
  "host_id": "a2b6c1e6-2f41-4c9e-9e6c-3f72b6d69f70",
  "password_hash": "<argon2id hash>",
  "signaling_resolver_url": "https://example.com/resolver.json",
  "stun": ["stun:stun.l.google.com:19302", "stun:stun1.l.google.com:19302"],
  "turn": { "url": "", "username": "", "credential": "" },
  "logging": { "max_bytes": 10485760, "files": 5 },
  "lockout": { "failed_attempts": 0, "locked_until": null }
}
```

- The service updates the `lockout` section to persist brute-force lock timers across restarts.
- IPC requests from the GUI are validated against the caller session and group membership before mutating this file.
- A named pipe endpoint (`\\.\pipe\P2PRD.Config`) exposes JSON commands for the configurator: `status` (returns host ID, resolver URL, password presence, current STUN/TURN), `set_password` (hashes the supplied value and clears lockout state), `set_resolver` (updates the resolver URL), and `set_ice` (overrides STUN/TURN used when composing ICE servers). The pipe is ACL-restricted to SYSTEM and Administrators on Windows.
- The WPF configurator consumes the pipe to provide four UI actions: refresh status (host ID/password presence/resolver URL/ICE), update password (hash + clear lockout), update resolver URL (feeds the reconnect loop), and update ICE (STUN list + TURN credentials passed through to WebRTC offer creation). It surfaces pipe errors when the service is down or insufficient rights prevent connection.

## Data Channel Protocol (Operator ↔ Host)

Messages are JSON objects with a `type` discriminant. Core verbs:

| Type | Direction | Payload | Notes |
| ---- | --------- | ------- | ----- |
| `auth` | Operator → Host | `{ "password": "..." }` | Password sent once per session over DTLS-protected channel; host validates hash and rate limits. |
| `auth_result` | Host → Operator | `{ "status": "ok" | "locked" | "invalid", "retry_after_ms"?: number }` | Drives UI feedback and lockout timers. |
| `monitor_list` | Host → Operator | `{ "monitors": [{"id": "DISPLAY1", "name": "Dell U2720Q", "size": "3840x2160", "dpi_scale": 1.5 }, ...] }` | Sent after auth and whenever topology changes. |
| `monitor_switch` | Operator → Host | `{ "id": "DISPLAY2" }` | Host switches capture source without renegotiating WebRTC. |
| `input` | Operator → Host | `{ "mouse": { "x": 0.42, "y": 0.17, "buttons": {"left": true}, "wheel": 0 }, "keyboard": { "scan_code": 0x1D, "is_key_down": true } }` | Coordinates normalized to active monitor; host maps to physical pixels with per-monitor DPI. |
| `host_busy` | Host → Operator | `{ "reason": "active_session" }` | Sent to second operator if a session is already active. |
| `ice_state` | Either | `{ "state": "checking" | "connected" | "failed" | "disconnected" }` | For UI surfacing and reconnect triggers. |

The signaling channel mirrors these verbs for the headless prototype. Operators begin with `operator_hello` (session claim), receive `host_hello` (host ID and active monitor), and may request `monitor_list` or `monitor_switch` before WebRTC wiring lands. Single-session enforcement applies equally across signaling and data-channel traffic.

For the current prototype, DXGI Desktop Duplication is preferred; when available, it feeds the capture loop (PNG/WebRTC media) with a GDI fallback when duplication cannot be established. Frames follow the active monitor selection and provide a sanity-check preview saved to disk by the operator CLI; VP8 video-track frames are also decoded and persisted under `./frames/video` for validation alongside PNG captures.

### WebRTC Negotiation
- **Offer/Answer:** Host emits `sdp_offer` (SIPSorcery RTCPeerConnection with STUN servers from config); operator replies with `sdp_answer` after attaching a control data channel.
- **ICE:** Both parties trickle `ice_candidate` messages with `candidate`, `sdp_mid`, and `sdp_mline_index`, forwarding them over the signaling socket.
- **Media Tracks:** Host attaches VP8-capable video track alongside control/frame data channels. Captured BGRA frames are encoded to VP8 and sent over the media track; the legacy PNG-over-signaling path remains as a fallback for diagnostics.
- **State Surfacing:** ICE connection state changes are serialized as `ice_state` messages to keep the UI aware of reconnect needs even while media continues on the negotiated track.

## Session Lifecycle and Reconnects

1. **Resolver Polling**: Service and client poll the resolver URL every 5–10 minutes with exponential backoff on failure. On change, they reconnect WebSocket signaling gracefully and attempt to preserve ongoing P2P sessions; if not possible, they tear down and re-establish.
2. **Authentication**: Host enforces 5-attempt limit; on lockout, it rejects new `auth` messages until `locked_until` elapses.
3. **Single-Session Enforcement**: Host keeps a single active operator token. New operators receive `host_busy` and the WebRTC data channel is closed.
4. **ICE Monitoring**: On `failed`/`disconnected`, the client attempts 3–5 reconnects with incremental backoff while preserving UI state.
5. **Session 0 + Secure Desktop**: Service detects WTS session switches and, when UAC/logon surfaces, switches the capture thread onto the active input desktop before acquiring frames. Input injection respects desktop context and is sandboxed to the authenticated operator token.

> Prototype status: the host now runs a resolver-driven reconnect loop with exponential backoff when no endpoint is available, reconnects the signaling WebSocket when the resolver value changes or the socket drops, and logs active console session transitions for visibility into Session 0/secure-desktop context changes.

## Capture and Input Pipeline

- **Capture Preference**: DXGI Desktop Duplication with GPU hardware acceleration; GDI fallback when DXGI is unavailable (e.g., remote sessions or driver issues). Before capture, the host attempts to switch the thread to the current input desktop so UAC/logon surfaces are visible when permissions allow.
- **Monitor Switching**: Switching capture output updates the duplication source without renegotiating WebRTC. Mouse coordinates are scaled using the selected monitor's DPI and resolution; virtual desktop bounds are not reused. The service queries per-monitor DPI via `GetDpiForMonitor` and enables per-monitor DPI awareness during startup to avoid WinForms/Win32 coordinate virtualization.
- **Frame Encoding**: H.264 preferred, VP8 fallback. Encoder selection favors hardware (Intel Quick Sync/NVENC/AMD AMF) when available, with software fallback and bitrate caps to maintain <500 ms glass-to-glass latency. The current prototype uses software VP8 on top of the DXGI duplication path, falling back to GDI capture when DXGI is unavailable, and will migrate to hardware encoding where possible.
- **Input Injection**: Uses `SendInput`/`InjectKeyboardInput` APIs with safeguards for Secure Desktop. Attempts to switch the worker thread to the active input desktop before injecting so UAC/logon desktops receive events when permitted. Supports mouse move/click/wheel, keyboard scan codes, and secure attention sequence (Ctrl+Alt+Del) via `SendSAS` when running elevated on Windows.

## Logging and Telemetry

- Host service initializes Serilog sinks writing to `%ProgramData%/P2PRD/logs/service-<date>.log` (10 MB per file, `rollOnFileSizeLimit` with up to 10 files retained) and console output for interactive runs.
- Event categories:
  - **Auth**: success, invalid password, lockout start/end.
  - **Network**: resolver update, WebSocket connect/disconnect, ICE transitions.
  - **Capture**: DXGI failures, fallback to GDI, monitor topology changes.
  - **Service**: WTS session changes, desktop switches, unhandled exceptions.
- Log format: structured lines from `ILogger`/Serilog templates; additional sinks (JSON, SIEM) can be added without code changes by adjusting the Serilog configuration.

## Installation and Deployment Notes

- The installer must generate the host ID once and set ACLs on config/log directories.
- Service must start automatically and restart on failure; recovery actions configured in SCM.
- GUI installer registers the configurator for all users but pipes changes to the service running as SYSTEM.
- Ngrok/tunnel configuration for the signaling server should include TLS termination and rate-limit middleware where available.
- Published Windows builds can be registered as a service via `scripts/install-service.ps1` (requires Administrator). The script stops/removes any previous `P2PRD` instance, recreates it with `start=auto`, and starts it unless `-NoStart` is provided; `scripts/uninstall-service.ps1` reverses the registration.

## Open Questions / Next Steps

- Validate the stability of the chosen WebRTC library inside Session 0 with DXGI duplication (Stage 1).
- Confirm Secure Desktop capture path for UAC/logon without deadlocks in the capture thread.
- Decide on Argon2 vs bcrypt parameters to balance security and CPU load in a service context.
- Define precise error codes for signaling and data-channel messages to keep UI consistent.
