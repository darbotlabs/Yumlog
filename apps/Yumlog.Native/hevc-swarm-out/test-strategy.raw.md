Agent is idle (waiting for messages). agent_id: test-strategy-1, agent_type: dayour-test, status: idle, description: Lossless + sync test plan, elapsed: 271s, total_turns: 1

[Turn 0]
## Test Summary

Scope under test: `Yumlog.Native record --out-file ... --fps ... --duration ...` in `E:\Yumlog\apps\Yumlog.Native`, targeting the planned native recorder pipeline:

`Windows.Graphics.Capture GPU capture -> HEVC lossless encoder -> MP4 muxer -> synchronized timed metadata track`

Main quality objective: prove that the recorder is not merely visually lossless, but mathematically lossless for the supported truly-lossless pixel path, frame-accurate, metadata-synchronized, backend-capability aware, and realtime on NVENC.

Important alignment notes from the files read:

- Existing tests are Pester `Describe` / `It` style with simple Arrange / Act / Assert.
- Existing repo documentation still describes FFmpeg-based recording.
- Current `Recording.fs` records through FFmpeg `gdigrab` and forces `-pix_fmt yuv420p`; that is not mathematically lossless for desktop RGB/chroma detail.
- The new native implementation must intentionally move away from that path. The tests below are designed to fail against the current FFmpeg/yuv420 implementation and pass only when the native lossless path is implemented.
- `Domain.fs` record shapes that must survive metadata round-trip include `OcrResult`, `OcrLine`, `OcrWord`, `TextBounds`, `UiNavigationHint`, and frame-level timing/index data.

---

# 1. Complete Test Plan Checklist

## 1. Lossless verification

### 1.1 Synthetic source must be deterministic

Assertion:

- The recorder must support a test-only deterministic source mode, enabled only by test environment variables, so the exact source frames are known before encoding.
- Recommended test hook:
  - `YUMLOG_NATIVE_ENABLE_TEST_HOOKS=1`
  - `YUMLOG_NATIVE_TEST_SOURCE=synthetic-yuv444-v1`
  - `YUMLOG_NATIVE_TEST_WIDTH=320`
  - `YUMLOG_NATIVE_TEST_HEIGHT=180`
  - `YUMLOG_NATIVE_TEST_FPS=10`
  - `YUMLOG_NATIVE_TEST_DURATION=2`
  - `YUMLOG_NATIVE_TEST_PATTERN=chroma-stress-counter-v1`

Method:

- Generate the same deterministic YUV444 frame sequence in the Pester harness.
- Each frame contains:
  - high-frequency luma pattern,
  - high-frequency chroma pattern that immediately exposes YUV420/NV12 subsampling,
  - per-frame counter encoded into deterministic pixel regions.

Why this is required:

- Desktop capture timing is nondeterministic enough that a true pixel proof needs a deterministic source seam.
- This should not weaken the product. It should be a test-only source injected before the encoder/muxer layer, exercising the exact encoder and MP4 muxer used by real capture.

Concrete assertion:

- `decoded.raw.yuv444p` SHA-256 equals `reference.raw.yuv444p` SHA-256.
- Additionally, each decoded frame hash equals the corresponding reference frame hash, in order.

Pass condition:

- Every byte of decoded YUV444 equals the generated source YUV444.
- Max absolute byte difference is zero.
- No frame is missing, duplicated, or reordered.

Fail condition:

- Any byte differs.
- Any decoded frame hash is absent, duplicated, or out of order.
- Output reports `yuv420p`, `nv12`, or another chroma-subsampled format.

---

### 1.2 External decoder is allowed only in the test harness

Assertion:

- The product must not depend on FFmpeg.
- The test harness may depend on external decoders to independently verify the product output.

Method:

- Use `ffprobe.exe` and `ffmpeg.exe` only from the Pester test harness.
- `ffmpeg.exe` decodes the produced MP4 to raw `yuv444p`.
- `ffprobe.exe` inspects stream metadata, frame timestamps, pixel format, frame count, and timed metadata packets.

Isolation rule:

- Test dependency is explicit and isolated:
  - `ffmpeg.exe` and `ffprobe.exe` are used only inside `Test-NativeRecording.Tests.ps1`.
  - They are not referenced by `Yumlog.Native` product code.
  - Missing decoder tools fail the lossless verification test with a clear harness setup error, not a product capability error.

Optional fallback:

- If `ffprobe` cannot expose the custom metadata samples cleanly, use Bento4 test-only tools such as `mp4dump.exe` / `mp4extract.exe`.
- Bento4 must also remain a test-only dependency.

---

### 1.3 RGB to YUV444 / YUV420 subtlety must be tested directly

Assertion:

- Lossless HEVC in YUV444 proves lossless preservation of the YUV444 samples given to the encoder.
- Lossless HEVC in YUV420 does not preserve full-resolution chroma from desktop RGB/BGRA.
- Therefore, the test must assert a truly-lossless pixel path:
  - accepted: `yuv444p`, `yuv444p10le`, `gbrp`, `rgb24`, `rgba`, `bgra`, or another documented non-subsampled/reversible path;
  - rejected: `yuv420p`, `nv12`, `p010le`, or any 4:2:0 path.

Method:

- Synthetic frames include one-pixel alternating chroma detail.
- If the encoder uses YUV420/NV12, chroma samples cannot round-trip exactly.
- Test asserts the probed output pixel format is not 4:2:0.
- Test decodes as `yuv444p` and compares against the YUV444 reference bytes.

Concrete assertion:

- `ffprobe.streams[video].pix_fmt` must not be any of:
  - `yuv420p`
  - `nv12`
  - `p010le`
  - any format matching `*420*`
- For the primary test, expected format should be `yuv444p` or an explicitly configured true-lossless format.

---

### 1.4 Negative test for 420 path

Assertion:

- The test suite must prove that the chroma-stress pattern catches a non-lossless 420 path.

Method:

- Add an optional negative test mode:
  - force encoder/muxer to use `YUMLOG_NATIVE_TEST_FORCE_PIXEL_FORMAT=yuv420p`, or equivalent test hook;
  - run the same synthetic sequence;
  - verify that the lossless comparison fails.

Concrete assertion:

- If output is 420, test must fail with:
  - "Output pixel format is yuv420p/nv12/420; this is not mathematically lossless for the tested source."

This prevents a future regression where `lossless` encoder settings are used but the input path silently subsamples chroma.

---

## 2. Frame accuracy

### 2.1 Frame count

Assertion:

- For deterministic synthetic source:
  - `actualFrameCount == fps * duration`.
- For real desktop WGC capture smoke:
  - `actualFrameCount` should be within a documented startup/shutdown tolerance, recommended tolerance: `0` for synthetic, `1` for live desktop smoke only.

Method:

- Use `ffprobe -count_frames` and/or decode raw frame byte length.
- For YUV444 8-bit:
  - `frameBytes = width * height * 3`
  - `decodedFrameCount = decodedRawFileSize / frameBytes`

Concrete assertion:

- With `fps=10` and `duration=2`, expected count is `20`.
- Decoded raw file size must be exactly `20 * width * height * 3`.

---

### 2.2 No dropped or duplicated frames

Assertion:

- The decoded frame sequence must match the generated reference sequence by index.

Method:

- Hash each source frame.
- Hash each decoded frame.
- Compare arrays:
  - same length,
  - same hash at each index,
  - no duplicate decoded hashes unless the reference also has the same duplicate at the same index.

Concrete assertion:

- `decodedFrameHashes[i] == referenceFrameHashes[i]` for every `i`.

---

### 2.3 Monotonic presentation timestamps

Assertion:

- Video frame presentation timestamps must be monotonic non-decreasing.
- For constant-FPS synthetic source, adjacent deltas should be approximately `1 / fps`.

Method:

