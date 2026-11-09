# 51 Percent Crew — Channel Study

**Channel:** 51 Percent Crew (host: "Todd"), active Sep 2017 – Jul 2019
**Corpus:** 47 videos, ~12h 38m total; 43 machine transcripts (~117k words)
**Genre:** Crypto technical-analysis education (TradingView-based), live BTC market commentary, interviews, and community contests. Primary venue: BTC/USD on Bitfinex.

This study was produced by reading the full transcripts of the 19 core educational videos and skimming metadata for the rest. Sources are in `transcripts/`; the per-video catalog is `INDEX.md` / `index.json`.

---

## 1. What the channel is

Todd presents himself as a career trader (10+ years of candlestick charting) who started teaching in the Jan 2018 crypto boom because he noticed his audience lacked basics. The channel splits into four strands:

1. **The lesson series (Jan 2018):** TradingView setup → trend lines → magnet tool → Fibonacci (Lessons 1–4), then candlestick reading.
2. **Tool deep-dives (2018–2019):** Fibonacci retracement/extension/speed fan, Gann fan, Ichimoku cloud, volume profile, kill zones, Bitfinex long/short sentiment.
3. **Live trade commentary (Feb–Jun 2018):** dated BTC analyses where he narrates entries/exits in real time — the applied counterpart to the lessons.
4. **Off-topic:** interviews (John McAfee, crypto tax attorney Andrew Gordon, project CEOs), security tutorials, contests, and sponsored Halo masternode content.

Later material (2019) migrates the "how to trade it" depth behind a Patreon; the YouTube videos become refreshers of those classes.

## 2. The methodology, distilled

### 2.1 Chart hygiene (Lessons 1 & 3, 2019 setup video)
- Dark chart, solid black background, **gridlines removed** — his stated reason: gridlines create false reference levels ("your mind plays tricks on you").
- High-contrast candles (bold green/red, wicks in the *opposite* color of the body) for fast reading.
- Auto price-scaling for zoomed-in work; **log scale** only for multi-month views.
- Build a custom favorites toolbar (fib, ray, trend line) to speed up chart work.

### 2.2 Accuracy discipline (Lesson 2 — the channel's signature habit)
- Never eyeball trend lines. Use the **ray tool**, then open the object's coordinates and type in the exact **OHLC values** read from the data window, then **lock the drawing** so it can't be nudged.
- Rationale: a few pixels of mouse error at the anchor compounds to hundreds of dollars at the projection end.
- The **magnet tool** (Lesson 3) automates snapping to the four candle positions (O/H/L/C) — taught *after* the manual method so students understand what the magnet is doing.

### 2.3 Fibonacci system (Lessons 4, Parts 1–2, Extension, Speed Fan)
The centerpiece of his method. Key rules, verbatim-level:
- **"Wick to wick, with the trend"** — always drag the retracement along the trend direction, anchored on wicks. Exception: ignore an abnormal wick that only exists on one low-liquidity exchange; use the candle body instead.
- Fibs nest: fib inside a fib inside a fib; multiple fibs per chart, with lines extended/labels moved for readability.
- **Level semantics:** 38.2/50/61.8/78.6 are the working levels. The 50 isn't a true Fibonacci number but he keeps it because enough traders act on it. Best risk/reward entries are the **61.8 and 78.6**; aggressive traders enter at the 50 with a stop below the 61.8.
- **Trend invalidation rule:** a candle *close* below the 61.8 invalidates the trend ("institutions took too much profit") — though in crypto's violence he tolerates stretching to the 78.6.
- **"Good traders don't buy at prices, they buy in zones"** — entries are placed across the 50→61.8 band, not at a single tick.
- He'll manually nudge a fib level to line up with a neighboring fib's 61.8 when price barely pierced it, because other traders keep their levels even.
- **Trend-based fib extension:** measure a completed swing (low → high → retracement low) to project the next move's targets into unowned price territory (all-time highs).
- **Speed fan:** anchor pivot-to-pivot low/high; diagonal fib angles become S/R. He removes the grid box, then overlays a normal retracement to re-create the grid with full-width lines. Confluence where fan lines cross horizontal fib levels = higher-probability reversal zones ("price follows the path of least resistance").

