namespace PoeAncientsPriceHelper;

// Per-cycle timings pushed to the debug overlay (F3 / --debug).
internal sealed record ScanTimings(
    double CaptureMs,
    double DetectMs,
    double OcrMs,
    double MatchMs,
    double CycleMs,
    double? LastApiFetchMs = null,
    bool ApiFetching = false);