- Use `ffprobe -show_frames`.
- Read `best_effort_timestamp_time` or `pts_time`.
- Assert:
  - no timestamp decreases;
  - delta tolerance is bounded by MP4 timescale rounding.

Recommended tolerance:

- `timestampToleranceSec = max(0.001, frameDurationSec * 0.10)`
- For 10 fps, tolerance is `0.01` seconds.
- For 30 fps, tolerance is approximately `0.00333` seconds.

Concrete assertion:

- `pts[i] >= pts[i-1]`
- `abs((pts[i] - pts[i-1]) - (1 / fps)) <= timestampToleranceSec`

---

### 2.4 Correct resolution

Assertion:

- Output video stream width and height equal requested test dimensions.

Method:

- Use `ffprobe -show_streams`.

Concrete assertion:

- `width == 320`
- `height == 180`

---

## 3. Metadata sync

### 3.1 One metadata sample per video frame

Assertion:

- Timed metadata track must have exactly one sample per video frame.

Method:

- Use `ffprobe -show_packets -select_streams d` for a data/timed-metadata stream.
- If the muxer uses `metx`, `mett`, `mdta`, or another timed metadata handler that `ffprobe` cannot decode, use test-only Bento4 tools.
- Each metadata sample payload should be UTF-8 JSON or another deterministic binary schema with documented parsing.

Concrete assertion:

- `metadataSampleCount == videoFrameCount`.

---

### 3.2 Metadata sample timestamps match video frame timestamps

Assertion:

- Metadata sample `pts_time` must match the corresponding video frame `pts_time`.

Method:

- Extract video frame timestamps.
- Extract metadata sample timestamps.
- Compare by index and/or by explicit `frameIndex` field in metadata payload.

Recommended tolerance:

- `max(0.001, frameDurationSec * 0.10)` seconds.
- For most MP4 timescales this should be exact or near-exact.

Concrete assertion:

- `abs(metadataPts[i] - videoPts[i]) <= timestampToleranceSec`.

---

### 3.3 Metadata payload round-trips Domain.fs shapes

Assertion:

- Each metadata sample must deserialize into the expected frame metadata shape and preserve:
  - frame index,
  - QPC timestamp or QPC ticks,
  - OCR result,
  - OCR lines,
  - OCR words,
  - text bounds,
  - UI navigation hints.

Expected sample shape, representative:

```json
{
  "frameIndex": 7,
  "qpcTimestamp": 123456789000,
  "timestampUtc": "2026-06-19T12:34:56.789Z",
  "ocr": {
    "provider": "test-synthetic",
    "isAvailable": true,
    "message": "",
    "text": "YUMLOG_SYNC_FRAME_007",
    "lines": [
      {
        "text": "YUMLOG_SYNC_FRAME_007",
        "words": [
          {
            "text": "YUMLOG_SYNC_FRAME_007",
            "confidence": 1.0,
            "boundingBox": {
              "topLeft": { "x": 16.0, "y": 16.0 },
              "topRight": { "x": 216.0, "y": 16.0 },
              "bottomRight": { "x": 216.0, "y": 40.0 },
              "bottomLeft": { "x": 16.0, "y": 40.0 }
            }
          }
        ]
      }
    ]
  },
  "hints": [
    {
      "kind": "button",
      "label": "Next",
      "confidence": 1.0,
      "bounds": {
        "topLeft": { "x": 240.0, "y": 120.0 },
        "topRight": { "x": 300.0, "y": 120.0 },
        "bottomRight": { "x": 300.0, "y": 160.0 },
        "bottomLeft": { "x": 240.0, "y": 160.0 }
      }
    }
  ]
}
```

Concrete assertion:

- JSON parses.
- `frameIndex` is present and equals the expected index.
- `qpcTimestamp` is present and monotonic.
- `ocr.lines[].text` contains the injected known string at the expected frame.
- `hints[].kind`, `hints[].label`, `hints[].confidence`, and `hints[].bounds` exist and have valid values.

---

### 3.4 Known on-screen string sync

Assertion:

- A known string injected at a known frame/time must appear in the metadata sample for that same frame/time.

Method:

- In synthetic test mode, inject:
  - `YUMLOG_NATIVE_TEST_TEXT_AT_FRAME=7`
  - `YUMLOG_NATIVE_TEST_TEXT=YUMLOG_SYNC_FRAME_007`
- The synthetic source renders the string into the frame.
- The metadata path records the OCR result for that frame.

Concrete assertion:

- The metadata sample with `frameIndex == 7` contains OCR text `YUMLOG_SYNC_FRAME_007`.
- Its metadata PTS aligns to video frame 7 PTS within tolerance.

---

## 4. Backend and capability coverage

### 4.1 NVENC present path

Assertion:

- On a machine with NVIDIA T1000/NVENC available, the default or requested NVENC lossless backend succeeds and is mathematically lossless.

Method:

- Detect NVENC capability using the product capability layer, not merely `nvidia-smi`.
- Test may also use `nvidia-smi` as a diagnostic.
- Invoke:
  - `Yumlog.Native record --out-file ... --fps 10 --duration 2 --encoder nvenc-lossless`
- Assert:
  - exit code is zero,
  - output MP4 exists,
  - backend reported in result/status is NVENC,
  - decoded YUV444 hashes match reference,
  - pixel format is true-lossless.

Concrete assertion:

- `backend == "nvenc-lossless"` or equivalent canonical value.
- Lossless test passes.

---

### 4.2 NVENC absent fallback to x265

Assertion:

- If NVENC is unavailable, recorder automatically falls back to x265 lossless and still produces mathematically lossless output.

Method:

- Run in an environment without NVENC, or use a test hook:
  - `YUMLOG_NATIVE_TEST_DISABLE_NVENC=1`
- Invoke:
  - `Yumlog.Native record --out-file ... --fps 10 --duration 2`
- Assert:
  - exit code is zero,
  - backend reported is `x265-lossless`,
  - pixel format remains true-lossless,
  - decoded frame bytes match reference.

Concrete assertion:

- Fallback is automatic.
- No silent downgrade to non-lossless pixel format.

---

### 4.3 Neither backend available

Assertion:

- If neither NVENC lossless nor x265 lossless is available, recorder must fail fast with a capability-gate message.

Method:

- Use a controlled test environment or test hooks:
  - `YUMLOG_NATIVE_TEST_DISABLE_NVENC=1`
  - `YUMLOG_NATIVE_TEST_DISABLE_X265=1`

Concrete assertion:

- Exit code is non-zero.
- No partial MP4 is reported as successful.
- Error message contains:
  - "No lossless HEVC encoder available"
  - "NVENC unavailable"
  - "x265 unavailable"
  - remediation hint, such as install/enable x265 fallback or run on NVENC-capable GPU.

---

## 5. Performance and realtime smoke

### 5.1 NVENC realtime smoke

Assertion:

- NVENC lossless keeps up with realtime at the target capture resolution.

Method:

- Record deterministic synthetic or real WGC source at:
  - CI smoke: 1280x720, 30 fps, 5 seconds.
  - Hardware validation: native target resolution, for example 1920x1080 or 4K, 30/60 fps.
- Measure wall-clock duration around CLI invocation.
- Compute:
  - `encodedFrames / elapsedSeconds`
  - `throughputFps >= requestedFps * 0.95`
  - no dropped frames.

Concrete assertion:

- For NVENC:
  - wall time does not exceed `duration * 1.20` for smoke;
  - decoded frame count equals expected;
  - frame hashes match for synthetic mode.

---

### 5.2 x265 fallback performance classification

Assertion:

- x265 lossless fallback must remain mathematically lossless, but it is not required to be realtime at 4K.

Method:

- Run same lossless verification through x265.
- Record throughput.

Concrete assertion:

- Lossless correctness is required.
- Realtime is not required for x265 at 4K.
- If throughput is below realtime, classify as:
  - "non-realtime fallback"
  - not a release blocker for correctness.

---

