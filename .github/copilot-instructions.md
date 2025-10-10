# GitHub Copilot Instructions for Yumlog

## Project Overview

Darbot Yumlog is a lightweight, efficient PowerShell-based screen capture and recording utility that acts as "vision" for AI by taking screenshots at set intervals and analyzing them. The tool is designed to be simple, reliable, and robust.

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
