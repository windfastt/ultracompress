# Distributed LAN workers

UltraCompress can fan compression steps to **remote worker agents** over gRPC while keeping the **tray/engine job API** on **localhost-only** `EngineService` (see PR #3).

## Ports

| Listener | Address | Service | Auth |
|----------|---------|---------|------|
| Client | `localhost:17832` | `EngineService` (enqueue, watch jobs) | Loopback only |
| Workers | `0.0.0.0:17833` (configurable) | `WorkerService` | Token file |

Enable in `appsettings.json` or environment:

```json
"Engine": {
  "DistributedWorkers": {
    "Enabled": true,
    "WorkerTokenFile": "C:\\ProgramData\\UltraCompress\\worker.token"
  }
}
```

Copy the same single-line token to every machine (`worker.token`). Lines starting with `#` are ignored.

## Worker agent

```bash
cd src/UltraCompress.WorkerAgent
dotnet run -- --engine 192.168.1.10 --port 17833 --token-file /path/to/worker.token
```

The agent calls `Heartbeat`, long-polls `LeaseStep`, runs the same zstd pipeline as the local CLI, then `ReportStepResult`.

## Path visibility

Leases include **engine-local paths**. LAN workers must see the same files (SMB share, identical drive mapping, etc.) until block-split staging lands in a later phase.

## Failure handling

Lease TTL defaults to **120s** (playbook §4). Expired leases are re-queued; the scheduler can fall back to the local `UltraCompress.Worker` process when `FallbackToLocalProcess` is true.