# 2. Runnable Pester Test Scaffolding: `Test-NativeRecording.Tests.ps1`

Place this file under `E:\Yumlog\Skills\Test-NativeRecording.Tests.ps1`.

This scaffold follows the repo’s existing Pester style: `BeforeAll`, `AfterAll`, `Describe`, `It`, temp directory setup, simple Arrange / Act / Assert, and compatibility-friendly assertion helpers.

The scaffold intentionally uses external `ffmpeg.exe` and `ffprobe.exe` only as test harness dependencies. The product under test must not call FFmpeg.

```powershell
# Pester tests for Yumlog.Native native no-FFmpeg lossless recording.
# Test harness external dependencies:
# - ffmpeg.exe: used only by tests to decode MP4 to raw frames for independent verification.
# - ffprobe.exe: used only by tests to inspect MP4 streams, frames, packets, and metadata.
#
# These tools must not be product dependencies.
#
# Expected product test hooks for deterministic lossless validation:
# - YUMLOG_NATIVE_ENABLE_TEST_HOOKS=1
# - YUMLOG_NATIVE_TEST_SOURCE=synthetic-yuv444-v1
# - YUMLOG_NATIVE_TEST_WIDTH=<width>
# - YUMLOG_NATIVE_TEST_HEIGHT=<height>
# - YUMLOG_NATIVE_TEST_FPS=<fps>
# - YUMLOG_NATIVE_TEST_DURATION=<duration seconds>
# - YUMLOG_NATIVE_TEST_PATTERN=chroma-stress-counter-v1
# - YUMLOG_NATIVE_TEST_TEXT_AT_FRAME=<frame index>
# - YUMLOG_NATIVE_TEST_TEXT=<known string>
#
# The synthetic-yuv444-v1 generator in the product must use the same formula as
# New-SyntheticYuv444Reference below. It should inject frames before the encoder,
# so the production encoder and MP4 muxer are still exercised.

BeforeAll {
    $script:RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
    $script:NativeProject = Join-Path $script:RepoRoot "apps\Yumlog.Native\Yumlog.Native.fsproj"
    $script:TestDir = Join-Path ([System.IO.Path]::GetTempPath()) ("yumlog-native-recording-tests-" + [System.Guid]::NewGuid().ToString("N"))

    New-Item -ItemType Directory -Path $script:TestDir -Force | Out-Null

    function Assert-True {
        param(
            [bool] $Condition,
            [string] $Message
        )

        if (-not $Condition) {
            throw $Message
        }
    }

    function Assert-Equal {
        param(
            $Actual,
            $Expected,
            [string] $Message
        )

        if ($Actual -ne $Expected) {
            throw "$Message Expected=[$Expected] Actual=[$Actual]"
        }
    }

    function Assert-ApproximatelyEqual {
        param(
            [double] $Actual,
            [double] $Expected,
            [double] $Tolerance,
            [string] $Message
        )

        $delta = [Math]::Abs($Actual - $Expected)
        if ($delta -gt $Tolerance) {
            throw "$Message Expected=[$Expected] Actual=[$Actual] Tolerance=[$Tolerance] Delta=[$delta]"
        }
    }

    function Find-Tool {
        param([string] $Name)

        $cmd = Get-Command $Name -ErrorAction SilentlyContinue
        if ($cmd) {
            return $cmd.Source
        }

        return $null
    }

    function Invoke-ProcessChecked {
        param(
            [string] $FileName,
            [string[]] $Arguments,
            [string] $WorkingDirectory = $script:RepoRoot,
            [int[]] $AllowedExitCodes = @(0)
        )

        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $FileName
        $psi.WorkingDirectory = $WorkingDirectory
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true

        foreach ($arg in $Arguments) {
            [void] $psi.ArgumentList.Add($arg)
        }

        $proc = New-Object System.Diagnostics.Process
        $proc.StartInfo = $psi

        [void] $proc.Start()
        $stdout = $proc.StandardOutput.ReadToEnd()
        $stderr = $proc.StandardError.ReadToEnd()
        $proc.WaitForExit()

        $result = [pscustomobject]@{
            ExitCode = $proc.ExitCode
            StdOut = $stdout
            StdErr = $stderr
            Command = $FileName
            Arguments = $Arguments
        }

        if ($AllowedExitCodes -notcontains $proc.ExitCode) {
            throw ("Process failed. Command={0} Args={1} ExitCode={2} StdOut={3} StdErr={4}" -f `
                $FileName, ($Arguments -join " "), $proc.ExitCode, $stdout, $stderr)
        }

        return $result
    }

    function Invoke-NativeRecord {
        param(
            [string] $OutFile,
            [int] $Fps,
            [int] $DurationSec,
            [string] $Encoder
        )

        # Prefer a published/installed CLI if the caller provides it.
        # Example:
        #   $env:YUMLOG_NATIVE_CMD = "E:\Yumlog\apps\Yumlog.Native\bin\Release\net10.0-windows10.0.26100.0\win-x64\Yumlog.Native.exe"
        $nativeCmd = $env:YUMLOG_NATIVE_CMD

        $recordArgs = @(
            "record",
            "--out-file", $OutFile,
            "--fps", ([string] $Fps),
            "--duration", ([string] $DurationSec)
        )

        if ($Encoder -and $Encoder.Trim().Length -gt 0) {
            $recordArgs += @("--encoder", $Encoder)
        }

        if ($nativeCmd -and (Test-Path $nativeCmd)) {
            return Invoke-ProcessChecked -FileName $nativeCmd -Arguments $recordArgs
        }

        $dotnetArgs = @(
            "run",
            "--project", $script:NativeProject,
            "-c", "Release",
            "--"
        ) + $recordArgs

        return Invoke-ProcessChecked -FileName "dotnet" -Arguments $dotnetArgs
    }

    function Set-TestEnv {
        param(
            [int] $Width,
            [int] $Height,
            [int] $Fps,
            [int] $DurationSec,
            [int] $TextAtFrame,
            [string] $Text,
            [string] $DisableNvenc,
            [string] $DisableX265
        )

        $old = @{}
        $names = @(
            "YUMLOG_NATIVE_ENABLE_TEST_HOOKS",
            "YUMLOG_NATIVE_TEST_SOURCE",
            "YUMLOG_NATIVE_TEST_WIDTH",
            "YUMLOG_NATIVE_TEST_HEIGHT",
            "YUMLOG_NATIVE_TEST_FPS",
            "YUMLOG_NATIVE_TEST_DURATION",
            "YUMLOG_NATIVE_TEST_PATTERN",
            "YUMLOG_NATIVE_TEST_TEXT_AT_FRAME",
            "YUMLOG_NATIVE_TEST_TEXT",
            "YUMLOG_NATIVE_TEST_DISABLE_NVENC",
            "YUMLOG_NATIVE_TEST_DISABLE_X265"
        )

        foreach ($name in $names) {
            $old[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
        }

        [Environment]::SetEnvironmentVariable("YUMLOG_NATIVE_ENABLE_TEST_HOOKS", "1", "Process")
        [Environment]::SetEnvironmentVariable("YUMLOG_NATIVE_TEST_SOURCE", "synthetic-yuv444-v1", "Process")
        [Environment]::SetEnvironmentVariable("YUMLOG_NATIVE_TEST_WIDTH", ([string] $Width), "Process")
        [Environment]::SetEnvironmentVariable("YUMLOG_NATIVE_TEST_HEIGHT", ([string] $Height), "Process")
        [Environment]::SetEnvironmentVariable("YUMLOG_NATIVE_TEST_FPS", ([string] $Fps), "Process")
        [Environment]::SetEnvironmentVariable("YUMLOG_NATIVE_TEST_DURATION", ([string] $DurationSec), "Process")
        [Environment]::SetEnvironmentVariable("YUMLOG_NATIVE_TEST_PATTERN", "chroma-stress-counter-v1", "Process")
        [Environment]::SetEnvironmentVariable("YUMLOG_NATIVE_TEST_TEXT_AT_FRAME", ([string] $TextAtFrame), "Process")
        [Environment]::SetEnvironmentVariable("YUMLOG_NATIVE_TEST_TEXT", $Text, "Process")

        if ($DisableNvenc) {
            [Environment]::SetEnvironmentVariable("YUMLOG_NATIVE_TEST_DISABLE_NVENC", $DisableNvenc, "Process")
        }

        if ($DisableX265) {
            [Environment]::SetEnvironmentVariable("YUMLOG_NATIVE_TEST_DISABLE_X265", $DisableX265, "Process")
        }

        return $old
    }

    function Restore-TestEnv {
        param([hashtable] $Old)

        foreach ($key in $Old.Keys) {
            [Environment]::SetEnvironmentVariable($key, $Old[$key], "Process")
        }
    }

    function New-SyntheticYuv444Reference {
        param(
            [string] $Path,
            [int] $Width,
            [int] $Height,
            [int] $FrameCount
        )

        # Deterministic YUV444 8-bit pattern.
        # Plane order per frame: Y plane, U plane, V plane.
        #
        # The U/V planes deliberately contain one-pixel high-frequency chroma.
        # Any 4:2:0 path will destroy this pattern and fail the hash comparison.
        $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)

        try {
            $planeBytes = $Width * $Height
            $y = New-Object byte[] $planeBytes
            $u = New-Object byte[] $planeBytes
            $v = New-Object byte[] $planeBytes

            for ($frame = 0; $frame -lt $FrameCount; $frame++) {
                for ($yy = 0; $yy -lt $Height; $yy++) {
                    for ($xx = 0; $xx -lt $Width; $xx++) {
                        $offset = ($yy * $Width) + $xx

                        # Luma gradient plus frame counter.
                        $y[$offset] = [byte] (($xx + (3 * $yy) + (17 * $frame)) % 256)

                        # Chroma stress: one-pixel alternating chroma, frame-dependent.
                        $u[$offset] = [byte] (((($xx % 2) * 255) -bxor (($frame * 11) % 256)) % 256)
                        $v[$offset] = [byte] (((($yy % 2) * 255) -bxor (($frame * 7) % 256)) % 256)
                    }
                }

                # Encode a small frame counter stripe into luma for easier visual diagnostics.
                # This is not needed for the hash proof, but helps humans inspect decoded frames.
                $stripeHeight = [Math]::Min(8, $Height)
                for ($yy2 = 0; $yy2 -lt $stripeHeight; $yy2++) {
                    for ($xx2 = 0; $xx2 -lt $Width; $xx2++) {
                        $bit = ($frame -shr ($xx2 % 8)) -band 1
                        $y[($yy2 * $Width) + $xx2] = [byte] ($(if ($bit -eq 1) { 240 } else { 16 }))
                    }
                }

                $fs.Write($y, 0, $y.Length)
                $fs.Write($u, 0, $u.Length)
                $fs.Write($v, 0, $v.Length)
            }
        }
        finally {
            $fs.Dispose()
        }

        return $Path
    }

    function Get-FileSha256 {
        param([string] $Path)

        return (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToLowerInvariant()
    }

    function Get-RawYuv444FrameHashes {
        param(
            [string] $Path,
            [int] $Width,
            [int] $Height
        )

        $frameBytes = $Width * $Height * 3
        $length = (Get-Item -LiteralPath $Path).Length

        if (($length % $frameBytes) -ne 0) {
            throw "Raw YUV444 file length is not an even number of frames. Path=$Path Length=$length FrameBytes=$frameBytes"
        }

        $frameCount = [int] ($length / $frameBytes)
        $hashes = New-Object System.Collections.Generic.List[string]
        $buffer = New-Object byte[] $frameBytes

        $fs = [System.IO.File]::OpenRead($Path)
        try {
            for ($i = 0; $i -lt $frameCount; $i++) {
                $read = 0
                while ($read -lt $frameBytes) {
                    $n = $fs.Read($buffer, $read, $frameBytes - $read)
                    if ($n -le 0) {
                        throw "Unexpected end of raw file at frame $i"
                    }
                    $read += $n
                }

                $sha = [System.Security.Cryptography.SHA256]::Create()
                try {
                    $bytes = $sha.ComputeHash($buffer)
                    $hex = -join ($bytes | ForEach-Object { $_.ToString("x2") })
                    [void] $hashes.Add($hex)
                }
                finally {
                    $sha.Dispose()
                }
            }
        }
        finally {
            $fs.Dispose()
        }

        return ,$hashes.ToArray()
    }

    function Get-FfprobeJson {
        param(
            [string] $FfprobePath,
            [string[]] $Arguments
        )

        $args = @("-v", "error") + $Arguments + @("-of", "json")
        $result = Invoke-ProcessChecked -FileName $FfprobePath -Arguments $args
        return $result.StdOut | ConvertFrom-Json
    }

    function Decode-Mp4ToRawYuv444 {
        param(
            [string] $FfmpegPath,
            [string] $InputPath,
            [string] $OutputRawPath
        )

        if (Test-Path $OutputRawPath) {
            Remove-Item -LiteralPath $OutputRawPath -Force
        }

        $args = @(
            "-v", "error",
            "-y",
            "-i", $InputPath,
            "-map", "0:v:0",
            "-f", "rawvideo",
            "-pix_fmt", "yuv444p",
            $OutputRawPath
        )

        [void] (Invoke-ProcessChecked -FileName $FfmpegPath -Arguments $args)

        Assert-True (Test-Path $OutputRawPath) "Decoded raw file was not created."
        return $OutputRawPath
    }

    function Get-VideoStreamInfo {
        param(
            [string] $FfprobePath,
            [string] $Mp4Path
        )

        $json = Get-FfprobeJson -FfprobePath $FfprobePath -Arguments @(
            "-count_frames",
            "-show_streams",
            $Mp4Path
        )

        $video = $json.streams | Where-Object { $_.codec_type -eq "video" } | Select-Object -First 1
        if (-not $video) {
            throw "No video stream found in $Mp4Path"
        }

        return $video
    }

    function Get-VideoFrames {
        param(
            [string] $FfprobePath,
            [string] $Mp4Path
        )

        $json = Get-FfprobeJson -FfprobePath $FfprobePath -Arguments @(
            "-select_streams", "v:0",
            "-show_frames",
            "-show_entries", "frame=best_effort_timestamp_time,pkt_pts_time,pts_time,pkt_duration_time,width,height,pict_type",
            $Mp4Path
        )

        $frames = @()
        if ($json.frames) {
            $frames = @($json.frames | Where-Object { $_.media_type -eq $null -or $_.pict_type -ne $null })
        }

        return ,$frames
    }

    function Get-FramePtsSeconds {
        param($Frame)

        if ($Frame.best_effort_timestamp_time) {
            return [double] $Frame.best_effort_timestamp_time
        }

        if ($Frame.pts_time) {
            return [double] $Frame.pts_time
        }

        if ($Frame.pkt_pts_time) {
            return [double] $Frame.pkt_pts_time
        }

        throw "Frame did not contain a usable timestamp."
    }

    function Convert-FfprobeHexDumpToUtf8 {
        param([string] $Data)

        if (-not $Data) {
            return ""
        }

        # ffprobe -show_data often emits lines like:
        # 00000000: 7b22 6672 616d 6549 6e64 6578 223a 307d  {"frameIndex":0}
        $bytes = New-Object System.Collections.Generic.List[byte]
        $lines = $Data -split "`n"

        foreach ($line in $lines) {
            if ($line -match "^\s*[0-9a-fA-F]{8}:\s*(.+)$") {
                $rest = $Matches[1]

                # Keep only the hex part before the ASCII rendering.
                # Groups are typically two or four hex characters separated by spaces.
                $hexMatches = [regex]::Matches($rest, "\b[0-9a-fA-F]{2}\b")
                foreach ($m in $hexMatches) {
                    [void] $bytes.Add([Convert]::ToByte($m.Value, 16))
                }
            }
        }

        if ($bytes.Count -eq 0) {
            return $Data
        }

        $arr = $bytes.ToArray()

        # Trim trailing null bytes if the metadata sample is padded.
        $end = $arr.Length
        while ($end -gt 0 -and $arr[$end - 1] -eq 0) {
            $end--
        }

        if ($end -lt $arr.Length) {
            $trimmed = New-Object byte[] $end
            [Array]::Copy($arr, $trimmed, $end)
            $arr = $trimmed
        }

        return [System.Text.Encoding]::UTF8.GetString($arr)
    }

    function Get-TimedMetadataSamples {
        param(
            [string] $FfprobePath,
            [string] $Mp4Path
        )

        # This assumes the timed metadata track is exposed by ffprobe as a data stream.
        # If the muxer uses a handler that ffprobe does not expose as d:0, replace this
        # helper with Bento4 mp4dump/mp4extract test-only parsing.
        $json = Get-FfprobeJson -FfprobePath $FfprobePath -Arguments @(
            "-select_streams", "d",
            "-show_packets",
            "-show_data",
            "-show_entries", "packet=stream_index,pts_time,dts_time,data",
            $Mp4Path
        )

        $samples = New-Object System.Collections.Generic.List[object]

        if (-not $json.packets) {
            return ,$samples.ToArray()
        }

        foreach ($packet in $json.packets) {
            $pts = $null
            if ($packet.pts_time) {
                $pts = [double] $packet.pts_time
            }
            elseif ($packet.dts_time) {
                $pts = [double] $packet.dts_time
            }
            else {
                throw "Metadata packet did not contain pts_time or dts_time."
            }

            $text = Convert-FfprobeHexDumpToUtf8 -Data $packet.data

            $payload = $null
            try {
                $payload = $text | ConvertFrom-Json
            }
            catch {
                throw "Metadata packet was not valid UTF-8 JSON. Text=$text"
            }

            [void] $samples.Add([pscustomobject]@{
                Pts = $pts
                Text = $text
                Payload = $payload
            })
        }

        return ,$samples.ToArray()
    }

    function Assert-VideoTimestamps {
        param(
            [object[]] $Frames,
            [int] $Fps
        )

        Assert-True ($Frames.Count -gt 0) "No video frames were found."

        $frameDuration = 1.0 / [double] $Fps
        $tolerance = [Math]::Max(0.001, $frameDuration * 0.10)

        $previous = $null
        for ($i = 0; $i -lt $Frames.Count; $i++) {
            $pts = Get-FramePtsSeconds -Frame $Frames[$i]

            if ($previous -ne $null) {
                Assert-True ($pts -ge $previous) "Video timestamps are not monotonic at frame $i. Previous=$previous Current=$pts"

                $delta = $pts - $previous
                Assert-ApproximatelyEqual -Actual $delta -Expected $frameDuration -Tolerance $tolerance `
                    -Message "Unexpected frame timestamp delta at frame $i."
            }

            $previous = $pts
        }
    }

    function Assert-LosslessRoundTrip {
        param(
            [string] $Mp4Path,
            [string] $ReferenceRawPath,
            [string] $DecodedRawPath,
            [string] $FfmpegPath,
            [string] $FfprobePath,
            [int] $Width,
            [int] $Height,
            [int] $ExpectedFrameCount
        )

        $video = Get-VideoStreamInfo -FfprobePath $FfprobePath -Mp4Path $Mp4Path

        Assert-Equal -Actual ([int] $video.width) -Expected $Width -Message "Video width mismatch."
        Assert-Equal -Actual ([int] $video.height) -Expected $Height -Message "Video height mismatch."

        $pixFmt = [string] $video.pix_fmt
        Assert-True ($pixFmt -and $pixFmt.Length -gt 0) "Video pixel format was not reported by ffprobe."

        $notLosslessFormats = @("yuv420p", "nv12", "p010le")
        Assert-True ($notLosslessFormats -notcontains $pixFmt) "Output pixel format is $pixFmt. 4:2:0/NV12 is not mathematically lossless for this test."
        Assert-True ($pixFmt -notmatch "420") "Output pixel format is $pixFmt. Any 4:2:0 path is rejected by this lossless test."

        [void] (Decode-Mp4ToRawYuv444 -FfmpegPath $FfmpegPath -InputPath $Mp4Path -OutputRawPath $DecodedRawPath)

        $frameBytes = $Width * $Height * 3
        $decodedLength = (Get-Item -LiteralPath $DecodedRawPath).Length
        $expectedLength = [int64] $ExpectedFrameCount * [int64] $frameBytes

        Assert-Equal -Actual $decodedLength -Expected $expectedLength -Message "Decoded raw byte length does not match expected frame count."

        $referenceHash = Get-FileSha256 -Path $ReferenceRawPath
        $decodedHash = Get-FileSha256 -Path $DecodedRawPath

        Assert-Equal -Actual $decodedHash -Expected $referenceHash -Message "Decoded YUV444 bytes do not exactly match synthetic reference."

        $referenceFrameHashes = Get-RawYuv444FrameHashes -Path $ReferenceRawPath -Width $Width -Height $Height
        $decodedFrameHashes = Get-RawYuv444FrameHashes -Path $DecodedRawPath -Width $Width -Height $Height

        Assert-Equal -Actual $decodedFrameHashes.Count -Expected $referenceFrameHashes.Count -Message "Decoded frame count does not match reference frame count."

        for ($i = 0; $i -lt $referenceFrameHashes.Count; $i++) {
            Assert-Equal -Actual $decodedFrameHashes[$i] -Expected $referenceFrameHashes[$i] -Message "Frame hash mismatch at frame index $i."
        }
    }
}

AfterAll {
    Remove-Item -LiteralPath $script:TestDir -Recurse -Force -ErrorAction SilentlyContinue
}

Describe "Yumlog.Native native recording lossless verification" {
    It "proves HEVC lossless round-trip byte-for-byte with a synthetic YUV444 chroma-stress sequence" {
        # Arrange
        $ffmpeg = Find-Tool "ffmpeg.exe"
        $ffprobe = Find-Tool "ffprobe.exe"

        Assert-True ($ffmpeg -ne $null) "ffmpeg.exe is required by this test harness only. It must not be a product dependency."
        Assert-True ($ffprobe -ne $null) "ffprobe.exe is required by this test harness only. It must not be a product dependency."

        $width = 320
        $height = 180
        $fps = 10
        $durationSec = 2
        $expectedFrames = $fps * $durationSec
        $knownTextFrame = 7
        $knownText = "YUMLOG_SYNC_FRAME_007"

        $outFile = Join-Path $script:TestDir "lossless-roundtrip.mp4"
        $referenceRaw = Join-Path $script:TestDir "reference.yuv444p"
        $decodedRaw = Join-Path $script:TestDir "decoded.yuv444p"

        [void] (New-SyntheticYuv444Reference -Path $referenceRaw -Width $width -Height $height -FrameCount $expectedFrames)

        $oldEnv = Set-TestEnv -Width $width -Height $height -Fps $fps -DurationSec $durationSec `
            -TextAtFrame $knownTextFrame -Text $knownText

        try {
            # Act
            [void] (Invoke-NativeRecord -OutFile $outFile -Fps $fps -DurationSec $durationSec -Encoder "auto-lossless")

            # Assert
            Assert-True (Test-Path $outFile) "Recorder did not create the output MP4."

            Assert-LosslessRoundTrip -Mp4Path $outFile `
                -ReferenceRawPath $referenceRaw `
                -DecodedRawPath $decodedRaw `
                -FfmpegPath $ffmpeg `
                -FfprobePath $ffprobe `
                -Width $width `
                -Height $height `
                -ExpectedFrameCount $expectedFrames
        }
        finally {
            Restore-TestEnv -Old $oldEnv
        }
    }
}

Describe "Yumlog.Native native recording frame accuracy" {
    It "records the exact synthetic frame count with correct resolution and monotonic timestamps" {
        # Arrange
        $ffmpeg = Find-Tool "ffmpeg.exe"
        $ffprobe = Find-Tool "ffprobe.exe"

        Assert-True ($ffmpeg -ne $null) "ffmpeg.exe is required by this test harness only."
        Assert-True ($ffprobe -ne $null) "ffprobe.exe is required by this test harness only."

        $width = 320
        $height = 180
        $fps = 10
        $durationSec = 2
        $expectedFrames = $fps * $durationSec
        $knownTextFrame = 7
        $knownText = "YUMLOG_SYNC_FRAME_007"

        $outFile = Join-Path $script:TestDir "frame-accuracy.mp4"
        $decodedRaw = Join-Path $script:TestDir "frame-accuracy-decoded.yuv444p"

        $oldEnv = Set-TestEnv -Width $width -Height $height -Fps $fps -DurationSec $durationSec `
            -TextAtFrame $knownTextFrame -Text $knownText

        try {
            # Act
            [void] (Invoke-NativeRecord -OutFile $outFile -Fps $fps -DurationSec $durationSec -Encoder "auto-lossless")

            # Assert
            Assert-True (Test-Path $outFile) "Recorder did not create the output MP4."

            $video = Get-VideoStreamInfo -FfprobePath $ffprobe -Mp4Path $outFile
            Assert-Equal -Actual ([int] $video.width) -Expected $width -Message "Video width mismatch."
            Assert-Equal -Actual ([int] $video.height) -Expected $height -Message "Video height mismatch."

            [void] (Decode-Mp4ToRawYuv444 -FfmpegPath $ffmpeg -InputPath $outFile -OutputRawPath $decodedRaw)

            $frameBytes = $width * $height * 3
            $decodedLength = (Get-Item -LiteralPath $decodedRaw).Length
            $actualFrames = [int] ($decodedLength / $frameBytes)

            Assert-Equal -Actual $actualFrames -Expected $expectedFrames -Message "Synthetic recording frame count mismatch."

            $frames = Get-VideoFrames -FfprobePath $ffprobe -Mp4Path $outFile
            Assert-Equal -Actual $frames.Count -Expected $expectedFrames -Message "ffprobe video frame count mismatch."
            Assert-VideoTimestamps -Frames $frames -Fps $fps
        }
        finally {
            Restore-TestEnv -Old $oldEnv
        }
    }
}

Describe "Yumlog.Native native recording metadata synchronization" {
    It "writes one timed metadata sample per frame with matching timestamps and synchronized OCR text" {
        # Arrange
        $ffprobe = Find-Tool "ffprobe.exe"

        Assert-True ($ffprobe -ne $null) "ffprobe.exe is required by this test harness only."

        $width = 320
        $height = 180
        $fps = 10
        $durationSec = 2
        $expectedFrames = $fps * $durationSec
        $knownTextFrame = 7
        $knownText = "YUMLOG_SYNC_FRAME_007"
        $frameDuration = 1.0 / [double] $fps
        $timestampTolerance = [Math]::Max(0.001, $frameDuration * 0.10)

        $outFile = Join-Path $script:TestDir "metadata-sync.mp4"

        $oldEnv = Set-TestEnv -Width $width -Height $height -Fps $fps -DurationSec $durationSec `
            -TextAtFrame $knownTextFrame -Text $knownText

        try {
            # Act
            [void] (Invoke-NativeRecord -OutFile $outFile -Fps $fps -DurationSec $durationSec -Encoder "auto-lossless")

            # Assert
            Assert-True (Test-Path $outFile) "Recorder did not create the output MP4."

            $videoFrames = Get-VideoFrames -FfprobePath $ffprobe -Mp4Path $outFile
            Assert-Equal -Actual $videoFrames.Count -Expected $expectedFrames -Message "Video frame count mismatch."

            $metadataSamples = Get-TimedMetadataSamples -FfprobePath $ffprobe -Mp4Path $outFile
            Assert-Equal -Actual $metadataSamples.Count -Expected $videoFrames.Count -Message "Timed metadata sample count must equal video frame count."

            for ($i = 0; $i -lt $videoFrames.Count; $i++) {
                $videoPts = Get-FramePtsSeconds -Frame $videoFrames[$i]
                $metadataPts = [double] $metadataSamples[$i].Pts

                Assert-ApproximatelyEqual -Actual $metadataPts -Expected $videoPts -Tolerance $timestampTolerance `
                    -Message "Metadata PTS did not align with video PTS at frame $i."

                $payload = $metadataSamples[$i].Payload

                Assert-True ($payload.frameIndex -ne $null) "Metadata payload missing frameIndex at sample $i."
                Assert-Equal -Actual ([int] $payload.frameIndex) -Expected $i -Message "Metadata frameIndex mismatch at sample $i."

                Assert-True ($payload.qpcTimestamp -ne $null) "Metadata payload missing qpcTimestamp at sample $i."
                Assert-True ($payload.ocr -ne $null) "Metadata payload missing ocr object at sample $i."
                Assert-True ($payload.ocr.lines -ne $null) "Metadata payload missing ocr.lines at sample $i."
                Assert-True ($payload.hints -ne $null) "Metadata payload missing hints at sample $i."
            }

            $syncSample = $metadataSamples | Where-Object { [int] $_.Payload.frameIndex -eq $knownTextFrame } | Select-Object -First 1
            Assert-True ($syncSample -ne $null) "Could not find metadata sample for known text frame $knownTextFrame."

            $ocrText = [string] $syncSample.Payload.ocr.text
            if (-not $ocrText -or $ocrText.Length -eq 0) {
                $ocrText = (($syncSample.Payload.ocr.lines | ForEach-Object { $_.text }) -join " ")
            }

            Assert-True ($ocrText.Contains($knownText)) "Known on-screen OCR text was not present in synchronized metadata. Expected text=$knownText Actual text=$ocrText"

            $syncVideoPts = Get-FramePtsSeconds -Frame $videoFrames[$knownTextFrame]
            Assert-ApproximatelyEqual -Actual ([double] $syncSample.Pts) -Expected $syncVideoPts -Tolerance $timestampTolerance `
                -Message "Known text metadata sample did not align to expected video frame PTS."
        }
        finally {
            Restore-TestEnv -Old $oldEnv
        }
    }
}

Describe "Yumlog.Native native recording backend capability gates" {
    It "uses NVENC lossless when requested and available" {
        # Arrange
        # This is a hardware-dependent validation test. It should run on the NVIDIA T1000 validation machine.
        # The lossless assertion reuses the same byte-for-byte proof as the generic test.
        $ffmpeg = Find-Tool "ffmpeg.exe"
        $ffprobe = Find-Tool "ffprobe.exe"

        Assert-True ($ffmpeg -ne $null) "ffmpeg.exe is required by this test harness only."
        Assert-True ($ffprobe -ne $null) "ffprobe.exe is required by this test harness only."

        $nvidiaSmi = Find-Tool "nvidia-smi.exe"
        if (-not $nvidiaSmi) {
            Write-Warning "nvidia-smi.exe not found. Skipping NVENC-present hardware test on this machine."
            return
        }

        $width = 320
        $height = 180
        $fps = 10
        $durationSec = 2
        $expectedFrames = $fps * $durationSec
        $outFile = Join-Path $script:TestDir "nvenc-lossless.mp4"
        $referenceRaw = Join-Path $script:TestDir "nvenc-reference.yuv444p"
        $decodedRaw = Join-Path $script:TestDir "nvenc-decoded.yuv444p"

        [void] (New-SyntheticYuv444Reference -Path $referenceRaw -Width $width -Height $height -FrameCount $expectedFrames)

        $oldEnv = Set-TestEnv -Width $width -Height $height -Fps $fps -DurationSec $durationSec `
            -TextAtFrame 7 -Text "YUMLOG_SYNC_FRAME_007"

        try {
            # Act
            [void] (Invoke-NativeRecord -OutFile $outFile -Fps $fps -DurationSec $durationSec -Encoder "nvenc-lossless")

            # Assert
            Assert-True (Test-Path $outFile) "NVENC recorder did not create output MP4."

            Assert-LosslessRoundTrip -Mp4Path $outFile `
                -ReferenceRawPath $referenceRaw `
                -DecodedRawPath $decodedRaw `
                -FfmpegPath $ffmpeg `
                -FfprobePath $ffprobe `
                -Width $width `
                -Height $height `
                -ExpectedFrameCount $expectedFrames
        }
        finally {
            Restore-TestEnv -Old $oldEnv
        }
    }

    It "falls back to x265 lossless when NVENC is disabled or unavailable" {
        # Arrange
        $ffmpeg = Find-Tool "ffmpeg.exe"
        $ffprobe = Find-Tool "ffprobe.exe"

        Assert-True ($ffmpeg -ne $null) "ffmpeg.exe is required by this test harness only."
        Assert-True ($ffprobe -ne $null) "ffprobe.exe is required by this test harness only."

        $width = 320
        $height = 180
        $fps = 10
        $durationSec = 2
        $expectedFrames = $fps * $durationSec
        $outFile = Join-Path $script:TestDir "x265-fallback.mp4"
        $referenceRaw = Join-Path $script:TestDir "x265-reference.yuv444p"
        $decodedRaw = Join-Path $script:TestDir "x265-decoded.yuv444p"

        [void] (New-SyntheticYuv444Reference -Path $referenceRaw -Width $width -Height $height -FrameCount $expectedFrames)

        $oldEnv = Set-TestEnv -Width $width -Height $height -Fps $fps -DurationSec $durationSec `
            -TextAtFrame 7 -Text "YUMLOG_SYNC_FRAME_007" -DisableNvenc "1"

        try {
            # Act
            [void] (Invoke-NativeRecord -OutFile $outFile -Fps $fps -DurationSec $durationSec -Encoder "auto-lossless")

            # Assert
            Assert-True (Test-Path $outFile) "x265 fallback recorder did not create output MP4."

            Assert-LosslessRoundTrip -Mp4Path $outFile `
                -ReferenceRawPath $referenceRaw `
                -DecodedRawPath $decodedRaw `
                -FfmpegPath $ffmpeg `
                -FfprobePath $ffprobe `
                -Width $width `
                -Height $height `
                -ExpectedFrameCount $expectedFrames
        }
        finally {
            Restore-TestEnv -Old $oldEnv
        }
    }

    It "fails fast with a capability message when no lossless HEVC backend is available" {
        # Arrange
        $width = 320
        $height = 180
        $fps = 10
        $durationSec = 1
        $outFile = Join-Path $script:TestDir "no-backend.mp4"

        $oldEnv = Set-TestEnv -Width $width -Height $height -Fps $fps -DurationSec $durationSec `
            -TextAtFrame 0 -Text "YUMLOG_NO_BACKEND" -DisableNvenc "1" -DisableX265 "1"

        try {
            # Act
            $failed = $false
            $errorText = ""

            try {
                [void] (Invoke-NativeRecord -OutFile $outFile -Fps $fps -DurationSec $durationSec -Encoder "auto-lossless")
            }
            catch {
                $failed = $true
                $errorText = [string] $_
            }

            # Assert
            Assert-True $failed "Recorder should fail when both NVENC and x265 are unavailable."
            Assert-True ($errorText -match "No lossless HEVC encoder available") "Missing clear capability-gate message. Error=$errorText"
            Assert-True ($errorText -match "NVENC") "Capability message should mention NVENC availability. Error=$errorText"
            Assert-True ($errorText -match "x265") "Capability message should mention x265 availability. Error=$errorText"
        }
        finally {
            Restore-TestEnv -Old $oldEnv
        }
    }
}

Describe "Yumlog.Native native recording performance smoke" {
    It "keeps NVENC lossless at realtime throughput for the smoke resolution" {
        # Arrange
        $nvidiaSmi = Find-Tool "nvidia-smi.exe"
        if (-not $nvidiaSmi) {
            Write-Warning "nvidia-smi.exe not found. Skipping NVENC realtime smoke on this machine."
            return
        }

        $ffprobe = Find-Tool "ffprobe.exe"
        Assert-True ($ffprobe -ne $null) "ffprobe.exe is required by this test harness only."

        $width = 1280
        $height = 720
        $fps = 30
        $durationSec = 5
        $expectedFrames = $fps * $durationSec
        $outFile = Join-Path $script:TestDir "nvenc-realtime-smoke.mp4"

        $oldEnv = Set-TestEnv -Width $width -Height $height -Fps $fps -DurationSec $durationSec `
            -TextAtFrame 10 -Text "YUMLOG_REALTIME_SMOKE"

        try {
            # Act
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            [void] (Invoke-NativeRecord -OutFile $outFile -Fps $fps -DurationSec $durationSec -Encoder "nvenc-lossless")
            $sw.Stop()

            # Assert
            Assert-True (Test-Path $outFile) "NVENC realtime smoke did not create output MP4."

            $elapsedSec = $sw.Elapsed.TotalSeconds
            $maxAllowedSec = [double] $durationSec * 1.20
            Assert-True ($elapsedSec -le $maxAllowedSec) "NVENC encode did not keep realtime smoke threshold. ElapsedSec=$elapsedSec MaxAllowedSec=$maxAllowedSec"

            $frames = Get-VideoFrames -FfprobePath $ffprobe -Mp4Path $outFile
            Assert-Equal -Actual $frames.Count -Expected $expectedFrames -Message "NVENC realtime smoke frame count mismatch."
        }
        finally {
            Restore-TestEnv -Old $oldEnv
        }
    }
}
```

---

# 3. Required Test Fixtures

## 3.1 Synthetic pattern generator

Name:

- `synthetic-yuv444-v1`

Purpose:

- Provides mathematically known input frames.
- Bypasses nondeterministic desktop timing while still exercising the production encoder and MP4 muxer.

Required behavior:

- Generate exactly `fps * duration` frames.
- Frame dimensions are read from:
  - `YUMLOG_NATIVE_TEST_WIDTH`
  - `YUMLOG_NATIVE_TEST_HEIGHT`
- Pixel format:
  - `yuv444p` 8-bit for the first validation target.
- Plane order:
  - Y plane,
  - U plane,
  - V plane.
- Pattern formula must match the Pester helper `New-SyntheticYuv444Reference`.

Pattern:

- Y:
  - `(x + 3*y + 17*frameIndex) % 256`
- U:
  - `(((x % 2) * 255) xor ((frameIndex * 11) % 256)) % 256`
- V:
  - `(((y % 2) * 255) xor ((frameIndex * 7) % 256)) % 256`

Why this fixture matters:

- One-pixel alternating U/V patterns guarantee that YUV420/NV12 subsampling fails the byte comparison.
- This directly protects the core lossless claim.

---

## 3.2 Known-text overlay fixture

Name:

- `YUMLOG_SYNC_FRAME_007`

Purpose:

- Validates OCR metadata synchronization.

Required behavior:

- In synthetic test mode, render or inject known text at a known frame.
- Environment variables:
  - `YUMLOG_NATIVE_TEST_TEXT_AT_FRAME=7`
  - `YUMLOG_NATIVE_TEST_TEXT=YUMLOG_SYNC_FRAME_007`
- Metadata for frame 7 must contain:
  - `ocr.text` or `ocr.lines[].text` including `YUMLOG_SYNC_FRAME_007`.
  - `frameIndex == 7`.
  - matching metadata/video PTS.

Implementation options:

1. Render text into the synthetic frame and run the same OCR path used for WGC frames.
2. For deterministic CI, use a synthetic OCR provider only when `YUMLOG_NATIVE_ENABLE_TEST_HOOKS=1`, returning the known `OcrResult` shape.

Recommended approach:

- Use deterministic synthetic OCR for CI.
- Add a separate manual or hardware OCR validation test for real Windows AI OCR if required.

---

## 3.3 UI navigation hint fixture

Name:

- `synthetic-ui-hints-v1`

Purpose:

- Ensures `UiNavigationHint` and `TextBounds` shapes from `Domain.fs` survive timed metadata mux/demux.

Required metadata sample content:

- At least one hint per frame, or at known frames:
  - `kind = "button"`
  - `label = "Next"`
  - `confidence = 1.0`
  - valid `bounds.topLeft`, `topRight`, `bottomRight`, `bottomLeft`

Concrete assertion:

- Metadata JSON parses.
- Hint bounds are present and numeric.
- Confidence is between `0.0` and `1.0`.

---

## 3.4 External decoder fixture

Name:

- `ffmpeg-test-decoder`

Purpose:

- Independently decode MP4/HEVC for verification.

Required tools on test machines:

- `ffmpeg.exe`
- `ffprobe.exe`

Rules:

- Test-only.
- Not packaged with product unless explicitly under a test tools directory.
- Not called from product code.
- If missing, lossless tests fail with a harness setup message.

---

## 3.5 Optional MP4 timed metadata parser fixture

Name:

- `bento4-test-metadata-parser`

Purpose:

- Extract metadata samples if `ffprobe` cannot parse the chosen timed metadata handler.

Tools:

- `mp4dump.exe`
- `mp4extract.exe`

Rules:

- Test-only.
- Required only if `ffprobe -select_streams d -show_packets -show_data` cannot expose payload samples.

---

# 4. Automation Plan

## Suite layout

Recommended file:

- `E:\Yumlog\Skills\Test-NativeRecording.Tests.ps1`

Recommended invocation:

```powershell
Invoke-Pester .\Skills\Test-NativeRecording.Tests.ps1
```

or for older Pester usage consistent with the repo:

```powershell
.\Skills\Test-NativeRecording.Tests.ps1
```

## Test layers

1. Unit-level native encoder tests, if the encoder abstraction is exposed:
   - synthetic frame in,
   - encoded HEVC access unit out,
   - decode and compare.

2. Integration tests through CLI:
   - `Yumlog.Native record --out-file ... --fps ... --duration ...`
   - synthetic source via test hooks,
   - MP4 output verified externally.

3. Hardware capability tests:
   - NVENC-present machine,
   - NVENC-disabled fallback,
   - no-backend capability failure.

4. Performance smoke:
   - NVENC at 720p30 for CI/hardware smoke,
   - optional 1080p/4K validation on the T1000 machine.

## Ownership

- Native recorder implementation owner:
  - implement test-only synthetic source hook,
  - implement backend capability reporting,
  - implement metadata track muxing.
- Test owner:
  - maintain Pester harness,
  - maintain external decoder setup,
  - validate CI/hardware matrix.
- Release owner:
  - require all correctness tests before marking the recorder done.

---

# 5. Risk Hotspots

## 5.1 Silent chroma subsampling

Risk:

- Encoder is configured as lossless, but input path is `NV12` or `yuv420p`.
- Result is visually good but not mathematically lossless for desktop RGB/chroma detail.

Mitigation:

- Hard fail on `yuv420p`, `nv12`, `p010le`, or `*420*`.
- Chroma-stress fixture guarantees byte mismatch if subsampled.

---

## 5.2 RGB conversion not actually reversible

Risk:

- Desktop BGRA is converted to YUV444 using a non-reversible matrix, then HEVC lossless preserves only the converted YUV, not original RGB.

Mitigation:

- Define the product’s exact lossless claim:
  - If claiming YUV444-domain lossless, compare YUV444 source to YUV444 decoded output.
  - If claiming desktop RGB/BGRA lossless, require a reversible RGB/GBR path and compare decoded RGB/BGRA bytes to the original BGRA source.
- Do not claim desktop RGB mathematical losslessness unless the RGB/BGRA path is proven byte-for-byte.

Recommended release wording:

- "Lossless in the recorder’s non-subsampled encoder input domain."
- If RGB byte-for-byte is required, implement and test RGB/GBR preservation explicitly.

---

## 5.3 Metadata extraction compatibility

Risk:

- Timed metadata track is valid MP4 but not exposed by `ffprobe` as a data stream.

Mitigation:

- Keep metadata schema documented.
- Add Bento4 test-only extractor if needed.
- Test one sample per frame and PTS alignment regardless of parser.

---

## 5.4 Real desktop capture timing

Risk:

- WGC real desktop capture may have startup jitter or compositor timing variation.

Mitigation:

- Use synthetic source for mathematical proof.
- Use WGC live capture for separate smoke tests with limited tolerance.
- Do not weaken synthetic test tolerances.

---

## 5.5 x265 performance

Risk:

- x265 lossless at 4K may not be realtime.

Mitigation:

- Correctness remains mandatory.
- Realtime is required for NVENC path.
- x265 is classified as a non-realtime fallback if throughput is below requested FPS.

---

# 6. Release Confidence View

## Safe only when all of these are true

1. Lossless proof passes:
   - synthetic YUV444 source,
   - encoded MP4,
   - decoded YUV444,
   - full-file SHA-256 match,
   - per-frame hash match.

2. Pixel format gate passes:
   - no `yuv420p`,
   - no `nv12`,
   - no `p010le`,
   - no `*420*`.

3. Frame accuracy passes:
   - exact synthetic frame count,
   - correct dimensions,
   - no drops,
   - no duplicates,
   - monotonic timestamps.

4. Metadata sync passes:
   - one metadata sample per frame,
   - metadata PTS aligns to video PTS,
   - known OCR text appears at the known frame,
   - `Domain.fs` metadata shapes round-trip.

5. Backend coverage passes:
   - NVENC-present path succeeds and is lossless,
   - NVENC-disabled path falls back to x265 and is lossless,
   - no-backend path fails with clear capability message.

6. Performance smoke passes:
   - NVENC holds realtime at the smoke resolution,
   - x265 fallback performance is measured and documented.

---

# 7. Exit Criteria: "record is DONE"

The native recorder should not be considered done until all criteria below are met:

1. Core lossless claim proven:
   - Automated synthetic YUV444 test passes byte-for-byte after HEVC encode, MP4 mux, MP4 demux, and external decode.
   - Test rejects 4:2:0/NV12 output.

2. Frame accuracy proven:
   - `frameCount == fps * duration` for synthetic mode.
   - Decoded frame hashes match source frame hashes by index.
   - PTS values are monotonic and frame-spaced within tolerance.
   - Resolution matches requested capture dimensions.

3. Metadata sync proven:
   - Timed metadata track contains one sample per video frame.
   - Metadata sample PTS aligns with video frame PTS.
   - Known string injected at a known frame appears in that frame’s metadata sample.
   - OCR lines, words, bounds, UI hints, frame index, and QPC timestamp round-trip.

4. Backend behavior proven:
   - NVENC lossless succeeds on the NVIDIA T1000 validation machine.
   - x265 lossless fallback succeeds when NVENC is disabled/unavailable.
   - Missing backend condition fails fast with a clear actionable message.

5. Performance classified:
   - NVENC meets realtime smoke threshold.
   - x265 fallback is allowed to be non-realtime at high resolutions but must remain lossless.

6. Product dependency boundary maintained:
   - `Yumlog.Native` product code does not depend on FFmpeg.
   - FFmpeg/ffprobe/Bento4 are used only by tests for independent verification.