### 2.4 Candlestick reading (Lesson 5, Doji video, exit videos)
- Fundamentals: OHLC anatomy, body vs wick, color = close vs prior close, per-candle data in the data window.
- **Doji taxonomy:** neutral (cross), long-legged, gravestone (sell pressure), dragonfly (buy pressure). In crypto he relaxes the geometry — a cross in the top ~¾ of the range still counts as a dragonfly, because the market is too violent for textbook shapes.
- Signals need a **confirmation candle**; longer timeframes (4h, 1D) carry more weight than 5–30m.
- **The three-doji sell rule:** three consecutive dojis after an uptrend = exit; he claims the market fails "80–90% of the time" on that pattern.
- Pattern vocabulary used throughout: bullish/bearish engulfing, spinning top (indecision), hammer vs inverted hammer (context-dependent — bullish at a bottom, bearish at a top).

### 2.5 Support & resistance philosophy ("The most IMPORTANT thing," May 2019)
He calls S/R *the* most important trading concept. Distinctives:
- Draw S/R through a **combination of wicks and bodies**, not slavishly wick-to-wick; expect price to "argue" around the level. "Bitcoin doesn't care about your lines."
- The master pattern repeated in nearly every applied video: **former resistance becomes support, former support becomes resistance** — trade the retest after a break.
- Both diagonal and horizontal S/R; intersections of two levels form high-value zones.
- Fib anchors are exploratory: try multiple swings (high→low, low→high, tighter ranges) until levels visibly correlate with price history, then trust that layout.

