namespace PmcScraper;

/// <summary>
/// Describes the outcome of a single PMC HTTP request.
/// Each outcome carries a different pressure weight inside the PID controller.
/// </summary>
public enum RequestOutcome
{
    Success,        // article fetched and parsed correctly
    EmptyResponse,  // HTML returned but title is missing — soft ban signal
    Timeout,        // request timed out (TaskCanceledException)
    HttpError,      // non-ban HTTP error (4xx / 5xx except 429)
    HttpBan,        // HTTP 429 / confirmed rate-limit — triggers a hard pause
    OtherError,     // any other failure
}

/// <summary>
/// PID-based inter-request throttle.
///
/// The controller measures a rolling "pressure" signal built from weighted
/// request outcomes, then drives <see cref="_delay"/> so that pressure
/// converges to <see cref="TargetPressure"/>.
///
/// Separate asymmetric gains ensure the delay rises quickly on errors and
/// descends conservatively once conditions improve.
/// </summary>
public static class TicketManager
{
    // ─── Delay Bounds ────────────────────────────────────────
    public const int DefaultDelay = 700;   // ms — minimum inter-request gap
    public const int MaximumDelay = 2000;  // ms — hard cap
    private const int IterationPoll = 15;  // ms — polling granularity inside wait loop

    // ─── PID Tuning ──────────────────────────────────────────
    /// <summary>Tolerate up to 8 % weighted error pressure before raising delay.</summary>
    private const double TargetPressure = 0.08;
    private const double Kp = 400.0;    // proportional — ms of change per unit of pressure error
    private const double Ki = 15.0;     // integral     — corrects sustained deviation
    private const double Kd = 200.0;    // derivative   — reacts to sudden pressure spikes
    private const double IntegralClamp = 12.0;  // anti-windup guard
    private const int WindowSize = 30;          // rolling pressure window (requests)

    // ─── Outcome → Pressure Weights ──────────────────────────
    // Indexed by (int)RequestOutcome; higher = more stress on the system.
    private static readonly double[] PressureOf =
    {
        0.00,  // Success
        0.50,  // EmptyResponse — soft signal (content gating)
        0.85,  // Timeout       — connection stress
        1.00,  // HttpError     — server rejected request
        2.00,  // HttpBan       — confirmed rate-limit (also triggers pause)
        0.60,  // OtherError
    };

    // ─── Pause Configuration ─────────────────────────────────
    private const int BanPauseMs            = 60_000;  // 60 s on HTTP 429
    private const int ErrorPauseMs          = 20_000;  // 20 s after N consecutive errors
    private const int ConsecutiveErrorLimit = 5;       // consecutive failures that trigger soft pause

    // ─── State ──────────────────────────────────────────────
    private static readonly object _lock = new();
    private static readonly Queue<double> _window = new();  // rolling pressure samples

    private static DateTime _lastTime     = DateTime.MinValue;
    private static DateTime _pauseUntil   = DateTime.MinValue;
    private static DateTime _lastPidTime  = DateTime.UtcNow;

    public static int _delay     = DefaultDelay * 2;
    public static int _bestDelay = 0;  // lowest delay that sustained acceptable pressure

    // PID internals
    private static double _integral          = 0;
    private static double _lastPressureError = 0;
    private static int    _consecutiveErrors = 0;

    // ─── Public API ──────────────────────────────────────────

    public static void RecordOutcome(RequestOutcome outcome)
    {
        lock (_lock)
        {
            double p = PressureOf[(int)outcome];

            _window.Enqueue(p);
            if (_window.Count > WindowSize) _window.Dequeue();

            if (outcome == RequestOutcome.Success)
                _consecutiveErrors = 0;
            else
                _consecutiveErrors++;

            double rolling = _window.Average();
            RunPid(rolling);
            ApplyPause(outcome);
            UpdateBestDelay(rolling);
        }
    }

    /// <summary>Legacy shim — prefer <see cref="RecordOutcome"/>.</summary>
    public static void RecordResult(bool success) =>
        RecordOutcome(success ? RequestOutcome.Success : RequestOutcome.OtherError);

    // ─── PID Controller ───────────────────────────────────────
    private static void RunPid(double rollingPressure)
    {
        var now = DateTime.UtcNow;
        // Clamp dt so long pauses / idle gaps don't cause huge integral / derivative jumps
        double dt = Math.Clamp((now - _lastPidTime).TotalSeconds, 0.05, 5.0);
        _lastPidTime = now;

        double error = rollingPressure - TargetPressure;

        _integral          = Math.Clamp(_integral + error * dt, -IntegralClamp, IntegralClamp);
        double derivative  = (error - _lastPressureError) / dt;
        _lastPressureError = error;

        double output = Kp * error + Ki * _integral + Kd * derivative;

        // Asymmetric response: full speed up, 55 % speed down (conservative recovery)
        int delta = output > 0
            ? (int)Math.Round(output)
            : (int)Math.Round(output * 0.55);

        _delay = Math.Clamp(_delay + delta, DefaultDelay, MaximumDelay);
    }

    // ─── Pause Logic ─────────────────────────────────────────
    private static void ApplyPause(RequestOutcome outcome)
    {
        DateTime until;

        if (outcome == RequestOutcome.HttpBan)
        {
            until     = DateTime.UtcNow.AddMilliseconds(BanPauseMs);
            _integral = IntegralClamp;  // keep delay high once the pause expires
        }
        else if (_consecutiveErrors >= ConsecutiveErrorLimit)
        {
            until = DateTime.UtcNow.AddMilliseconds(ErrorPauseMs);
        }
        else return;

        if (until > _pauseUntil)
            _pauseUntil = until;
    }

    // ─── Best-Delay Tracker ───────────────────────────────────
    private static void UpdateBestDelay(double rolling)
    {
        // Only record when the full window is in and pressure is comfortably below target
        if (_window.Count < WindowSize || rolling >= TargetPressure * 1.5) return;

        _bestDelay = _bestDelay == 0
            ? _delay
            : Math.Min(_bestDelay, _delay);
    }

    // ─── Ticket Acquisition ───────────────────────────────────
    /// <summary>
    /// Waits for any active pause to expire, then enforces the inter-request
    /// spacing defined by <see cref="_delay"/> before granting a ticket.
    /// </summary>
    public static async Task WaitForTicketAsync(CancellationToken ct = default)
    {
        // Phase 1 — honor any hard/soft pause
        DateTime pauseUntil;
        lock (_lock) { pauseUntil = _pauseUntil; }

        int pauseMs = (int)(pauseUntil - DateTime.UtcNow).TotalMilliseconds;
        if (pauseMs > 0)
            await Task.Delay(pauseMs, ct);

        // Phase 2 — normal inter-request spacing
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int currentDelay;
            DateTime lastTime;
            lock (_lock)
            {
                currentDelay = _delay;
                lastTime     = _lastTime;
            }

            var elapsed = (DateTime.UtcNow - lastTime).TotalMilliseconds;
            if (elapsed >= currentDelay)
            {
                lock (_lock) { _lastTime = DateTime.UtcNow; }
                return;
            }

            var waitMs = (int)(currentDelay - elapsed) + 1;
            await Task.Delay(Math.Min(waitMs, IterationPoll), ct);
        }
    }

    // ─── Status ──────────────────────────────────────────────
    public static (int Delay, int BestDelay, double Pressure, bool Paused) GetStatus()
    {
        lock (_lock)
        {
            double p    = _window.Count > 0 ? _window.Average() : 0.0;
            bool paused = DateTime.UtcNow < _pauseUntil;
            return (_delay, _bestDelay, p, paused);
        }
    }
}
