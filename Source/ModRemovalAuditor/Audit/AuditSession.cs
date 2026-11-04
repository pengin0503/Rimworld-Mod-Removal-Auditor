using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ModRemovalAuditor.Audit
{
    /// <summary>
    /// One scan scope (map things, holders, world, terrain, ...). Implementations are driven on the
    /// main thread only; <see cref="Step"/> must do a small, bounded amount of work.
    /// </summary>
    public interface IScanSource : IObservationDescriber
    {
        string ScopeId { get; }

        /// <summary>If a critical source fails, the whole scan fails (e.g. provenance indexing).</summary>
        bool IsCritical { get; }

        void Begin();

        /// <summary>Performs one unit of work, appending observations. Returns true while more work remains.</summary>
        bool Step(List<AuditObservation> output);

        /// <summary>Checked, or Partial when the source hit a known limit (see <see cref="Warning"/>).</summary>
        CoverageStatus FinalStatus { get; }

        string Warning { get; }

        /// <summary>Releases snapshots and buffers. Always called, including on cancel and failure.</summary>
        void End();
    }

    public readonly struct ScanProgress
    {
        public ScanProgress(ScanState state, int sourceIndex, int sourceCount, string currentScope, long unitsProcessed)
        {
            State = state;
            SourceIndex = sourceIndex;
            SourceCount = sourceCount;
            CurrentScope = currentScope;
            UnitsProcessed = unitsProcessed;
        }

        public ScanState State { get; }

        public int SourceIndex { get; }

        public int SourceCount { get; }

        public string CurrentScope { get; }

        public long UnitsProcessed { get; }
    }

    /// <summary>
    /// Main-thread, bounded, cancellable scan: Idle -> Scanning -> Complete | Cancelled | Failed.
    /// One traversal serves every selected mod. Nothing here touches game state.
    /// </summary>
    public sealed class AuditSession
    {
        public const int DefaultNodeBudget = 300;

        public const double DefaultTimeBudgetMs = 2.0;

        private readonly IReadOnlyList<AuditTarget> targets;

        private readonly IReadOnlyList<IScanSource> sources;

        private readonly Func<AuditObservation, OriginResolution> resolve;

        private readonly IReadOnlyList<CoverageEntry> staticCoverage;

        private readonly Func<double> clockMs;

        private readonly Action<string> logError;

        private readonly Func<string> abortCheck;

        private readonly List<CoverageEntry> coverage = new List<CoverageEntry>();

        private readonly List<AuditObservation> buffer = new List<AuditObservation>(16);

        private AuditAccumulator accumulator;

        private int sourceIndex;

        private bool sourceBegun;

        private long sourceUnits;

        private long totalUnits;

        private readonly HashSet<string> loggedScopes = new HashSet<string>(StringComparer.Ordinal);

        public AuditSession(
            IReadOnlyList<AuditTarget> targets,
            IReadOnlyList<IScanSource> sources,
            Func<AuditObservation, OriginResolution> resolve,
            IReadOnlyList<CoverageEntry> staticCoverage = null,
            Func<double> clockMs = null,
            Action<string> logError = null,
            Func<string> abortCheck = null)
        {
            this.targets = targets ?? throw new ArgumentNullException(nameof(targets));
            this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
            this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
            this.staticCoverage = staticCoverage ?? Array.Empty<CoverageEntry>();
            this.clockMs = clockMs ?? DefaultClock;
            this.logError = logError;
            this.abortCheck = abortCheck;
        }

        public ScanState State { get; private set; } = ScanState.Idle;

        /// <summary>Available only after a completed scan; null while scanning, after cancel and after failure.</summary>
        public AuditReport Report { get; private set; }

        public string FailureMessage { get; private set; }

        public ScanProgress Progress => new ScanProgress(
            State,
            sourceIndex,
            sources.Count,
            sourceIndex < sources.Count ? sources[sourceIndex].ScopeId : null,
            totalUnits);

        public void Start()
        {
            if (State != ScanState.Idle)
            {
                throw new InvalidOperationException("A session can only be started once; create a new session to re-scan.");
            }
            List<ModKey> keys = new List<ModKey>(targets.Count);
            for (int i = 0; i < targets.Count; i++)
            {
                keys.Add(targets[i].Key);
            }
            accumulator = new AuditAccumulator(keys);
            State = ScanState.Scanning;
        }

        /// <summary>
        /// Advances the scan by at most <paramref name="nodeBudget"/> units or until
        /// <paramref name="timeBudgetMs"/> elapses, whichever comes first. At least one unit is always processed.
        /// </summary>
        public ScanProgress Advance(int nodeBudget, double timeBudgetMs)
        {
            if (State != ScanState.Scanning)
            {
                return Progress;
            }
            string abort = abortCheck?.Invoke();
            if (abort != null)
            {
                Fail(abort);
                return Progress;
            }

            double started = clockMs();
            int units = 0;
            while (State == ScanState.Scanning)
            {
                if (sourceIndex >= sources.Count)
                {
                    Complete();
                    break;
                }
                if (units > 0 && (units >= nodeBudget || clockMs() - started >= timeBudgetMs))
                {
                    break;
                }

                IScanSource source = sources[sourceIndex];
                if (!sourceBegun)
                {
                    sourceBegun = true;
                    sourceUnits = 0;
                    try
                    {
                        source.Begin();
                    }
                    catch (Exception ex)
                    {
                        FinishSource(source, CoverageStatus.Failed, Describe(ex), ex);
                        units++;
                        continue;
                    }
                }

                bool more;
                buffer.Clear();
                try
                {
                    more = source.Step(buffer);
                    for (int i = 0; i < buffer.Count; i++)
                    {
                        AuditObservation observation = buffer[i];
                        OriginResolution origin = resolve(observation);
                        accumulator.Add(observation, origin, source);
                    }
                }
                catch (Exception ex)
                {
                    sourceUnits++;
                    totalUnits++;
                    units++;
                    FinishSource(source, CoverageStatus.Failed, Describe(ex), ex);
                    continue;
                }
                finally
                {
                    buffer.Clear();
                }
                sourceUnits++;
                totalUnits++;
                units++;
                if (!more)
                {
                    CoverageStatus status;
                    string warning;
                    try
                    {
                        status = source.FinalStatus;
                        warning = source.Warning;
                    }
                    catch (Exception ex)
                    {
                        status = CoverageStatus.Failed;
                        warning = Describe(ex);
                    }
                    if (status != CoverageStatus.Checked && status != CoverageStatus.Partial)
                    {
                        status = CoverageStatus.Partial;
                    }
                    FinishSource(source, status, warning, null);
                }
            }
            return Progress;
        }

        /// <summary>Stops the scan and frees transient buffers. A cancelled scan never produces a report.</summary>
        public void Cancel()
        {
            if (State != ScanState.Scanning)
            {
                return;
            }
            EndCurrentSource();
            State = ScanState.Cancelled;
            accumulator?.Clear();
            accumulator = null;
            Report = null;
        }

        private void FinishSource(IScanSource source, CoverageStatus status, string warning, Exception error)
        {
            SafeEnd(source);
            sourceBegun = false;
            coverage.Add(new CoverageEntry(source.ScopeId, status, sourceUnits, warning));
            if (error != null)
            {
                LogOnce(source.ScopeId, "[ModRemovalAuditor] Scope '" + source.ScopeId + "' failed: " + error);
                if (source.IsCritical)
                {
                    Fail(Describe(error));
                    return;
                }
            }
            sourceIndex++;
            sourceUnits = 0;
        }

        private void Complete()
        {
            List<CoverageEntry> all = new List<CoverageEntry>(coverage.Count + staticCoverage.Count);
            all.AddRange(coverage);
            all.AddRange(staticCoverage);
            Report = accumulator.BuildReport(ScanState.Complete, targets, all, totalUnits);
            accumulator.Clear();
            accumulator = null;
            State = ScanState.Complete;
        }

        private void Fail(string message)
        {
            EndCurrentSource();
            FailureMessage = message;
            State = ScanState.Failed;
            accumulator?.Clear();
            accumulator = null;
            Report = null;
        }

        private void EndCurrentSource()
        {
            if (sourceBegun && sourceIndex < sources.Count)
            {
                SafeEnd(sources[sourceIndex]);
                sourceBegun = false;
            }
        }

        private void SafeEnd(IScanSource source)
        {
            try
            {
                source.End();
            }
            catch (Exception ex)
            {
                LogOnce(source.ScopeId, "[ModRemovalAuditor] Scope '" + source.ScopeId + "' failed to release: " + ex);
            }
        }

        /// <summary>Logs at most one error per scope so a broken mod cannot flood the log.</summary>
        private void LogOnce(string scopeId, string message)
        {
            if (!loggedScopes.Add(scopeId ?? string.Empty))
            {
                return;
            }
            logError?.Invoke(message);
        }

        private static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;

        private static double DefaultClock() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
    }
}
