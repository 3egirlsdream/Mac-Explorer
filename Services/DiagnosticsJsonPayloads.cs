using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacExplorer.Services;

internal sealed record NativeNodesReleased(int Count, bool DetachedOnly);
internal sealed record FontTableAllocator(bool Installed, string Allocator, string AvaloniaVersion);
internal sealed record VolumeEjectFailure(bool Success, string Exception,
    [property: JsonPropertyName("HResult")] int HResult, double ElapsedMs);
internal sealed record VolumeEjectError(string? Domain, long Code);
internal sealed record VolumeEjectReport(bool Success, VolumeEjectError[]? Errors, int? ProcessId, double ElapsedMs);
internal sealed record RawDecodeReport(int Width, int Height, int MaxPixelSize);
internal sealed record ThumbnailHelperFailure(string Backend, [property: JsonPropertyName("ExitCode")] int ExitCode,
    int MaxPixelSize);
internal sealed record ThumbnailHelperCancellation(string Backend, int MaxPixelSize);
internal sealed record DiagnosticFailure(string Exception);
internal sealed record ImageDecodeStatistics(int Active, int Waiting, long ReservedBytes, long Decoded,
    long Rejected);
internal sealed record ImageDecodeRejection(string Format, [property: JsonPropertyName("Width")] int Width,
    [property: JsonPropertyName("Height")] int Height, int SampledWidth, int SampledHeight, int EncodedBytes,
    long EstimatedBytes);
internal sealed record LargeImageDecode(string Format, [property: JsonPropertyName("Width")] int Width,
    [property: JsonPropertyName("Height")] int Height, int TargetWidth, int TargetHeight, long EstimatedBytes);
internal sealed record WindowStateRequest(string Requested, string Trigger);
internal sealed record WindowStateChange(string? Previous, string? Current);
internal sealed record WindowRenderReport(int Schema, long WindowId, string WindowType, int Sequence, string Reason,
    object? Request, long WindowElapsedMs, long GeometryAgeMs, string State, bool Visible, bool Active,
    double[] ClientSize, double[] BoundsSize, double[]? ContentSize, double RenderScaling, double DesktopScaling,
    bool MeasureValid, bool ArrangeValid, double[] RootTransform, double[]? ContentTransform, string NativeStatus,
    JsonElement? Native, string[] Mismatches, int MismatchSamples, bool Persistent);
internal sealed record WindowRenderFailure(long WindowId, string Exception);
internal sealed record GcGenerationSample(string Generation, long SizeBeforeBytes, long SizeAfterBytes,
    long FragmentationBeforeBytes, long FragmentationAfterBytes);
internal sealed record GcSample(long SampleId, string Kind, bool Available, long Index, int Generation,
    bool Concurrent, bool Compacted, long HeapBytes, long FragmentedBytes, long CommittedBytes,
    long LiveAfterGcBytes, long PromotedBytes, long PinnedObjects, long FinalizationPending,
    double PauseTimePercentage, double[] PauseDurationsMs, long MemoryLoadBytes, long HighMemoryLoadThresholdBytes,
    long TotalAvailableMemoryBytes, GcGenerationSample[] Generations);
internal sealed record DiagnosticOperationReport(string Action, long ObjectId, long RelatedId, int? Count,
    bool? Visible, int? Mode);
internal sealed record DiagnosticSessionContext(int DiagnosticSchema, string? Version, string? Avalonia,
    string Channel, string Runtime, string Os, string Architecture, int Pid, bool ServerGc, string LatencyMode,
    Dictionary<string, object> GcConfiguration, DiagnosticPolicy Policy);
internal sealed record DiagnosticPolicy(int SampleSeconds, int QueueCapacity, int EventRateLimit,
    int MaxRecordCharacters, int MaxTaskFailureRecordCharacters, int RetentionDays, int FileBytes, int EventFiles,
    int TrendFiles, int SourceCapacity, int LifetimeCapacity, int EndedLifetimeHours);
internal sealed record MemorySample(long SampleId, long RssBytes, long? PhysicalFootprintBytes, long ManagedBytes,
    long HeapBytes, long FragmentedBytes, long LiveAfterGcBytes, long CommittedBytes, long AllocatedBytes,
    double? AllocationRateBytesPerSecond, long GcIndex, int Gen0, int Gen1, int Gen2, int Threads,
    long FootprintGrowthBytes, long JitCompiledIlBytes, long JitCompiledMethods, long SkiaCpuResourceBytes,
    long SkiaCpuFontBytes, DiagnosticHealth DiagnosticHealth, bool HighMemory, long DroppedEvents,
    ImageDecodeStatistics ImageDecode);
internal sealed record DiagnosticHealth(int Queued, int EventQueueCapacity, long RateLimited, long QueueFull,
    long Oversized, long SerializationFailed, long SourcesEvicted, long DeletedLogs, long DeletionFailures);
internal sealed record MemoryUiSample(long SampleId, double DispatcherDelayMs, int? Windows, int? Tabs,
    bool NativeAccessibilityFix, bool FontTablesPoh, MemoryDiagnosticState[] Sources);
internal sealed record MemoryObserverSample(long SampleId, double WorkerMs, double UiMs);
internal sealed record MemorySourceFailure(string Source, string Exception);
internal sealed record DiagnosticSessionStop(long DroppedEvents);
internal sealed record LifetimeState(long ObjectId, string Kind, long RelatedId, string Change, string State);
internal sealed record RetainedLifetime(long ObjectId, string Kind, long RelatedId, string State, long AgeSeconds,
    int Gen2Collections);
internal sealed record LifetimeSample(long SampleId, int Tracked, int Capacity, long Evicted, long Expired,
    LifetimeKindSample[] Kinds);
internal sealed record LifetimeKindSample(string Kind, int Alive, int CollectedSinceSample, int DetachedAlive,
    int EndedAlive, int EndedAliveAfterGen2);
internal sealed record RetainedLifetimes(long SampleId, int Limit, List<RetainedLifetime> Objects);
internal sealed record DiagnosticOperationStart(long OperationId, string Action, long ObjectId, long RelatedId);
internal sealed record DiagnosticOperationEnd(long OperationId, string Action, long ObjectId, long RelatedId,
    string Outcome, int? Count, double ElapsedMs);
internal sealed record DiagnosticTaskFinished(string Kind, string State, string? Exception, int Items,
    long RetainedBytes, bool Retry);
internal sealed record DiagnosticTaskFailure(string TaskId, string Kind, string? Operation, string? TargetDirectory,
    string? CurrentFile, string? Exception, int? HResult, int? NativeErrorCode, string? Message, string? Detail,
    bool DetailTruncated);
internal sealed record DiagnosticRetryStateReleased(int Count, int RetainedLimit, long ByteLimit);
internal sealed record SearchHistoryTrim(int Evicted, int History, long SnapshotBytes);
internal sealed record EmptyDiagnosticData;
