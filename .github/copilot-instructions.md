# GitHub Copilot Instructions for Yumlog

## Project Overview

Darbot Yumlog is a local Windows screen capture, recording, and analysis utility that acts as "vision" for AI. It has two layers: the original lightweight PowerShell/FFmpeg tooling, and a native F# stack (`apps/Yumlog.Native`) that adds hardware HEVC recording, a layered `.mdkg` metadata container, verifiable runbooks, and fully offline on-device reasoning. The tool is designed to be simple, reliable, and robust.

## Core Principles

- **Lightweight**: Keep the codebase minimal and dependencies minimal
- **Simple**: Easy to understand, use, and maintain
- **Efficient**: Optimize for performance using FFmpeg
- **Reliable**: Ensure consistent behavior across runs
- **Robust**: Handle edge cases and errors gracefully
- **Evidence over assertion**: Claims about a recording should carry a resolvable citation

## Architecture

### Directory Structure
- `Skills/`: Reusable PowerShell functions/modules (core functionality)
- `launchers/`: Top-level PowerShell scripts for user interaction
- `apps/Yumlog.Native/`: F# implementation library (capture, encode, mdkg, reasoning)
- `apps/Yumlog.Native.Cli/`: Runnable native CLI entry point
- `docs/`: `NATIVE-CLI.md`, `ON-DEVICE-REASONING.md`
- `config/`: Configuration files (`tools.json`, `yumlog.native.json`)
- `.tools/`: Auto-installed dependencies (FFmpeg)

### Key Components
1. **Record-Screen.ps1**: Desktop video recording using FFmpeg
2. **Capture-Screens.ps1**: Periodic screenshot capture
3. **Run-FFmpeg.ps1**: FFmpeg execution wrapper
4. **yumlog.ps1**: Unified CLI for all operations
5. **yumlog-manager.html**: Web-based UI for managing recordings
6. **WgcCapture.fs / NvencHevc.fs / X265Encoder.fs / HevcMux.fs**: Native capture and HEVC encode path
7. **Mdkg.fs / MdkgIntel.fs**: `.mdkg` container plus timeline, citations, and runbooks
8. **OnDeviceLlm.fs**: Offline model reasoning and live screen watching
9. **SelfTest.fs**: Built-in 16-check correctness suite

## Working on the native stack

- Build `apps/Yumlog.Native.Cli`, not `apps/Yumlog.Native` — the latter is a library.
- `bin/` and `obj/` are committed despite appearing in `.gitignore`. **Rebuild before trusting `selftest`**; a stale binary once reported 15 checks while the source defined 16.
- `selftest` JSON keys are `passed`, `passedCount`, `failedCount`, `cases`.
- CLI JSON output has trailing non-JSON text; slice between the first `{` and last `}`.
- Unknown CLI arguments are ignored silently, so a flag typo exits 0 and does nothing.
- To make a fact visible to `reason`, put it in the grounding string in `OnDeviceLlm.fs`. Runbook proofs are **not** sent to the model.

### Testing the screen-observation features

**Never fabricate screen content to test screen observation.** Generating synthetic full-screen windows proves only that the tool can see the fabrication, and it hijacks the operator's display. Validate `watch` and `follow` against real application activity, with explicit consent.

Prefer control experiments over single passing cases. Frame-rate reporting is verified at 12, 15, and 30 fps precisely because one passing rate cannot distinguish a correct read from a latched constant.

## Development Guidelines

### PowerShell Scripts
- Use `[CmdletBinding()]` for advanced function features
- Set `$ErrorActionPreference = 'Stop'` for proper error handling
- Provide sensible defaults for all parameters
- Include inline comments for complex logic
- Export functions when imported as modules
- Follow existing naming conventions (Verb-Noun)

### Configuration
- All defaults should be in `config/tools.json`
- Support command-line parameter overrides
- Use relative paths for portability

### Testing
- Use Pester for unit tests
- Tests should be in `*.Tests.ps1` files
- Test files should be self-contained
- Compatible with Pester 3.4.0 (Windows PowerShell default)

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
1. Create function in appropriate Skills/*.ps1 file
2. Add launcher script in launchers/ if needed
3. Update yumlog.ps1 with new action (if CLI command)
4. Update config/tools.json with defaults
5. Add tests in Skills/*.Tests.ps1
6. Document in README.md and Tasklist.md

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

- **No telemetry**: All operations are local
- **No network calls**: Except for FFmpeg download during install
- **No data collection**: User data stays on their machine
- **Sandboxed execution**: PowerShell execution policy respected

## Documentation

- Keep README.md concise and user-focused
- Tasklist.md contains detailed technical documentation
- Inline comments for complex PowerShell logic
- Update both when making changes

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
