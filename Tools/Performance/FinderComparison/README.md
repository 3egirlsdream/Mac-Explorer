# Mac Explorer / Finder native comparison

This tool observes the official v1.0.64 binary externally. It never links benchmark code into the product or changes product behavior. Existing uncommitted DemoRecord/product work is left in place.

Run fixture generation through `Tools/Testing/run-isolated.sh`, using the bundled Python runtime (Pillow, NumPy, ReportLab and pypdf). `prepare.py` creates a new synthetic dataset and starts the extracted official application directly with the inherited `MACEXPLORER_TEST_ROOT`. The returned session contains a local ownership token; do not publish it.

`build_native.py` reuses the existing DemoRecord input and ScreenCaptureKit implementation in a generated build output and appends `CompareExtensions.swift`. `CompareNative` checks the run's root ownership and target executable identity. Recorder output is at 2880×1620 / nominal 30fps; the editable composition exports at 1920×1080. A window whitelist is mandatory: application filters can accidentally include another running instance with the same bundle identifier. Finder recordings include only the newly created controlled window. Preflight captures are not release assets.

`benchmark.py` returns to the shared parent before each trial, types the controlled path through the application's normal path UI, begins capture before Return, and saves the event host timestamp. Its external AX observer waits for the correct folder title and visible total count. It then performs an actual first-row click and records selection status. The interaction measurement is an upper bound after a count observation; do not present it as an independently measured earliest possible interaction. First visible file pixels need separate source-frame review. A directory count does not certify that every offscreen icon and metadata field has rendered.

One first-open trial plus five repeat trials are retained per size and application. Repeat order alternates, and medians/ranges must exclude the first-open series. First open is a new directory path, not a disk-cache-flushed cold start. Captured source PTS and native host timestamps share the host clock. `analyze_frames.swift` is read-only Vision text recognition over actual recorded frames; it does not fabricate software UI or infer application FPS.

Footprint sampling uses `proc_pid_rusage(RUSAGE_INFO_V4).ri_phys_footprint` in bytes. It is not RSS, virtual size, or the managed heap. Finder preview/search services may live outside its main process and must be described separately. No forced GC or cache flush is used.

Keep the display unlocked. Do not run concurrent native interaction jobs. Never close other user apps or restart Finder to obtain a favorable baseline. Index coverage must be independently proven before a filename-search timing is valid; `/private/tmp` is commonly excluded by Spotlight. An unavailable search comparison must be reported as unavailable, not as a slow result or a missing-result product defect.

All timing, fixture, codec and input results are evidence, not a substitute for viewing the exported pixels. Do not reuse old benchmark scores, old footage, or the tiny PNG fixtures for this 12MP workload. Finish the editable ChatCut composition from original source assets, not a flattened local edit.
