# Paperboy Desktop

Paperboy Desktop is a Windows-native .NET 10 WPF app for creating and handling
lightweight `.paperboy.zip` bundles.

## Features

- Drag and drop files or folders into a modern Windows UI.
- Create manifest-backed `.paperboy.zip` archives.
- Choose `SmallestSize`, `Optimal`, `Fastest`, or `NoCompression`.
- Inspect bundle manifests without unpacking the payload.
- Unpack bundles to a selected folder.
- Toss bundles to an outbox folder or final destination path.
- Preserve SHA-256 hashes, byte counts, and source metadata in
  `paperboy-manifest.json`.

## Build

```powershell
dotnet build .\apps\Paperboy.Desktop\Paperboy.Desktop.csproj -c Release
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
apps\Paperboy.Desktop\bin\Release\net10.0-windows\win-x64\publish\
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