### 2.6 Context indicators
- **Ichimoku cloud (2019):** taught in plain English (leading span A/B = cloud boundaries; baseline; conversion line; drop the lagging span — he and traders he respects don't use it). Rules: price above cloud = bullish, below = bearish, inside = consolidation/no-trade; blue-over-red = bullish (a "TK cross," analogized to a MACD crossover); kumo color twist = regime change; all lines behave as S/R with the same former-S/R flipping logic.
- **Volume profile:** the price-axis histogram vs the familiar time-axis volume; visual range (his setting: 48 rows), session volume (UTC daily), and fixed range — the latter paired with kill zones to profile a single session's accumulation.
- **Kill zones:** the three market opens — Asia ~7pm ET, London ~2am ET, New York ~8am ET (the "Bitcoin Kill Zones v2" TradingView indicator). His claim: nearly all of a day trader's money is made in about an hour around an open; watch for pre-open accumulation.
- **Bitfinex longs/shorts:** margin-position counts as a *sentiment* gauge, not a flow indicator (it misses spot buys/sells). His self-invented twist: lay a Fibonacci **on the shorts chart itself** to read how far positioning has retraced — e.g., shorts closing back through the 50% = "half the margin traders think the bottom is near." Take it "with a grain of salt."
- **MACD** appears in exit videos as a lagging signal — act on the *curl* before the crossover prints, not after.

### 2.7 Risk management & trade process (the two "why I took this trade" videos)
His full live-trade loop, reconstructed from the May 2019 walkthrough and the Mar 2018 exit video:
1. **Big picture first** — establish the higher-timeframe trend; prefer trading with it.
2. **Locate the entry zone** by confluence: former-resistance-turned-support + fib level + cloud boundary.
3. **Size the risk before entering** — stop below the nearest structural support (cloud bottom or last low), targeting ~1% + fees of risk; two nearby support levels = a well-protected entry.
4. **Manage by structure, not emotion** — ratchet the stop up behind each reclaimed fib level (61.8 → 50 → 38.2) as the trade works; take partials once fees are covered.
5. **Cross-check the broad market** — before trusting a BTC higher-high, check whether ETH/USD and LTC/USD confirmed it; a lone higher-high plus a spinning top = distribution warning.
6. **Exit mechanically** — tighten to a trailing stop (or a stop a hair below a fib) so "the human interaction is taken out of it"; once committed, don't renegotiate with yourself.
7. Watch the order books and momentum as secondary confirmation; after exiting, go to "standby" — no position is a position.

### 2.8 Trading philosophy
- **TA is decision support, not prophecy:** "we're gathering data to make an educated decision… we're all going to be right sometimes and wrong sometimes."
- **Crypto adaptation doctrine:** classic rules (doji geometry, 61.8 invalidation) are guidelines to be loosened for crypto's speed and violence — "follow the rules, but change the rules."
- **Trader, not hodler:** positions last ten minutes to a couple of days; being flat is a deliberate state.
- Everybody uses the same tools — Fibonacci "works" substantially *because* it's ubiquitous (self-fulfilling coordination), which is also why he keeps the non-Fibonacci 50% level.

## 3. Recommended study order

| Phase | Videos |
|-------|--------|
| Setup | Lesson 1 (chart setup) → 2019 TradingView setup refresher |
| Precision | Lesson 2 (trend lines/ray) → Lesson 3 (magnet) |
| Candles | Candlestick explanation → Doji/when-to-sell |
| Fibonacci | Lesson 4 → Fib Part 1 → Fib Part 2 → Fib Extension → Speed Fan |
| Structure | "Most IMPORTANT thing" (S/R) → Gann Fan → Ichimoku Cloud |
| Context | Kill Zones → Volume Profile → Bitfinex Long/Short |
| Application | "Why did I take this trade" → "Candles & momentum exit" → the dated BTC TA videos (Feb–Jun 2018) in order |

## 4. Caveats for the modern reader

- **Era-bound:** everything is calibrated to 2018's Bitfinex-dominated, hyper-volatile BTC market. Kill-zone times, the Bitfinex L/S chart, and "Horrocks trading software" references have aged; several linked tools/contests are defunct.
- **Unverified performance claims:** "How I make $100,000 a day" is admitted clickbait in the video's own first sentence; the three-doji "80–90%" failure rate and "8-for-9 week" are anecdotes, not backtests.
- **Conflicts of interest:** affiliate links (TradingView), Patreon upsells, and sponsored content (Halo masternode series, Synapse AI interview) are woven through the catalog.
- **Survivorship of method:** the core durable content is the *discipline* — exact anchor coordinates, locked drawings, zone-based entries, structure-based stops, confirmation before action — more than any single indicator's predictive power.
- 4 videos have no transcript (music/no-speech or captions disabled): Dbet, The Bitcoin War, Now that's funny, BTC Whale on Bitfinex.

## 5. Archive layout

```
S:\Yumlog\archive\51PercentCrew\
├── *.mkv                 # full-quality originals (video+audio, ≤1080p), embedded metadata
├── *.webp                # thumbnails
├── archive.txt           # yt-dlp download ledger (re-run = incremental)
├── download_log.txt      # download session log
├── INDEX.md              # human-readable catalog (date, title, length, views, words)
├── index.json            # machine-readable catalog
├── meta\                 # per-video .info.json + raw .vtt captions
├── transcripts\          # cleaned plain-text transcripts (43 videos)
├── tools\parse_archive.py# regenerates transcripts + index from meta\
└── STUDY.md              # this document
```

To refresh the archive later (grabs only new uploads):
```powershell
yt-dlp -f "bv*[height<=1080]+ba/b" --merge-output-format mkv `
  -o "S:\Yumlog\archive\51PercentCrew\%(upload_date)s - %(title)s [%(id)s].%(ext)s" `
  --download-archive S:\Yumlog\archive\51PercentCrew\archive.txt `
  --embed-metadata --write-thumbnail `
  "https://www.youtube.com/@51PercentCrew/videos"
```
