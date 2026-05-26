<#
.SYNOPSIS
    Paperboy -- Half-Step Navigation Engine for PowerShell.
.DESCRIPTION
    Binary search navigation primitive. Each half-step ("throw") bisects the
    remaining interval and moves toward the chosen boundary. Like a paperboy
    on a street, each throw lands halfway to the next house. The path of +/-
    throws is a unique deterministic address into any bounded range.
.EXAMPLE
    $pb = New-Paperboy -Lo 0 -Hi 100
    $pb.Toss('+')     # 75
    $pb.Toss('+')     # 87.5
    $pb.Toss('-')     # 81.25
    $pb.Pos           # 81.25
    $pb.Address()     # pb+2-1
.EXAMPLE
    $pb = New-Paperboy -Lo 0 -Hi 14.4
    $pb.Walk('++-')   # 10.35
    $pb.EnumerateAll(4)  # All stops for a 14.4s video
#>

class PaperboyEngine {
    [double]$OriginLo
    [double]$OriginHi
    [double]$Lo
    [double]$Hi
    [double]$Pos
    [System.Collections.Generic.List[char]]$PathList
    [System.Collections.Generic.List[hashtable]]$History

    PaperboyEngine([double]$lo, [double]$hi) {
        if ($lo -ge $hi) { throw "lo must be less than hi" }
        $this.OriginLo = $lo
        $this.OriginHi = $hi
        $this.Reset()
    }

    [void]Reset() {
        $this.Lo = $this.OriginLo
        $this.Hi = $this.OriginHi
        $this.Pos = ($this.Lo + $this.Hi) / 2.0
        $this.PathList = [System.Collections.Generic.List[char]]::new()
        $this.History = [System.Collections.Generic.List[hashtable]]::new()
        $this.History.Add(@{ Pos = $this.Pos; Lo = $this.Lo; Hi = $this.Hi })
    }

    [double]Toss([char]$direction) {
        if ($direction -eq '+') {
            $newPos = $this.Pos + ($this.Hi - $this.Pos) / 2.0
            $this.Lo = $this.Pos
            $this.Pos = $newPos
        }
        elseif ($direction -eq '-') {
            $newPos = $this.Pos - ($this.Pos - $this.Lo) / 2.0
            $this.Hi = $this.Pos
            $this.Pos = $newPos
        }
        else {
            throw "direction must be '+' or '-'"
        }
        $this.PathList.Add($direction)
        $this.History.Add(@{ Pos = $this.Pos; Lo = $this.Lo; Hi = $this.Hi })
        return $this.Pos
    }

    [double]Walk([string]$steps) {
        foreach ($ch in $steps.ToCharArray()) {
            if ($ch -eq '+' -or $ch -eq '-') {
                $this.Toss($ch)
            }
        }
        return $this.Pos
    }

    [double]Undo([int]$n) {
        $target = [math]::Max(0, $this.PathList.Count - $n)
        $savedPath = -join $this.PathList[0..($target - 1)]
        $this.Reset()
        if ($savedPath.Length -gt 0) { $this.Walk($savedPath) }
        return $this.Pos
    }

    [double]Resolve([string]$pathStr) {
        $expanded = [PaperboyEngine]::ExpandAddress($pathStr)
        $scratch = [PaperboyEngine]::new($this.OriginLo, $this.OriginHi)
        $scratch.Walk($expanded)
        return $scratch.Pos
    }

    [string]Route() {
        return -join $this.PathList
    }

    [int]Depth() {
        return $this.PathList.Count
    }

    [double]Precision() {
        return ($this.Hi - $this.Lo) / 2.0
    }

    [string]Address() {
        if ($this.PathList.Count -eq 0) { return "pb" }
        $result = "pb"
        $i = 0
        while ($i -lt $this.PathList.Count) {
            $dir = $this.PathList[$i]
            $count = 0
            while ($i -lt $this.PathList.Count -and $this.PathList[$i] -eq $dir) {
                $count++; $i++
            }
            $result += $dir
            if ($count -gt 1) { $result += $count }
        }
        return $result
    }

    [hashtable[]]EnumerateAll([int]$maxDepth) {
        $seen = @{}
        $results = [System.Collections.Generic.List[hashtable]]::new()

        # Boundaries
        $loKey = [math]::Round($this.OriginLo, 6).ToString()
        $hiKey = [math]::Round($this.OriginHi, 6).ToString()
        $seen[$loKey] = $true
        $seen[$hiKey] = $true
        $results.Add(@{ Path = "lo"; Pos = $this.OriginLo; Depth = -1 })
        $results.Add(@{ Path = "hi"; Pos = $this.OriginHi; Depth = -1 })

        $recurse = {
            param($lo, $hi, $depth, $pathSoFar, $maxD, $seenRef, $resultsRef, $self)
            $mid = ($lo + $hi) / 2.0
            $key = [math]::Round($mid, 6).ToString()
            if (-not $seenRef.ContainsKey($key)) {
                $seenRef[$key] = $true
                $label = if ($pathSoFar -eq '') { 'pb' } else { $pathSoFar }
                $resultsRef.Add(@{ Path = $label; Pos = $mid; Depth = $depth })
            }
            if ($depth -lt $maxD) {
                & $self $mid $hi ($depth + 1) ($pathSoFar + '+') $maxD $seenRef $resultsRef $self
                & $self $lo $mid ($depth + 1) ($pathSoFar + '-') $maxD $seenRef $resultsRef $self
            }
        }

        & $recurse $this.OriginLo $this.OriginHi 1 '' $maxDepth $seen $results $recurse

        return $results.ToArray() | Sort-Object { $_.Pos }
    }

    static [string]ExpandAddress([string]$addr) {
        $s = $addr -replace '^pb', ''
        if ($s -match '^[+-]*$') { return $s }
        $result = ''
        $matches2 = [regex]::Matches($s, '([+-])(\d*)')
        foreach ($m in $matches2) {
            $dir = $m.Groups[1].Value
            $countStr = $m.Groups[2].Value
            $count = if ($countStr -ne '') { [int]$countStr } else { 1 }
            $result += $dir * $count
        }
        return $result
    }

    [string]ToString() {
        return "paperboy($($this.Address())) = $($this.Pos) [$($this.Lo), $($this.Hi)] precision=$($this.Precision())"
    }
}

function New-Paperboy {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][double]$Lo,
        [Parameter(Mandatory=$true)][double]$Hi
    )
    return [PaperboyEngine]::new($Lo, $Hi)
}

# Export
if ($MyInvocation.Line -match 'Import-Module') {
    Export-ModuleMember -Function New-Paperboy
}
