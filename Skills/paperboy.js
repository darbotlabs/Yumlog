/**
 * paperboy.js -- Paperboy Half-Step Navigation Engine
 *
 * A binary search navigation primitive. Each half-step ("throw") bisects
 * the remaining interval between the current position and a boundary,
 * moving toward that boundary. Like a paperboy on a street, each throw
 * lands halfway to the next house -- and from there you can throw again
 * in either direction.
 *
 * The path of +/- throws is a unique, deterministic address into any
 * bounded range.
 *
 * Usage:
 *   const pb = new Paperboy(0, 100);   // route [0, 100], starts at 50
 *   pb.toss('+');                       // 75
 *   pb.toss('+');                       // 87.5
 *   pb.toss('-');                       // 81.25
 *   pb.pos                             // 81.25
 *   pb.route                           // '++-'
 *   pb.address                         // 'pb+2-1'
 *   pb.precision                       // 6.25
 *   pb.deliver('++-')                  // 81.25  (stateless resolve)
 */

class Paperboy {
    /**
     * @param {number} lo   - Start of the route (inclusive)
     * @param {number} hi   - End of the route (inclusive)
     */
    constructor(lo, hi) {
        if (lo >= hi) throw new Error('lo must be less than hi');
        this._originLo = lo;
        this._originHi = hi;
        this.reset();
    }

    /** Reset to initial midpoint state. */
    reset() {
        this.lo = this._originLo;
        this.hi = this._originHi;
        this.pos = (this.lo + this.hi) / 2;
        this._path = [];
        this._history = [{ pos: this.pos, lo: this.lo, hi: this.hi }];
        return this;
    }

    /**
     * Execute one half-step throw.
     * @param {'+' | '-'} direction  - '+' throws toward hi, '-' throws toward lo
     * @returns {number} New position after the throw
     */
    toss(direction) {
        if (direction === '+') {
            const newPos = this.pos + (this.hi - this.pos) / 2;
            this.lo = this.pos;
            this.pos = newPos;
        } else if (direction === '-') {
            const newPos = this.pos - (this.pos - this.lo) / 2;
            this.hi = this.pos;
            this.pos = newPos;
        } else {
            throw new Error('direction must be "+" or "-"');
        }
        this._path.push(direction);
        this._history.push({ pos: this.pos, lo: this.lo, hi: this.hi });
        return this.pos;
    }

    /**
     * Execute a full route from a path string.
     * @param {string} throws - String of '+' and '-' characters (e.g. '++-')
     * @returns {number} Final position
     */
    walk(throws) {
        for (const ch of throws) {
            if (ch === '+' || ch === '-') this.toss(ch);
        }
        return this.pos;
    }

    /**
     * Undo the last n throws.
     * @param {number} [n=1] - Number of throws to undo
     * @returns {number} Position after undo
     */
    undo(n = 1) {
        const target = Math.max(0, this._path.length - n);
        const path = this._path.slice(0, target).join('');
        this.reset();
        if (path.length > 0) this.walk(path);
        return this.pos;
    }

    /**
     * Deliver to an address: resolve a path string to a position without modifying state.
     * @param {string} pathStr - Path string (e.g. '++-', 'pb+2-1')
     * @returns {number} Resolved position
     */
    deliver(pathStr) {
        const expanded = Paperboy.expandAddress(pathStr);
        const scratch = new Paperboy(this._originLo, this._originHi);
        scratch.walk(expanded);
        return scratch.pos;
    }

    /**
     * Generate all stops at a given depth (complete binary tree level).
     * @param {number} depth - Depth of the route tree (0 = midpoint only)
     * @returns {Array<{route: string, pos: number}>} All stops at that depth
     */
    enumerate(depth) {
        if (depth === 0) return [{ route: '', pos: (this._originLo + this._originHi) / 2 }];
        const results = [];
        const recurse = (d, pathSoFar) => {
            if (d === 0) {
                results.push({ route: pathSoFar, pos: this.deliver(pathSoFar) });
                return;
            }
            recurse(d - 1, pathSoFar + '+');
            recurse(d - 1, pathSoFar + '-');
        };
        recurse(depth, '');
        return results.sort((a, b) => a.pos - b.pos);
    }

    /**
     * Generate all unique stops up to a given depth (all tree levels).
     * Sorted by position. Use for frame extraction timestamps, etc.
     * @param {number} maxDepth - Maximum route depth
     * @returns {Array<{route: string, pos: number, depth: number}>}
     */
    enumerateAll(maxDepth) {
        const seen = new Map();
        seen.set(this._originLo.toFixed(6), { route: 'lo', pos: this._originLo, depth: -1 });
        seen.set(this._originHi.toFixed(6), { route: 'hi', pos: this._originHi, depth: -1 });

        const recurse = (lo, hi, depth, pathSoFar) => {
            const mid = (lo + hi) / 2;
            const key = mid.toFixed(6);
            if (!seen.has(key)) {
                seen.set(key, { route: pathSoFar || 'pb', pos: mid, depth });
            }
            if (depth < maxDepth) {
                recurse(mid, hi, depth + 1, pathSoFar + '+');
                recurse(lo, mid, depth + 1, pathSoFar + '-');
            }
        };
        recurse(this._originLo, this._originHi, 1, '');
        return Array.from(seen.values()).sort((a, b) => a.pos - b.pos);
    }

    /** The raw route as a string of +/- throws. */
    get route() { return this._path.join(''); }

    /** Number of throws taken. */
    get depth() { return this._path.length; }

    /** Maximum error: half the current interval width. */
    get precision() { return (this.hi - this.lo) / 2; }

    /** Compact address: pb+2-1 form. */
    get address() {
        if (this._path.length === 0) return 'pb';
        let result = 'pb';
        let i = 0;
        while (i < this._path.length) {
            const dir = this._path[i];
            let count = 0;
            while (i < this._path.length && this._path[i] === dir) { count++; i++; }
            result += dir + (count > 1 ? count : '');
        }
        return result;
    }

    /** Full history of stops visited. */
    get history() { return this._history.map(h => ({ ...h })); }

    /**
     * Expand compact address (pb+2-1) to raw route (++-).
     * Also accepts raw routes as pass-through.
     * @param {string} addr
     * @returns {string} Expanded route of +/- characters
     */
    static expandAddress(addr) {
        let s = addr.replace(/^pb/, '');
        if (/^[+-]*$/.test(s)) return s;
        // Expand runs: +2-1 -> ++-
        let result = '';
        const re = /([+-])(\d*)/g;
        let m;
        while ((m = re.exec(s)) !== null) {
            if (m[0] === '') break;
            const dir = m[1];
            const count = m[2] ? parseInt(m[2]) : 1;
            result += dir.repeat(count);
        }
        return result;
    }

    /** String representation. */
    toString() {
        return `paperboy(${this.address}) = ${this.pos} [${this.lo}, ${this.hi}] precision=${this.precision}`;
    }
}

// Export for both Node.js and browser
if (typeof module !== 'undefined' && module.exports) {
    module.exports = { Paperboy };
} else if (typeof window !== 'undefined') {
    window.Paperboy = Paperboy;
}
