# Hello World sandbox agent

The `hello-world` "Joker" agent built with Microsoft Agent Framework, hosted on kagent v1.0's
sandbox runtime contract (`Harness` with `spec.byo`), instead of the legacy BYO Deployment
contract used by `samples/hello-world` and `KAgent.MAF`.

## Why a separate sample

kagent v1.0 runs BYO images inside a Substrate sandbox and expects a different contract than
the one `KAgent.Core` / `KAgent.MAF` implement:

| | `samples/hello-world` (legacy) | This sample (kagent v1.0 sandbox) |
|---|---|---|
| Configuration | `AZURE_*`, `KAGENT_URL`, `KAGENT_NAME`, `KAGENT_NAMESPACE` | `KAGENT_CONFIG_JSON` (compiled `ADKConfig`), `KAGENT_AGENT_CARD_JSON` |
| Model access | Azure OpenAI with a real key | OpenAI client; kagent's egress gateway injects the ModelConfig's key |
| A2A | HTTP JSON-RPC on `:8080` | `lf.a2a.v1.A2AService` gRPC (h2c) on `runtime.port` (`:80`) |
| Readiness | `GET /health` | `GET /readyz` on `:8081` |
| Task persistence | Controller REST `/api/tasks`, `/api/sessions` | `kagent.runtime.v1.TaskStoreService` gRPC at `runtime.control_plane_url` |

`Protos/` holds `a2a.proto` and `task_store.proto` from kagent `v1.0.0-alpha14`, with only their
`google.api` / `buf.validate` annotations removed so stock `Grpc.Tools` can compile them.

## Behaviour

- Each message creates a task that is persisted (SUBMITTED → WORKING → artifact → COMPLETED or
  FAILED) and settled through the TaskStore before each event is streamed back.
- Conversation history is rebuilt from the context's completed tasks via `ListTasks`, so it
  survives sandbox checkpoint/restore.
- Model errors fail the task with `Agent execution failed: <cause>`, so the reason shows up in
  the kagent UI instead of a generic message.
- One task runs at a time per session; a concurrent send is rejected with
  `session already has active work`, matching kagent's own runtimes.
- The AgentTemplate's `systemPrompt` replaces the default Joker instructions when set.

## Build

Build for the worker pool's architecture (`linux/amd64` for most clusters; Apple Silicon builds
`arm64` by default):

```sh
docker buildx build . --platform linux/amd64 -f samples/hello-world-sandbox/Dockerfile -t <you>/hello-world-sandbox-agent:latest --push
docker buildx imagetools inspect <you>/hello-world-sandbox-agent:latest   # copy the sha256 digest
```

## Deploy

The image must be pinned by digest, and the AgentTemplate must reference an **OpenAI**
ModelConfig (the sandbox's egress policy and injected key come from it).

```yaml
apiVersion: api.kagent.dev/v1alpha3
kind: Harness
metadata:
  name: my-byo-harness
  namespace: kagent
spec:
  byo: {}
  substrate:
    snapshotPolicy:
      location: s3://ate-snapshots/kagent/
    workerPoolRef:
      name: kagent-default
  workload:
    image: docker.io/<you>/hello-world-sandbox-agent@sha256:<digest>
    command: ["dotnet", "/app/HelloWorldSandbox.dll"]
```

The existing `Agent` (`harnessRef: my-byo-harness`) and `AgentTemplate`
(`modelConfig: default-model-config`) need no changes.
