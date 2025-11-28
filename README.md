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
