using System;

namespace SwiftGet.Engine.Turbo;

/// <summary>
/// Adaptive, self-tuning connection controller using BBR/AIMD principles:
/// slow-start ramp-up, plateau detection, periodic hill-climbing probes, and multiplicative backoff.
/// </summary>
public sealed class AdaptiveConcurrencyController : IConcurrencyController
{
    private readonly int _minConnections;
    private readonly int _maxConnections;
    private readonly TimeSpan _probeInterval;
    private readonly object _sync = new();

    private int _targetConnections;
    private ControllerPhase _currentPhase = ControllerPhase.SlowStart;
    private long _baselineRateBytesPerSec;
    private int _baselineConnections;
    private TimeSpan _timeInStep = TimeSpan.Zero;
    private DateTime _backoffUntilUtc = DateTime.MinValue;

    public int TargetConnections
    {
        get
        {
            lock (_sync) return _targetConnections;
        }
    }

    public ControllerPhase CurrentPhase
    {
        get
        {
            lock (_sync) return _currentPhase;
        }
    }

    public AdaptiveConcurrencyController(
        int initialConnections = 6,
        int minConnections = 2,
        int maxConnections = 32,
        TimeSpan? probeInterval = null)
    {
        _minConnections = Math.Max(1, minConnections);
        _maxConnections = Math.Max(_minConnections, maxConnections);
        _targetConnections = Math.Clamp(initialConnections, _minConnections, _maxConnections);
        _probeInterval = probeInterval ?? TimeSpan.FromSeconds(4);
    }

    public int Evaluate(CapacityEstimate estimate, TimeSpan elapsedSinceLastEval)
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            if (now < _backoffUntilUtc)
            {
                _currentPhase = ControllerPhase.Backoff;
                return _targetConnections;
            }

            var currentRate = estimate.CurrentThroughputBytesPerSec;
            _timeInStep += elapsedSinceLastEval;

            switch (_currentPhase)
            {
                case ControllerPhase.Backoff:
                    _currentPhase = ControllerPhase.SlowStart;
                    _baselineRateBytesPerSec = currentRate;
                    _baselineConnections = _targetConnections;
                    _timeInStep = TimeSpan.Zero;
                    break;

                case ControllerPhase.SlowStart:
                    // Evaluate every tick once new connections deliver data (>= 150ms)
                    if (_timeInStep >= TimeSpan.FromMilliseconds(150))
                    {
                        _timeInStep = TimeSpan.Zero;

                        if (_baselineRateBytesPerSec == 0)
                        {
                            // Initial baseline sample established
                            _baselineRateBytesPerSec = currentRate;
                            _baselineConnections = _targetConnections;
                            if (_targetConnections < _maxConnections)
                            {
                                int step = _targetConnections <= 4 ? 2 : Math.Max(2, _targetConnections / 2);
                                _targetConnections = Math.Min(_maxConnections, _targetConnections + step);
                            }
                        }
                        else
                        {
                            double growth = _baselineRateBytesPerSec > 0
                                ? (double)(currentRate - _baselineRateBytesPerSec) / _baselineRateBytesPerSec
                                : 1.0;

                            if (growth >= 0.08 && _targetConnections < _maxConnections)
                            {
                                // Good scaling: step up connections aggressively
                                _baselineRateBytesPerSec = currentRate;
                                _baselineConnections = _targetConnections;
                                int step = _targetConnections <= 4 ? 2 : Math.Max(2, _targetConnections / 2);
                                _targetConnections = Math.Min(_maxConnections, _targetConnections + step);
                            }
                            else
                            {
                                // Plateau reached: lock in best concurrency
                                if (growth < 0 && _baselineConnections > 0)
                                {
                                    _targetConnections = _baselineConnections;
                                }
                                _currentPhase = ControllerPhase.SteadyState;
                            }
                        }
                    }
                    break;

                case ControllerPhase.SteadyState:
                    if (_timeInStep >= _probeInterval)
                    {
                        _timeInStep = TimeSpan.Zero;
                        if (_targetConnections < _maxConnections)
                        {
                            _currentPhase = ControllerPhase.ProbingUp;
                            _baselineConnections = _targetConnections;
                            _baselineRateBytesPerSec = currentRate;
                            _targetConnections++;
                        }
                        else if (_targetConnections > _minConnections)
                        {
                            _currentPhase = ControllerPhase.ProbingDown;
                            _baselineConnections = _targetConnections;
                            _baselineRateBytesPerSec = currentRate;
                            _targetConnections--;
                        }
                    }
                    break;

                case ControllerPhase.ProbingUp:
                    if (_timeInStep >= TimeSpan.FromMilliseconds(800))
                    {
                        _timeInStep = TimeSpan.Zero;
                        double gain = _baselineRateBytesPerSec > 0
                            ? (double)(currentRate - _baselineRateBytesPerSec) / _baselineRateBytesPerSec
                            : 0;

                        if (gain < 0.05)
                        {
                            // Throughput did not improve: revert probe
                            _targetConnections = _baselineConnections;
                        }
                        _currentPhase = ControllerPhase.SteadyState;
                    }
                    break;

                case ControllerPhase.ProbingDown:
                    if (_timeInStep >= TimeSpan.FromMilliseconds(800))
                    {
                        _timeInStep = TimeSpan.Zero;
                        double drop = _baselineRateBytesPerSec > 0
                            ? (double)(_baselineRateBytesPerSec - currentRate) / _baselineRateBytesPerSec
                            : 0;

                        if (drop > 0.06)
                        {
                            // Rate dropped: restore previous connection count
                            _targetConnections = _baselineConnections;
                        }
                        _currentPhase = ControllerPhase.SteadyState;
                    }
                    break;
            }

            return _targetConnections;
        }
    }

    public void ReportThrottled(TimeSpan? retryAfter = null)
    {
        lock (_sync)
        {
            var pause = retryAfter ?? TimeSpan.FromSeconds(2);
            _backoffUntilUtc = DateTime.UtcNow.Add(pause);
            _currentPhase = ControllerPhase.Backoff;

            // Multiplicative decrease (AIMD halving)
            _targetConnections = Math.Max(_minConnections, (int)Math.Round(_targetConnections * 0.5));
            _baselineRateBytesPerSec = 0;
            _timeInStep = TimeSpan.Zero;
        }
    }

    public void ReportSuccess()
    {
    }
}
