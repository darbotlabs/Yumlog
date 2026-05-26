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
- `paperboy.cards.list`
- `paperboy.cards.schema`

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
