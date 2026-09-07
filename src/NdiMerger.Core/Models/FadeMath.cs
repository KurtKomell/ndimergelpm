namespace NdiMerger.Core.Models;

/// <summary>
/// Timed fade: reaches the target in exactly <c>durationSeconds</c>.
/// Linear interpolation — cheap on the render thread (no trig).
/// </summary>
internal struct FadeAnimator
{
    private float _from;
    private float _to;
    private float _elapsed;
    private float _lastTarget;
    private bool _armed;
    private bool _running;

    public bool IsRunning => _running;

    public void Snap(float value)
    {
        _from = value;
        _to = value;
        _lastTarget = value;
        _elapsed = 0f;
        _armed = true;
        _running = false;
    }

    /// <summary>
    /// Advance from the current draw value toward <paramref name="target"/>.
    /// Duration is wall-clock seconds for a full transition (0 = instant).
    /// </summary>
    public float Tick(float current, float target, float dt, float durationSeconds, out bool animating)
    {
        if (!_armed)
        {
            _from = current;
            _to = target;
            _lastTarget = target;
            _elapsed = 0f;
            _armed = true;
            _running = Abs(target - current) > 1e-5f;
        }
        else if (Abs(target - _lastTarget) > 1e-5f)
        {
            if (_running)
            {
                _to = target;
            }
            else
            {
                _from = current;
                _to = target;
                _elapsed = 0f;
                _running = Abs(target - current) > 1e-5f;
            }

            _lastTarget = target;
        }

        if (durationSeconds <= 0.001f || float.IsNaN(durationSeconds))
        {
            _from = target;
            _to = target;
            _elapsed = 0f;
            _running = false;
            animating = false;
            return target;
        }

        if (!_running)
        {
            animating = false;
            return current; // keep current — do not rewrite every frame
        }

        _elapsed += dt > 0f ? dt : 0f;
        float u = _elapsed / durationSeconds;
        if (u >= 1f)
        {
            _from = _to;
            _elapsed = durationSeconds;
            _running = false;
            animating = false;
            return _to;
        }

        animating = true;
        return _from + (_to - _from) * u;
    }

    private static float Abs(float v) => v < 0f ? -v : v;
}
