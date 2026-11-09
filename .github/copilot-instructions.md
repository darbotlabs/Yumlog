# GitHub Copilot Instructions for Yumlog

## Project Overview

Darbot Yumlog is a Windows local-first capture and automation toolkit with a native F# CLI, MDKG metadata contracts, Paperboy WPF/MCP capabilities, a local APIM/ACP service foundation, Widget Studio authoring, and PowerShell utilities.

## Core Principles

- **Lightweight**: Keep the codebase minimal and dependencies minimal
- **Simple**: Easy to understand, use, and maintain
- **Efficient**: Optimize for performance using FFmpeg
- **Reliable**: Ensure consistent behavior across runs
- **Robust**: Handle edge cases and errors gracefully

## Architecture

### Directory Structure
- `Skills/`: Reusable PowerShell functions/modules (core functionality)
- `launchers/`: Top-level PowerShell scripts for user interaction
- `config/`: Configuration files (tools.json)
- `.tools/`: Auto-installed dependencies (FFmpeg)
- `apps/Yumlog.Native/`: .NET 10 Windows F# CLI, OCR, MDKG, and extension catalog
- `apps/Paperboy.Desktop/`: .NET 11 WPF UI, capabilities, services, and MCP implementation
- `apps/Paperboy.Mcp/`: Stdio host reusing the Paperboy implementation
- `apps/Yumlog.APIM.Service/`: Loopback HTTP/ACP catalogs and envelope scaffolding
- `apps/Yumlog.WidgetStudio/`: Widget contract validation, preview, and package CLI
- `snap/`: Small F# capture executable used by `yumlog.cmd` when installed

### Key Components
1. **Record-Screen.ps1**: Desktop video recording using FFmpeg
2. **Capture-Screens.ps1**: Periodic screenshot capture
3. **Run-FFmpeg.ps1**: FFmpeg execution wrapper
4. **yumlog.ps1**: Unified CLI for all operations
5. **yumlog-manager.html**: Web-based UI for managing recordings

## Development Guidelines

### PowerShell Scripts
- Use `[CmdletBinding()]` for advanced function features
- Set `$ErrorActionPreference = 'Stop'` for proper error handling
- Provide sensible defaults for all parameters
- Include inline comments for complex logic
- Export functions when imported as modules
- Follow existing naming conventions (Verb-Noun)

### Configuration
- Keep PowerShell configuration in `config/tools.json` and native configuration in `config/yumlog.native.json`
- Preserve backward compatibility for missing native configuration sections
- Keep APIM settings in its component configuration; supply secrets through local environment/configuration, never tracked files
- Support command-line parameter overrides
- Use relative paths for portability

### Testing
- Use Pester for unit tests
- Tests should be in `*.Tests.ps1` files
- Test files should be self-contained
- Paperboy suites use modern Pester; the older screen-skills suite uses Pester 3 syntax and mock files
- Run native extensibility and MDKG round-trip scripts for native contract changes
- Run APIM's temporary loopback round-trip script and Widget Studio `self-test` for their respective changes
- Never install a Windows Service, register MSIX packages, provision OCR models, or capture a user's desktop as an ordinary unit-test step

### FFmpeg Integration
- Auto-install FFmpeg if not present
- Use `gdigrab` for screen capture on Windows
- Prefer `libx264` codec with `ultrafast` preset for speed
- Output to MP4 for videos, PNG for screenshots

### Code Style
- 4-space indentation
- Clear, descriptive variable names
- Minimal line length for readability
- Avoid unnecessary complexity

## Common Tasks

### Adding a New Command
1. Implement in the owning component; use Skills/*.ps1 for PowerShell functions
2. Wire the component's CLI, capability registry, or launcher as appropriate
3. Keep typed contracts, schema validation, and declared invocation behavior consistent
4. Preserve existing defaults and document capability/runtime requirements
5. Extend the existing component test suite
6. Update README.md, Tasklist.md, and the affected component guide

### Modifying Core Functionality
1. Update the relevant Skills/*.ps1 file
2. Ensure backward compatibility or document breaking changes
3. Update tests to cover new behavior
4. Test with various parameter combinations
5. Update documentation

### Adding Configuration Options
1. Add to config/tools.json with sensible defaults
2. Update PowerShell scripts to read new config
3. Document in README.md
4. Update HTML manager UI if user-configurable

## Integration Points

- **CLI**: PowerShell scripts in launchers/
- **Web UI**: yumlog-manager.html for visual management
- **Automation**: Scripts are designed for CI/CD integration
- **AI Tools**: JSON config and structured output for programmatic access

## Testing Strategy

- Unit tests for individual Skills functions
- Integration tests for launcher scripts
- Manual testing for FFmpeg integration
- Cross-version testing with Pester 3.4.0 and 5.x

## Performance Considerations

- Use FFmpeg's hardware acceleration when available
- Minimize file I/O operations
- Keep memory footprint low
- Optimize for fast startup time

## Security & Privacy

- **Local-first**: Recordings, bundles, metadata, and logs can contain private user data
- **Explicit networking**: Dependency/model downloads and configured HTTP connectors may use the network; do not claim all operations are offline
- **Credentials**: Never commit API keys, environment secrets, or signing certificates
- **Publication**: Do not newly stage generated outputs or downloaded third-party archives; inspect explicit staged paths
- **Execution**: A declarative capability is not proof of an executable backend or a sandbox; document implementation limits honestly

## Documentation

- Keep README.md concise and user-focused
- Tasklist.md contains detailed technical documentation
- Component READMEs and the native extension/MDKG guides define component-specific contracts
- Keep historical research notes distinct from current implementation documentation
- Inline comments for complex logic
- Update affected guides alongside source changes; keep examples runnable from the repository root

## Best Practices for AI Agents

When working on this repository:
1. Always check existing tests before making changes
2. Maintain backward compatibility
3. Keep changes minimal and focused
4. Test with actual FFmpeg operations when possible
5. Update documentation alongside code changes
6. Follow the established directory structure
7. Preserve the "no telemetry" and "local execution" principles
8. Keep the tool simple and avoid feature creep
