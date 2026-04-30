namespace PmcScraper;

public static class TicketManager
{
    // ─── Base Configuration ──────────────────────────────────
    public const int DefaultDelay = 500;    // ms — minimum interval between requests
    public const int MaximumDelay = 5000;   // ms — maximum delay cap
    private const int StepUp = 500;    // ms — delay increase amount on failure
    private const int StepDown = 100;    // ms — delay decrease amount on success
    private const int IterationPoll = 15;   // ms — polling frequency in wait loop

    // ─── State ──────────────────────────────────────────────
    private static readonly object _lock = new();
    private static readonly Queue<bool> _last5 = new();
    private static readonly Queue<bool> _last20 = new();
    private static DateTime _lastTime = DateTime.MinValue;
    public static int _delay = DefaultDelay * 2;
    public static int _best20Delay = 0;

    // ─── Record Result of Each Request ───────────────────────
    public static void RecordResult(bool success)
    {
        lock (_lock)
        {
            Enqueue(_last5, success, 5);
            Enqueue(_last20, success, 20);
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
        _delay = Math.Min(_delay + StepUp, MaximumDelay);
    }

    // ─── Delay Adjustment Logic ───────────────────────────────
    private static void AdjustDelay()
    {
        // Wait for enough data before making decisions
        if (_last5.Count < 5 && _last20.Count < 10) return;

        double rate5 = _last5.Count > 0 ? _last5.Count(x => x) / (double)_last5.Count : 1.0;
        double rate20 = _last20.Count > 0 ? _last20.Count(x => x) / (double)_last20.Count : 1.0;
        if (_best20Delay == 0)
        {
            _best20Delay = DefaultDelay * 4;
        }


        // Track the fastest delay that achieved >95% success rate
        if (rate20 > 0.95)
        {
            int diff = Math.Abs(_delay - _best20Delay);
            if (diff < 100)
            {
                _best20Delay = Math.Min(_delay, _best20Delay);
            }
            else
            {
                _best20Delay = (int)Math.Floor((_delay + _best20Delay) / 2.0);
            }
        }

        // Weighted score: recent 5 requests carry more weight
        double combined = rate5 * 0.6 + rate20 * 0.4;

        if (combined < 0.30)
        {
            // Over 70% failure — high pressure, back off quickly
            _delay = Math.Min(_delay + StepUp * 3, MaximumDelay);
        }
        else if (combined < 0.60)
        {
            // Between 40–70% failure — back off moderately
            _delay = Math.Min(_delay + StepUp, MaximumDelay);
        }
        else if (combined > 0.90)
        {
            // Over 90% success — safe to speed up slightly
            int candidate = Math.Max(_delay - StepDown, DefaultDelay);

            if (_best20Delay > 0 && candidate < _best20Delay && Math.Abs(_best20Delay - candidate) > StepDown)
            {
                // Snap toward best known delay instead of going below it
                _delay = (int)(_best20Delay - Math.Floor(StepDown / 4.0));
            }
            else
            {
                // Normal step down
                _delay = candidate;
            }
        }
        // Between 60–90% success — delay is stable, leave it unchanged
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