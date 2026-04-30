namespace PmcScraper;

public static class TicketManager
{
    // ─── Base Configuration ──────────────────────────────────
    public const int DefaultDelay = 500;    // ms — minimum interval between requests
    public const int MaximumDelay = 5000;   // ms — maximum delay cap
    private const int StepUp = 500;         // ms — delay increase on failure
    private const int IterationPoll = 15;   // ms — polling frequency in wait loop

    // Recovery phase: used when _delay is above _best20Delay (returning from an error spike)
    private const double RecoveryStepDownPct = 0.15; // 15% per step — fast return to known-good level
    private const int RecoveryAfterSuccesses = 3;    // only 3 consecutive successes needed
    // no hold-time guard during recovery — speed is safe because we're above the proven level

    // Exploration phase: used when _delay is at or below _best20Delay (pushing toward minimum)
    private const double ExploreStepDownPct = 0.04;  // 4% per step — cautious descent
    private const int ExploreAfterSuccesses = 8;     // 8 consecutive successes needed
    private const int ExploreHoldMs = 20_000;        // 20 s minimum between exploration steps

    // ─── State ──────────────────────────────────────────────
    private static readonly object _lock = new();
    private static readonly Queue<bool> _last5 = new();
    private static readonly Queue<bool> _last20 = new();
    private static DateTime _lastTime = DateTime.MinValue;
    public static int _delay = DefaultDelay * 2;
    public static int _best20Delay = 0;

    private static int _successStreak = 0;
    private static DateTime _lastDecreaseTime = DateTime.MinValue;

    // ─── Record Result of Each Request ───────────────────────
    public static void RecordResult(bool success)
    {
        lock (_lock)
        {
            Enqueue(_last5, success, 5);
            Enqueue(_last20, success, 20);

            if (success)
                _successStreak++;
            else
                _successStreak = 0;

            AdjustDelay();
        }
    }

    private static void Enqueue(Queue<bool> q, bool value, int maxSize)
    {
        q.Enqueue(value);
        if (q.Count > maxSize) q.Dequeue();
    }

    public static void IncreaseDelayOneStep()
    {
        lock (_lock)
        {
            _delay = Math.Min(_delay + StepUp, MaximumDelay);
            _successStreak = 0;
        }
    }

    // ─── Delay Adjustment Logic ───────────────────────────────
    private static void AdjustDelay()
    {
        // Require a full window of data before making any adjustment
        if (_last5.Count < 5 || _last20.Count < 20) return;

        double rate5 = _last5.Count(x => x) / (double)_last5.Count;
        double rate20 = _last20.Count(x => x) / (double)_last20.Count;

        if (_best20Delay == 0)
            _best20Delay = DefaultDelay * 4;

        // Track the fastest delay that achieved >95% success rate
        if (rate20 > 0.95)
        {
            int diff = Math.Abs(_delay - _best20Delay);
            if (diff < 100)
                _best20Delay = Math.Min(_delay, _best20Delay);
            else
                _best20Delay = (int)Math.Floor((_delay + _best20Delay) / 2.0);
        }

        // Weighted score: recent 5 requests carry more weight
        double combined = rate5 * 0.6 + rate20 * 0.4;

        if (combined < 0.30)
        {
            // Over 70% failure — high pressure, back off quickly
            _delay = Math.Min(_delay + StepUp * 3, MaximumDelay);
            _successStreak = 0;
        }
        else if (combined < 0.60)
        {
            // 40–70% failure — back off moderately
            _delay = Math.Min(_delay + StepUp, MaximumDelay);
            _successStreak = 0;
        }
        else if (combined > 0.95)
        {
            bool inRecovery = _best20Delay > 0 && _delay > _best20Delay;

            if (inRecovery)
            {
                // Above the known-good level — safe to return quickly; light cooldown only
                if (_successStreak >= RecoveryAfterSuccesses)
                {
                    // Step 15% toward _best20Delay; never overshoot it
                    int candidate = (int)Math.Round(_delay * (1.0 - RecoveryStepDownPct));
                    _delay = Math.Max(candidate, _best20Delay);

                    _successStreak = 0;
                    _lastDecreaseTime = DateTime.UtcNow;
                }
            }
            else
            {
                // At or below known-good level — explore cautiously with full cooldowns
                bool streakReady = _successStreak >= ExploreAfterSuccesses;
                bool holdReady = (DateTime.UtcNow - _lastDecreaseTime).TotalMilliseconds >= ExploreHoldMs;

                if (streakReady && holdReady)
                {
                    int candidate = Math.Max((int)Math.Round(_delay * (1.0 - ExploreStepDownPct)), DefaultDelay);
                    _delay = candidate;

                    _successStreak = 0;
                    _lastDecreaseTime = DateTime.UtcNow;
                }
            }
        }
        // Between 60–95% success — delay is stable, leave it unchanged
    }

    // ─── Acquire Ticket Before Sending a Request ─────────────
    /// <summary>
    /// Waits until enough time has passed since the last request,
    /// then allows the next request to proceed.
    /// </summary>
    public static async Task WaitForTicketAsync(CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int currentDelay;
            DateTime lastTime;

            lock (_lock)
            {
                currentDelay = _delay;
                lastTime = _lastTime;
            }

            var elapsed = (DateTime.UtcNow - lastTime).TotalMilliseconds;

            if (elapsed >= currentDelay)
            {
                lock (_lock) { _lastTime = DateTime.UtcNow; }
                return; // ticket granted
            }

            var waitMs = (int)(currentDelay - elapsed) + 1;
            await Task.Delay(Math.Min(waitMs, IterationPoll), ct);
        }
    }

    // ─── Current Status (for logging / debugging) ────────────
    public static (int Delay, double Rate5, double Rate20) GetStatus()
    {
        lock (_lock)
        {
            double r5 = _last5.Count > 0 ? _last5.Count(x => x) / (double)_last5.Count : 1.0;
            double r20 = _last20.Count > 0 ? _last20.Count(x => x) / (double)_last20.Count : 1.0;
            return (_delay, r5, r20);
        }
    }
}
