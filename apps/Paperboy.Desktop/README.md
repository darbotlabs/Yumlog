# Paperboy Desktop

Paperboy Desktop is a Windows-native .NET 11 WPF app for creating and handling
lightweight `.paperboy.zip` bundles.

## Features

- Drag and drop files or folders into a modern Windows UI.
- Create manifest-backed `.paperboy.zip` archives.
- Choose `SmallestSize`, `Optimal`, `Fastest`, or `NoCompression`.
- Inspect bundle manifests without unpacking the payload.
- Unpack bundles to a selected folder.
- Toss bundles to an outbox folder or final destination path.
- Export composable agent-card JSON for A2A/livetile surfaces.
- Copy the MCP stdio launch command for agent integration.
- Use Windows App SDK imagery and modern card/tile styling for showcase demos.
- Run offline Foundry Local metadata tagging for images, videos, audio, and documents.
- Switch to a full-page Foundry Local model-card layer for delivering language,
  vision, audio, and embedding model metadata as Paperboy bundles.
- Preserve SHA-256 hashes, byte counts, and source metadata in
  `paperboy-manifest.json`.

## Build

```powershell
dotnet build .\apps\Paperboy.Desktop\Paperboy.Desktop.csproj -c Release
dotnet build .\apps\Paperboy.Mcp\Paperboy.Mcp.csproj -c Release
```

## Publish

```powershell
dotnet publish .\apps\Paperboy.Desktop\Paperboy.Desktop.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true
```

The published app appears under:

```text
apps\Paperboy.Desktop\bin\Release\net11.0-windows\win-x64\publish\
```

## Bundle format

Paperboy Desktop uses the same bundle format as `launchers\paperboy.ps1`:

```text
bundle.paperboy.zip
├── paperboy-manifest.json
└── payload/
    └── ...
```

The manifest includes source paths, archive paths, last-write timestamps,
lengths, and SHA-256 hashes.

## MCP server

Paperboy includes a companion console MCP host:

```powershell
dotnet run --project .\apps\Paperboy.Mcp\Paperboy.Mcp.csproj
```

It exposes these tools over stdio JSON-RPC/MCP:

- `paperboy.bundle.pack`
- `paperboy.bundle.inspect`
- `paperboy.bundle.unpack`
- `paperboy.bundle.toss`
- `paperboy.foundryLocal.analyze`
- `paperboy.foundryLocal.modelCards`
- `paperboy.cards.list`
- `paperboy.cards.schema`

## Foundry Local offline pipelines

Paperboy expects a running Foundry Local OpenAI-compatible endpoint such as:

```text
http://127.0.0.1:52495/v1
```

Supported pipeline behavior:

- Images: send file metadata and optional image content to a vision-capable
  `/v1/responses` model.
- Videos: optionally sample the first frame with FFmpeg and send that frame plus
  file metadata to a vision-capable model.
- Audio: tag/classify using file metadata; use a Whisper model externally when
  full transcription is required.
- Documents/logs: tag/classify using file metadata and compact prompts.

The result is saved as `paperboy-foundry-metadata-*.json` and can be bundled
with the original payload for offline routing/search.

## Foundry Local model-card delivery

The **Foundry Models** viewport layer represents local model delivery as cards:

- `qwen2.5-0.5b` for fast language metadata and routing
- `qwen3.5-0.8b` for vision-capable image/video frame tagging
- `phi-3.5-mini` for document reasoning and summaries
- `whisper-tiny` for audio transcription workflows
- `qwen3-0.6b-embedding` for semantic indexing/search

Paperboy can export these cards as JSON or bundle them into a `.paperboy.zip`
for delivery to an offline Foundry Local workspace. Model weights are not
redistributed by Paperboy; the cards describe which aliases and pipeline roles a
receiving workspace should load locally.

## Agent-card schema

The app marks major UI surfaces with composable card metadata and ships the card
catalog under:

```text
Assets\AgentCards\paperboy.cards.json
Assets\AgentCards\paperboy.cards.schema.json
```

Cards describe the payload picker, bundle builder, MCP bridge, and livetile
surface so agents can reason over the UI as interactive A2A cards rather than
opaque controls.
