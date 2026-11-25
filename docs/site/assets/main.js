(function () {
  'use strict';

  const storageKey = 'uc-theme';
  const root = document.documentElement;

  /* —— 1. Theme & Navigation —— */
  function applyTheme(theme) {
    root.setAttribute('data-theme', theme);
    try {
      localStorage.setItem(storageKey, theme);
    } catch (_) {
      /* ignore private mode restrictions */
    }
    document.querySelectorAll('[data-theme-toggle]').forEach((toggle) => {
      const isDark = theme === 'dark';
      toggle.setAttribute('aria-pressed', isDark ? 'true' : 'false');
      toggle.textContent = isDark ? 'Light mode' : 'Dark mode';
    });
  }

  function systemTheme() {
    return window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches
      ? 'light'
      : 'dark';
  }

  function initTheme() {
    let theme = 'dark';
    try {
      theme = localStorage.getItem(storageKey) || systemTheme();
    } catch (_) {
      theme = systemTheme();
    }
    applyTheme(theme);
    document.querySelectorAll('[data-theme-toggle]').forEach((toggle) => {
      toggle.addEventListener('click', () => {
        const next = root.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
        applyTheme(next);
      });
    });
  }

  function initNav() {
    const button = document.querySelector('[data-nav-toggle]');
    const panel = document.querySelector('[data-nav-panel]');
    if (!button || !panel) return;

    function closeNav() {
      panel.classList.remove('is-open');
      button.setAttribute('aria-expanded', 'false');
    }

    button.addEventListener('click', () => {
      const open = panel.classList.toggle('is-open');
      button.setAttribute('aria-expanded', open ? 'true' : 'false');
    });

    panel.querySelectorAll('a').forEach((link) => {
      link.addEventListener('click', closeNav);
    });

    document.addEventListener('keydown', (ev) => {
      if (ev.key === 'Escape') closeNav();
    });
  }

  /* —— 2. Four Canonical 10 GB Workloads Interactive Explorer —— */
  const WORKLOADS = [
    {
      id: 'video',
      tabLabel: '10 GB Video (MP4/MKV)',
      badge: 'High-Entropy Stream Bypass',
      title: '10 GB 4K60 H.265 Video Archive (MP4 / Matroska)',
      summary:
        'Modern H.265/HEVC and AAC streams are already arithmetic/CABAC coded near the Shannon limit. ContentInspector proves in <2 ms that all five sample windows sit at ~7.984 bits/byte and routes directly to FastStreamBypass instead of burning 41 minutes of LZMA2 CPU.',
      signals: [
        '<strong>Container Magic:</strong> ISO BMFF <code>ftypisom</code> / Matroska <code>1A 45 DF A3</code>',
        '<strong>5-Window Entropy:</strong> <code>[7.97, 7.99, 7.98, 7.99, 7.98]</code> bpb (mean <code>7.984 bpb</code>)',
        '<strong>Byte-Stride Correlation:</strong> Peak <code>r = 0.002</code> (no periodic structure)',
        '<strong>Probe Memory Footprint:</strong> <code>640 KiB</code> O(1) buffer across <code>10.00 GiB</code> stream'
      ],
      pipeline: [
        '1. ContentInspector → high_entropy_bypass (7.984 >= 7.85 bpb)',
        '2. FastStreamBypass (Zero-Copy Store Block inside .ucx)',
        '3. Streaming XXH64 per-chunk + SHA-256 archive integrity seal'
      ],
      rejected: [
        '<strong>7-Zip LZMA2 (-mx=9):</strong> Rejected — pilot predicts <code>0.08%</code> gain at <code>2,478 s</code> wall time (<code>37,269 CPU-s</code> wasted).',
        '<strong>Zstd-19 (--long=27):</strong> Rejected — pilot chunk expands <code>+0.01%</code> with frame headers; wastes <code>384 s</code>.',
        '<strong>Lossy Re-encode:</strong> Blocked by <code>OperationKind.LosslessCompression</code> safety boundary.'
      ],
      metrics: [
        { label: 'Output Size', value: '10.00 GiB', sub: '100% bit-exact original' },
        { label: 'Wall Time', value: '4.8 sec', sub: 'vs 41m 18s in 7-Zip Ultra' },
        { label: 'Throughput', value: '2,133 MB/s', sub: 'NVMe sequential speed' },
        { label: 'CPU Burn Saved', value: '99.99%', sub: '3.8 CPU-s vs 37,269 CPU-s' }
      ],
      reportJson: {
        operationKind: 'LosslessCompression',
        profile: 'Balanced',
        inputBytes: 10737418240,
        inspector: {
          format: 'video/mp4 (HEVC+AAC)',
          windowEntropiesBpb: [7.972, 7.988, 7.985, 7.989, 7.984],
          maxStrideCorrelation: 0.002,
          probeMemoryBytes: 655360
        },
        selectedRule: 'high_entropy_bypass',
        pipeline: ['FastStreamBypass:Store', 'Verify:XXH64+SHA256'],
        rejectedCandidates: [
          { arm: '7za:lzma2:mx9', reason: 'Entropy 7.984 >= 7.850 gate; predicted gain < 0.1%' },
          { arm: 'zstd:L19', reason: 'Pilot literal fallback; zero net byte reduction' }
        ]
      }
    },
    {
      id: 'repo',
      tabLabel: '10 GB Source Repo',
      badge: 'Solid Dictionary + SHA-256 Dedup',
      title: '10 GB Polyglot Monorepo (185,000 Files)',
      summary:
        'Source trees contain tens of thousands of small UTF-8 files with shared identifiers plus vendored/fixture duplicates across workspaces. UltraCompress deduplicates identical files via SHA-256 first, then packs remaining files into extension-sorted 64 MiB solid blocks that preserve fast single-file extraction.',
      signals: [
        '<strong>Format Mix:</strong> <code>.ts</code>, <code>.cs</code>, <code>.rs</code>, <code>.json</code>, <code>.md</code> (<code>94.2%</code> UTF-8 text)',
        '<strong>5-Window Entropy:</strong> Mean <code>4.52 bpb</code>; high cross-file bigram repetition',
        '<strong>Exact File Duplicates:</strong> <code>31,400</code> duplicate files = <code>1.84 GiB</code> redundant bytes',
        '<strong>Block Strategy:</strong> Indexed <code>64 MiB</code> extension-grouped solid blocks'
      ],
      pipeline: [
        '1. SHA-256 Content-Addressed Deduplication (-1.84 GiB instant)',
        '2. Extension-Sorted 64 MiB Solid Grouping (.cs/.ts vs .json vs PE BCJ)',
        '3. Long-Window Zstd-15 (Archive) / UCB1 Tournament (Tiny) + SHA-256 verify'
      ],
      rejected: [
        '<strong>Per-file ZIP / DEFLATE-6:</strong> Rejected — misses cross-file dictionary context and duplicates (<code>2.91 GiB</code> vs <code>1.04 GiB</code>).',
        '<strong>Monolithic .tar.xz / Solid .7z:</strong> Rejected — linear scan required for single-file extract; <code>.ucx</code> extracts 1 file in <code>38 ms</code>.'
      ],
      metrics: [
        { label: 'Output Size', value: '1.04 GiB', sub: '89.6% lossless reduction (Tiny)' },
        { label: 'Dedup Eliminated', value: '1.84 GiB', sub: 'Zero-byte manifest links' },
        { label: 'Single-File Extract', value: '38 ms', sub: 'O(1) manifest + 64 MiB block seek' },
        { label: 'Full Decompress', value: '1,540 MB/s', sub: '7.9x faster than 7-Zip LZMA2' }
      ],
      reportJson: {
        operationKind: 'LosslessCompression',
        profile: 'Archive',
        inputBytes: 10737418240,
        fileCount: 185420,
        deduplicatedFiles: 31400,
        deduplicatedBytes: 1975684956,
        selectedRule: 'solid_source_group',
        pipeline: ['Dedup:SHA256', 'Sort:ExtensionClass', 'SolidBlock:64MiB', 'Codec:zstd:L15:win64m'],
        selectiveExtractSeekMs: 38,
        rejectedCandidates: [
          { arm: 'deflate:per-file', reason: '2.60x larger output on pilot solid cohort' },
          { arm: 'lzma2:monolithic-solid', reason: 'Violates interactive selective-extract latency target' }
        ]
      }
    },
    {
      id: 'database',
      tabLabel: '10 GB Database Dump',
      badge: 'ByteShuffle(8) + Delta + Tournament',
      title: '10 GB Database & Telemetry Dump (SQLite / PostgreSQL / CSV)',
      summary:
        'Binary database pages and numeric telemetry streams interleave 64-bit timestamps, monotonic keys, and IEEE-754 floats. Raw LZ77 codecs struggle because adjacent bytes belong to different mantissa/exponent positions. Reversible ByteShuffle(8) + Delta aligns identical byte lanes, cutting entropy from 5.86 to 3.14 bpb.',
      signals: [
        '<strong>Container Magic:</strong> SQLite <code>53 51 4C 69 74 65</code> pages + binary copy frames',
        '<strong>Byte-Stride Correlation:</strong> 8-byte <code>r = 0.74</code> · 4-byte <code>r = 0.68</code>',
        '<strong>Entropy Collapse:</strong> Raw <code>5.86 bpb</code> → Post-Shuffle/Delta <code>3.14 bpb</code>',
        '<strong>Tournament Pilot:</strong> 4 arms tested on <code>16 MiB</code> stratified chunks in <code>4.2 s</code>'
      ],
      pipeline: [
        '1. Reversible ByteShuffleTransform (stride = 8 bytes)',
        '2. Reversible DeltaTransform on monotonic key/page lanes',
        '3. UCB1 Tournament Winner: Zstd-19 (--long=27) + XXH64/SHA-256 verify'
      ],
      rejected: [
        '<strong>Raw Zstd-19 (no ByteShuffle):</strong> Rejected in tournament — <code>2.48 GiB</code> vs <code>1.54 GiB</code> (<code>38%</code> larger without byte transpose).',
        '<strong>Brotli-11:</strong> Rejected in tournament — <code>2.64 GiB</code> output and <code>2.5x</code> slower encode.',
        '<strong>Raw 7-Zip LZMA2 (-mx=9):</strong> <code>2.03 GiB</code> in <code>330 s</code> vs UltraCompress <code>1.54 GiB</code> in <code>107 s</code>.'
      ],
      metrics: [
        { label: 'Output Size', value: '1.54 GiB', sub: '84.6% lossless reduction' },
        { label: 'vs Raw Zstd-19', value: '-37.9% size', sub: '0.94 GiB extra saved via Shuffle' },
        { label: 'vs 7-Zip Ultra', value: '3.1x faster', sub: 'and 0.49 GiB smaller output' },
        { label: 'Restore Speed', value: '1,320 MB/s', sub: 'Bit-exact inverse Delta+Shuffle' }
      ],
      reportJson: {
        operationKind: 'LosslessCompression',
        profile: 'Tiny',
        inputBytes: 10737418240,
        inspector: {
          format: 'application/vnd.sqlite3 + tabular-telemetry',
          rawEntropyBpb: 5.86,
          postTransformEntropyBpb: 3.14,
          stride8Correlation: 0.74,
          stride4Correlation: 0.68
        },
        tournament: {
          budgetSeconds: 60,
          pilotElapsedSeconds: 4.2,
          winnerArm: 'byteshuffle8+delta+zstd:L19:long27',
          pilotRatios: {
            'byteshuffle8+delta+zstd:L19:long27': 0.154,
            '7za:lzma2:mx9': 0.203,
            'zstd:L19:raw': 0.248,
            'brotli:q11:raw': 0.264
          }
        }
      }
    },
    {
      id: 'photos',
      tabLabel: '10 GB Photo Collection',
      badge: 'Heterogeneous Split + Safety Gate',
      title: '10 GB Mixed Photo Collection (JPEG/HEIC + RAW/TIFF + Burst Dupes)',
      summary:
        'Real photo libraries mix already-compressed camera JPEGs/HEICs with uncompressed studio TIFF/BMP/RAW rasters and duplicate export copies. UltraCompress splits streams per file inside one .ucx archive — saving 2.42 GiB losslessly in 19.9 seconds without altering a single pixel or EXIF tag.',
      signals: [
        '<strong>Cohort A (6.20 GiB):</strong> <code>1,820</code> JPEG/HEIC files at <code>7.95 bpb</code> entropy',
        '<strong>Cohort B (3.10 GiB):</strong> <code>145</code> TIFF/BMP/RAW rasters at <code>5.41 bpb</code> (3-ch RGB stride <code>r = 0.81</code>)',
        '<strong>Cohort C (0.70 GiB):</strong> <code>485</code> exact duplicate burst/export copies (matching SHA-256)',
        '<strong>Safety Invariant:</strong> Zero JPEG recompression · Zero EXIF stripping in Lossless mode'
      ],
      pipeline: [
        '1. Cohort C (0.70 GiB dupes) → SHA-256 manifest dedup (0 bytes stored)',
        '2. Cohort A (6.20 GiB JPEG/HEIC) → FastStreamBypass Store at 2,050 MB/s',
        '3. Cohort B (3.10 GiB RAW/TIFF/BMP) → 3-ch DeltaTransform + Zstd (→ 1.38 GiB)'
      ],
      rejected: [
        '<strong>Uniform 7-Zip LZMA2 (-mx=9):</strong> Rejected — wastes <code>29m 10s</code> grinding JPEGs and still lands at <code>8.41 GiB</code> (vs <code>7.58 GiB</code>).',
        '<strong>Silent WebP/JPEG Re-encode:</strong> Strictly forbidden in compression profiles; available only in consent-gated Optimization Studio.'
      ],
      metrics: [
        { label: 'Lossless Output', value: '7.58 GiB', sub: '2.42 GiB saved · 100% bit-exact' },
        { label: 'Wall Time', value: '19.9 sec', sub: '88x faster than 7-Zip (29m 10s)' },
        { label: 'Pixel & EXIF Integrity', value: '100% Intact', sub: 'SHA-256 verified per file' },
        { label: 'Optional Studio', value: 'Consent-Gated', sub: 'Separate EXIF / WebP export flow' }
      ],
      reportJson: {
        operationKind: 'LosslessCompression',
        profile: 'Balanced',
        inputBytes: 10737418240,
        cohorts: [
          { class: 'ExactDuplicates', inputGiB: 0.7, outputGiB: 0.0, strategy: 'SHA256ManifestLink' },
          { class: 'CompressedMedia(JPEG/HEIC)', inputGiB: 6.2, outputGiB: 6.2, strategy: 'FastStreamBypass' },
          { class: 'UncompressedRaster(RAW/TIFF/BMP)', inputGiB: 3.1, outputGiB: 1.38, strategy: 'Delta3Ch+Zstd5' }
        ],
        mutatingTransformsApplied: false,
        studioHint: 'EXIF GPS hygiene and WebP export available separately in Optimization Studio (requires ConsentConfirmation)'
      }
    }
  ];

  function escapeHtml(str) {
    return String(str)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function initWorkloadExplorer() {
    const container = document.querySelector('[data-workload-explorer]');
    if (!container) return;

    const tabList = container.querySelector('[data-workload-tabs]');
    const panel = container.querySelector('[data-workload-panel]');
    if (!tabList || !panel) return;

    let activeIndex = 0;
    let showJson = false;

    function render() {
      const item = WORKLOADS[activeIndex];
      tabList.querySelectorAll('[role="tab"]').forEach((btn, idx) => {
        const selected = idx === activeIndex;
        btn.setAttribute('aria-selected', selected ? 'true' : 'false');
        btn.setAttribute('tabindex', selected ? '0' : '-1');
        btn.classList.toggle('is-active', selected);
      });

      panel.setAttribute('aria-labelledby', `workload-tab-${item.id}`);
      panel.innerHTML = `
        <div class="workload-top">
          <div>
            <span class="badge badge-accent">${escapeHtml(item.badge)}</span>
            <h3>${escapeHtml(item.title)}</h3>
            <p>${escapeHtml(item.summary)}</p>
          </div>
          <button type="button" class="btn btn-ghost btn-sm" data-toggle-report aria-expanded="${showJson ? 'true' : 'false'}">
            ${showJson ? 'Hide StrategyDecisionReport JSON' : 'Inspect StrategyDecisionReport JSON'}
          </button>
        </div>

        <div class="workload-columns">
          <div class="workload-box">
            <h4>1. ContentInspector Signals</h4>
            <ul class="signal-list">
              ${item.signals.map((s) => `<li>${s}</li>`).join('')}
            </ul>
          </div>
          <div class="workload-box">
            <h4>2. Selected Lossless Pipeline</h4>
            <div class="pipeline-steps">
              ${item.pipeline.map((step) => `<div class="pipeline-step">${escapeHtml(step)}</div>`).join('')}
            </div>
          </div>
          <div class="workload-box">
            <h4>3. Rejected Alternatives</h4>
            <ul class="rejected-list">
              ${item.rejected.map((r) => `<li>${r}</li>`).join('')}
            </ul>
          </div>
        </div>

        <div class="outcome-strip">
          ${item.metrics
            .map(
              (m) => `
            <div class="outcome-metric">
              <span>${escapeHtml(m.label)}</span>
              <strong>${escapeHtml(m.value)}</strong>
              <small>${escapeHtml(m.sub)}</small>
            </div>`
            )
            .join('')}
        </div>

        <div class="report-drawer" ${showJson ? '' : 'hidden'}>
          <pre class="code-block"><code>${escapeHtml(JSON.stringify(item.reportJson, null, 2))}</code></pre>
        </div>
      `;

      const reportBtn = panel.querySelector('[data-toggle-report]');
      if (reportBtn) {
        reportBtn.addEventListener('click', () => {
          showJson = !showJson;
          render();
        });
      }
    }

    tabList.innerHTML = WORKLOADS.map(
      (w, idx) => `
        <button
          type="button"
          role="tab"
          id="workload-tab-${escapeHtml(w.id)}"
          class="tab-btn${idx === 0 ? ' is-active' : ''}"
          aria-selected="${idx === 0 ? 'true' : 'false'}"
          tabindex="${idx === 0 ? '0' : '-1'}"
          data-idx="${idx}"
        >${escapeHtml(w.tabLabel)}</button>`
    ).join('');

    const buttons = Array.from(tabList.querySelectorAll('[role="tab"]'));
    buttons.forEach((btn, idx) => {
      btn.addEventListener('click', () => {
        activeIndex = idx;
        render();
      });
      btn.addEventListener('keydown', (ev) => {
        let next = idx;
        if (ev.key === 'ArrowRight') next = (idx + 1) % buttons.length;
        else if (ev.key === 'ArrowLeft') next = (idx - 1 + buttons.length) % buttons.length;
        else if (ev.key === 'Home') next = 0;
        else if (ev.key === 'End') next = buttons.length - 1;
        else return;
        ev.preventDefault();
        activeIndex = next;
        render();
        buttons[next].focus();
      });
    });

    render();
  }

  /* —— 3. Desktop Archive Manager Interactive Tour —— */
  const DESKTOP_VIEWS = [
    {
      id: 'pack',
      title: 'Drag & Drop Batch .ucx Archiving',
      subtitle: 'Live ContentInspector preview, duplicate badge & Destination Manager',
      windowTitle: 'UltraCompress Desktop — Pack & Inspect',
      badge: 'OperationKind: LosslessCompression',
      badgeClass: 'badge-success',
      description:
        'Drop mixed files and multi-gigabyte directories directly into the workspace. Before a single CPU core spins up, ContentInspector previews exact duplicate savings, per-class routing, destination free space, and the StrategyDecisionReport rationale card.',
      kvs: [
        { k: 'Selected Input', v: 'D:\\Projects\\StudioRelease (10.00 GiB · 42,180 files)' },
        { k: 'Duplicate Detection', v: '1.12 GiB exact SHA-256 duplicates collapsed automatically' },
        { k: 'Before → Predicted After', v: '10.00 GiB → ~3.85 GiB (Balanced · 100% Bit-Exact)' },
        { k: 'Destination Pre-Flight', v: 'E:\\Backups\\StudioRelease.ucx (412 GiB free — Passed)' }
      ],
      cli: 'uc pack "D:\\Projects\\StudioRelease" -o "E:\\Backups\\StudioRelease.ucx" --profile Balanced'
    },
    {
      id: 'browser',
      title: '.ucx Archive Browser & Selective Extract',
      subtitle: 'O(1) indexed manifest tree, per-file hashes & <45 ms single-file extract',
      windowTitle: 'UltraCompress Desktop — Archive Browser (monorepo.ucx)',
      badge: 'SHA-256 Verified Manifest',
      badgeClass: 'badge-accent',
      description:
        'Browse 185,000+ archived entries instantly without scanning solid streams. Filter by path or glob, inspect original vs packed bytes and deduplicated link counts, verify archive health, and extract only the files you need with strict path-traversal protection.',
      kvs: [
        { k: 'Manifest Load Time', v: '4.1 ms (indexed trailer seek · O(1) RAM)' },
        { k: 'Selection Filter', v: 'src/UltraCompress.Core/**/*.cs (64 files · 418 KiB)' },
        { k: 'Selective Extract Latency', v: '38 ms (decodes single 64 MiB solid block)' },
        { k: 'Integrity Check', v: 'XXH64 block + SHA-256 file digest verified on write' }
      ],
      cli: 'uc extract "E:\\Archives\\monorepo.ucx" --select "src/UltraCompress.Core/**/*.cs" -o "C:\\Restore"'
    },
    {
      id: 'queue',
      title: 'Resilient Queue, Pause/Resume & Crash Recovery',
      subtitle: 'SQLite jobs.db journaling, .part atomic staging & actionable remediation',
      windowTitle: 'UltraCompress Desktop — Active Queue & Recovery',
      badge: 'SQLite Crash Journal Active',
      badgeClass: 'badge-accent',
      description:
        'Pause, resume, or cancel jobs cleanly at chunk boundaries. Every job writes to atomic .ucx.part staging backed by %ProgramData%\\UltraCompress\\jobs.db — automatically resuming from the last verified chunk after power loss, OS reboot, or service restart.',
      kvs: [
        { k: 'Active Job #142', v: 'analytics-dump.ucx · Chunk 198/240 · 344 MB/s · ETA 00:05' },
        { k: 'Crash Recovery State', v: '1 interrupted job recovered at 82.5% (zero re-work)' },
        { k: 'Worker Isolation', v: 'Out-of-process worker · RAM capped at 256 MiB (Balanced SLA)' },
        { k: 'Actionable Error Card', v: 'Detects file lock PID, disk-full bytes needed, or ACL path' }
      ],
      cli: 'uc history --status active,recovered'
    },
    {
      id: 'studio',
      title: 'Consent-Gated Optimization & Hygiene Studio',
      subtitle: 'Strict boundary for Lossy, Format Conversion & EXIF/Symbol Removal',
      windowTitle: 'UltraCompress Desktop — Optimization & Data Hygiene Studio',
      badge: 'Explicit Consent Gate Required',
      badgeClass: 'badge-warn',
      description:
        'Mutating workflows never hide inside compression profiles. The dedicated Studio tab handles WebP/AVIF lossy exports, container transcoding, and EXIF GPS / PDB symbol stripping with side-by-side visual SSIM previews, itemized removal manifests, and PreserveOriginal = true locked by default.',
      kvs: [
        { k: 'Active Studio Mode', v: 'OperationKind.DataRemoval (EXIF GPS & Camera Serial)' },
        { k: 'Itemized Manifest', v: '1,820 JPEGs · 14.8 MiB GPS/MakerNote tags listed' },
        { k: 'Original Preservation', v: 'Locked ON — writes to .\\holiday-photos-sanitized\\' },
        { k: 'Execution Gate', v: 'Requires explicit ConsentConfirmation checkbox before start' }
      ],
      cli: 'uc hygiene ".\\holiday-photos" --strip exif-gps,camera-serial --out ".\\holiday-photos-sanitized" --consent-remove-data'
    }
  ];

  function initDesktopTour() {
    const rootEl = document.querySelector('[data-desktop-tour]');
    if (!rootEl) return;
    const navEl = rootEl.querySelector('[data-desktop-nav]');
    const viewEl = rootEl.querySelector('[data-desktop-view]');
    if (!navEl || !viewEl) return;

    let current = 0;

    function render() {
      const v = DESKTOP_VIEWS[current];
      navEl.querySelectorAll('[role="tab"]').forEach((btn, i) => {
        btn.setAttribute('aria-selected', i === current ? 'true' : 'false');
      });

      viewEl.innerHTML = `
        <div class="window-bar">
          <div class="window-dots" aria-hidden="true"><span></span><span></span><span></span></div>
          <span class="mono">${escapeHtml(v.windowTitle)}</span>
          <span class="badge ${escapeHtml(v.badgeClass)}">${escapeHtml(v.badge)}</span>
        </div>
        <div class="desktop-mock-body">
          <div>
            <h3 style="margin:0 0 0.45rem;font-size:1.2rem;">${escapeHtml(v.title)}</h3>
            <p style="margin:0 0 1rem;color:var(--text-muted);font-size:0.92rem;">${escapeHtml(v.description)}</p>
            <div class="mock-kv-grid">
              ${v.kvs
                .map(
                  (kv) => `
                <div class="mock-kv">
                  <span>${escapeHtml(kv.k)}</span>
                  <strong>${escapeHtml(kv.v)}</strong>
                </div>`
                )
                .join('')}
            </div>
          </div>
          <div>
            <div style="font-size:0.75rem;color:var(--text-muted);text-transform:uppercase;letter-spacing:0.04em;margin-bottom:0.35rem;">
              Equivalent CLI Command (100% Engine Parity)
            </div>
            <pre class="code-block" style="margin:0;padding:0.7rem 0.9rem;"><code>${escapeHtml(v.cli)}</code></pre>
          </div>
        </div>
      `;
    }

    navEl.innerHTML = DESKTOP_VIEWS.map(
      (v, i) => `
      <button type="button" role="tab" class="desktop-tab" aria-selected="${i === 0 ? 'true' : 'false'}" data-idx="${i}">
        <strong>${escapeHtml(v.title)}</strong>
        <span>${escapeHtml(v.subtitle)}</span>
      </button>`
    ).join('');

    navEl.querySelectorAll('[role="tab"]').forEach((btn, i) => {
      btn.addEventListener('click', () => {
        current = i;
        render();
      });
    });

    render();
  }

  /* —— 4. Dynamic Benchmark JSON Loader & Interactive Explorer —— */
  const METRIC_DEFS = [
    {
      key: 'outputGiB',
      label: 'Archive Size (GiB)',
      direction: 'asc',
      unit: 'GiB',
      format: (r) => `${r.outputGiB.toFixed(2)} GiB (${r.savingsPct.toFixed(1)}% saved)`
    },
    {
      key: 'compressMBps',
      label: 'Compress MB/s',
      direction: 'desc',
      unit: 'MB/s',
      format: (r) => `${r.compressMBps.toLocaleString()} MB/s`
    },
    {
      key: 'decompressMBps',
      label: 'Decompress MB/s',
      direction: 'desc',
      unit: 'MB/s',
      format: (r) => `${r.decompressMBps.toLocaleString()} MB/s`
    },
    {
      key: 'cpuPercent',
      label: 'CPU %',
      direction: 'asc',
      unit: '%',
      format: (r) => `${r.cpuPercent}% (${r.cpuSeconds.toLocaleString()} CPU-s)`
    },
    {
      key: 'peakRamMiB',
      label: 'Peak RAM (MiB)',
      direction: 'asc',
      unit: 'MiB',
      format: (r) => `${r.peakRamMiB.toLocaleString()} MiB`
    },
    {
      key: 'cpuSecPerGiBSaved',
      label: 'CPU-sec / GiB Saved',
      direction: 'asc',
      unit: 'CPU-s/GiB',
      format: (r) =>
        r.cpuEfficiencyNote
          ? r.cpuEfficiencyNote
          : `${r.cpuSecPerGiBSaved.toLocaleString()} CPU-s/GiB`
    }
  ];

  async function loadBenchmarkData() {
    const src = document.body.getAttribute('data-benchmark-src') || 'data/benchmarks.json';
    try {
      const res = await fetch(src, { cache: 'no-cache' });
      if (res.ok) {
        return await res.json();
      }
    } catch (_) {
      /* fallback to embedded script tag when opened over file:// */
    }
    const embedded = document.getElementById('uc-benchmark-fallback');
    if (embedded && embedded.textContent) {
      return JSON.parse(embedded.textContent);
    }
    throw new Error('Unable to load benchmark dataset.');
  }

  function formatCpuEfficiencyCell(r) {
    if (r.cpuEfficiencyNote) {
      return escapeHtml(r.cpuEfficiencyNote);
    }
    return `${r.cpuSecPerGiBSaved.toLocaleString()} s/GiB`;
  }

  function initBenchmarkWidgets() {
    const fullExplorer = document.querySelector('[data-benchmark-explorer]');
    const homePreview = document.querySelector('[data-benchmark-preview]');
    if (!fullExplorer && !homePreview) return;

    loadBenchmarkData()
      .then((dataset) => {
        if (homePreview) renderHomePreview(homePreview, dataset);
        if (fullExplorer) renderFullExplorer(fullExplorer, dataset);
      })
      .catch((err) => {
        const target = fullExplorer || homePreview;
        if (target) {
          target.innerHTML = `<div class="bench-callout"><p>Benchmark dataset failed to load: ${escapeHtml(err.message)}</p></div>`;
        }
      });
  }

  function renderHomePreview(container, dataset) {
    const tabsEl = container.querySelector('[data-preview-tabs]');
    const bodyEl = container.querySelector('[data-preview-body]');
    if (!tabsEl || !bodyEl) return;

    let activeCatId = dataset.categories[0].id;

    function update() {
      const cat = dataset.categories.find((c) => c.id === activeCatId) || dataset.categories[0];
      tabsEl.querySelectorAll('button').forEach((b) => {
        const active = b.getAttribute('data-cat') === cat.id;
        b.classList.toggle('is-active', active);
        b.setAttribute('aria-selected', active ? 'true' : 'false');
      });

      bodyEl.innerHTML = `
        <div class="bench-callout">
          <h3>${escapeHtml(cat.label)}</h3>
          <p style="margin-bottom:0.45rem;">${escapeHtml(cat.summary)}</p>
          <p><strong style="color:var(--accent-strong);">Why UltraCompress wins:</strong> ${escapeHtml(cat.whyUltraCompressWins)}</p>
        </div>
        <div class="table-shell">
          <table class="data-table">
            <thead>
              <tr>
                <th>Tool &amp; Profile</th>
                <th>Output Size</th>
                <th>Compress</th>
                <th>Decompress</th>
                <th>Peak RAM</th>
                <th>CPU Efficiency</th>
              </tr>
            </thead>
            <tbody>
              ${cat.results
                .map(
                  (r) => `
                <tr class="${r.isUltraCompress ? 'is-uc' : ''}">
                  <td>
                    <strong>${escapeHtml(r.tool)}</strong>
                    ${r.highlight ? `<div><span class="badge badge-accent" style="margin-top:0.25rem;">${escapeHtml(r.highlight)}</span></div>` : ''}
                  </td>
                  <td class="mono"><strong>${r.outputGiB.toFixed(2)} GiB</strong> <span style="color:var(--text-muted);">(${r.savingsPct.toFixed(1)}% saved)</span></td>
                  <td class="mono">${r.compressMBps.toLocaleString()} MB/s</td>
                  <td class="mono">${r.decompressMBps.toLocaleString()} MB/s</td>
                  <td class="mono">${r.peakRamMiB.toLocaleString()} MiB</td>
                  <td class="mono">${formatCpuEfficiencyCell(r)}</td>
                </tr>`
                )
                .join('')}
            </tbody>
          </table>
        </div>
      `;
    }

    tabsEl.innerHTML = dataset.categories
      .map(
        (c) => `
        <button type="button" role="tab" class="tab-btn" data-cat="${escapeHtml(c.id)}">
          ${escapeHtml(c.shortLabel)}
        </button>`
      )
      .join('');

    tabsEl.querySelectorAll('button').forEach((btn) => {
      btn.addEventListener('click', () => {
        activeCatId = btn.getAttribute('data-cat');
        update();
      });
    });

    update();
  }

  function renderFullExplorer(container, dataset) {
    const catTabs = container.querySelector('[data-bench-cats]');
    const metricBar = container.querySelector('[data-bench-metrics]');
    const calloutEl = container.querySelector('[data-bench-callout]');
    const chartEl = container.querySelector('[data-bench-chart]');
    const tableWrap = container.querySelector('[data-bench-table]');
    const hwEl = document.querySelector('[data-bench-hardware]');

    if (hwEl && dataset.hardware) {
      hwEl.innerHTML = `
        <li><strong>CPU:</strong> ${escapeHtml(dataset.hardware.cpu)}</li>
        <li><strong>Memory:</strong> ${escapeHtml(dataset.hardware.memory)}</li>
        <li><strong>NVMe Storage:</strong> ${escapeHtml(dataset.hardware.storage)}</li>
        <li><strong>OS &amp; Runtime:</strong> ${escapeHtml(dataset.hardware.os)} · ${escapeHtml(dataset.hardware.runtime)}</li>
        <li><strong>Measurement Protocol:</strong> ${escapeHtml(dataset.hardware.protocol)}</li>
      `;
    }

    let activeCatId = dataset.categories[0].id;
    let activeMetricKey = 'outputGiB';
    let sortKey = 'outputGiB';
    let sortDir = 'asc';

    function getMetricValue(row, key) {
      if (key === 'cpuSecPerGiBSaved' && row.savedGiB === 0) {
        return row.cpuSeconds;
      }
      return Number(row[key]) || 0;
    }

    function render() {
      const cat = dataset.categories.find((c) => c.id === activeCatId) || dataset.categories[0];
      const metricDef = METRIC_DEFS.find((m) => m.key === activeMetricKey) || METRIC_DEFS[0];

      catTabs.querySelectorAll('button').forEach((b) => {
        const active = b.getAttribute('data-cat') === cat.id;
        b.classList.toggle('is-active', active);
        b.setAttribute('aria-selected', active ? 'true' : 'false');
      });

      metricBar.querySelectorAll('button').forEach((b) => {
        const active = b.getAttribute('data-metric') === metricDef.key;
        b.classList.toggle('is-active', active);
        b.setAttribute('aria-pressed', active ? 'true' : 'false');
      });

      calloutEl.innerHTML = `
        <div style="display:flex;flex-wrap:wrap;justify-content:space-between;gap:0.75rem;align-items:center;margin-bottom:0.4rem;">
          <h3 style="margin:0;">${escapeHtml(cat.label)}</h3>
          <span class="badge badge-accent">Input: ${cat.inputGiB.toFixed(2)} GiB · SHA-256 Round-Trip Verified</span>
        </div>
        <p style="margin-bottom:0.45rem;">${escapeHtml(cat.summary)}</p>
        <p><strong style="color:var(--accent-strong);">Engineering Analysis:</strong> ${escapeHtml(cat.whyUltraCompressWins)}</p>
      `;

      /* Render visual bars sorted by activeMetricKey */
      const chartRows = [...cat.results].sort((a, b) => {
        const va = getMetricValue(a, metricDef.key);
        const vb = getMetricValue(b, metricDef.key);
        return metricDef.direction === 'asc' ? va - vb : vb - va;
      });

      const values = chartRows.map((r) => getMetricValue(r, metricDef.key));
      const maxVal = Math.max(...values, 1);

      chartEl.innerHTML = `
        <div style="display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:0.5rem;margin-bottom:0.25rem;">
          <strong style="font-size:0.92rem;">Visual Comparison — ${escapeHtml(metricDef.label)}</strong>
          <span style="font-size:0.8rem;color:var(--text-muted);">
            ${metricDef.direction === 'asc' ? 'Lower is better' : 'Higher is better'}
          </span>
        </div>
        ${chartRows
          .map((r) => {
            const rawVal = getMetricValue(r, metricDef.key);
            let widthPct = (rawVal / maxVal) * 100;
            if (metricDef.key === 'cpuSecPerGiBSaved' && maxVal > 5000) {
              /* Log-scaled bar on multi-order-of-magnitude CPU waste (e.g. MP4 video) so fast tools stay visible */
              widthPct = (Math.log10(rawVal + 1) / Math.log10(maxVal + 1)) * 100;
            }
            widthPct = Math.max(widthPct, 2.5);
            return `
              <div class="bar-row ${r.isUltraCompress ? 'is-uc' : ''}">
                <div class="bar-label">
                  ${escapeHtml(r.tool)}
                  <small>${escapeHtml(r.strategy)}</small>
                </div>
                <div class="bar-track" aria-hidden="true">
                  <div class="bar-fill" style="width:${widthPct.toFixed(1)}%"></div>
                </div>
                <div class="bar-value mono">${escapeHtml(metricDef.format(r))}</div>
              </div>
            `;
          })
          .join('')}
      `;

      /* Render sortable full table */
      const sortedTableRows = [...cat.results].sort((a, b) => {
        const va = getMetricValue(a, sortKey);
        const vb = getMetricValue(b, sortKey);
        return sortDir === 'asc' ? va - vb : vb - va;
      });

      const columns = [
        { key: 'outputGiB', label: 'Output (GiB)', defaultDir: 'asc' },
        { key: 'savingsPct', label: 'Saved %', defaultDir: 'desc' },
        { key: 'compressMBps', label: 'Compress MB/s', defaultDir: 'desc' },
        { key: 'decompressMBps', label: 'Decompress MB/s', defaultDir: 'desc' },
        { key: 'cpuPercent', label: 'CPU %', defaultDir: 'asc' },
        { key: 'peakRamMiB', label: 'Peak RAM', defaultDir: 'asc' },
        { key: 'cpuSecPerGiBSaved', label: 'CPU-s / GiB Saved', defaultDir: 'asc' }
      ];

      tableWrap.innerHTML = `
        <table class="data-table">
          <thead>
            <tr>
              <th>Tool, Strategy &amp; Exact Command</th>
              ${columns
                .map((col) => {
                  const isSorted = sortKey === col.key;
                  const arrow = isSorted ? (sortDir === 'asc' ? '↑' : '↓') : '↕';
                  const ariaSort = isSorted ? (sortDir === 'asc' ? 'ascending' : 'descending') : 'none';
                  return `<th aria-sort="${ariaSort}">
                    <button type="button" class="sort-btn" data-sort="${col.key}" data-default-dir="${col.defaultDir}">
                      ${escapeHtml(col.label)} <span aria-hidden="true">${arrow}</span>
                    </button>
                  </th>`;
                })
                .join('')}
            </tr>
          </thead>
          <tbody>
            ${sortedTableRows
              .map(
                (r) => `
              <tr class="${r.isUltraCompress ? 'is-uc' : ''}">
                <td>
                  <div style="display:flex;align-items:center;gap:0.45rem;flex-wrap:wrap;">
                    <strong>${escapeHtml(r.tool)}</strong>
                    ${r.highlight ? `<span class="badge badge-accent">${escapeHtml(r.highlight)}</span>` : ''}
                  </div>
                  <div style="color:var(--text-muted);font-size:0.81rem;margin:0.2rem 0;">${escapeHtml(r.strategy)}</div>
                  <code style="font-size:0.76rem;display:inline-block;">${escapeHtml(r.command)}</code>
                </td>
                <td class="mono"><strong>${r.outputGiB.toFixed(2)}</strong></td>
                <td class="mono">${r.savingsPct.toFixed(1)}%</td>
                <td class="mono">${r.compressMBps.toLocaleString()}</td>
                <td class="mono">${r.decompressMBps.toLocaleString()}</td>
                <td class="mono">${r.cpuPercent}% <span style="color:var(--text-muted);font-size:0.78rem;">(${r.cpuSeconds.toFixed(0)}s)</span></td>
                <td class="mono">${r.peakRamMiB.toLocaleString()} MiB</td>
                <td class="mono">${formatCpuEfficiencyCell(r)}</td>
              </tr>`
              )
              .join('')}
          </tbody>
        </table>
      `;

      tableWrap.querySelectorAll('[data-sort]').forEach((btn) => {
        btn.addEventListener('click', () => {
          const key = btn.getAttribute('data-sort');
          const defDir = btn.getAttribute('data-default-dir') || 'asc';
          if (sortKey === key) {
            sortDir = sortDir === 'asc' ? 'desc' : 'asc';
          } else {
            sortKey = key;
            sortDir = defDir;
          }
          render();
        });
      });
    }

    catTabs.innerHTML = dataset.categories
      .map(
        (c) => `
        <button type="button" role="tab" class="tab-btn" data-cat="${escapeHtml(c.id)}">
          ${escapeHtml(c.shortLabel)}
        </button>`
      )
      .join('');

    catTabs.querySelectorAll('button').forEach((btn) => {
      btn.addEventListener('click', () => {
        activeCatId = btn.getAttribute('data-cat');
        render();
      });
    });

    metricBar.innerHTML = METRIC_DEFS.map(
      (m) => `
        <button type="button" class="metric-pill" data-metric="${escapeHtml(m.key)}" aria-pressed="false">
          ${escapeHtml(m.label)}
        </button>`
    ).join('');

    metricBar.querySelectorAll('button').forEach((btn) => {
      btn.addEventListener('click', () => {
        activeMetricKey = btn.getAttribute('data-metric');
        const def = METRIC_DEFS.find((m) => m.key === activeMetricKey);
        if (def) {
          sortKey = def.key;
          sortDir = def.direction;
        }
        render();
      });
    });

    render();
  }

  document.addEventListener('DOMContentLoaded', () => {
    initTheme();
    initNav();
    initWorkloadExplorer();
    initDesktopTour();
    initBenchmarkWidgets();
  });
})();
