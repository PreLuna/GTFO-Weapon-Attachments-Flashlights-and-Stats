using System;

namespace PreLuna.WeaponAttachments.Execution;

// Only withdraw an owned write while it still matches; a new external write becomes the next upstream baseline.
public sealed class OwnedValue
{
    private bool _owned;
    private double _upstream, _last;
    public double Observe(double current)
    {
        if (!_owned || current != _last) { _upstream = current; _owned = false; }
        return _upstream;
    }
    public double Apply(double current, Func<double, double> calculate)
    {
        double baseline = Observe(current);
        double value = calculate(baseline);
        if (!double.IsFinite(value)) throw new ArgumentException("实例属性不能写入非有限值。");
        _last = value; _owned = value != baseline;
        return value;
    }
    public double Withdraw(double current)
    {
        double baseline = Observe(current); _owned = false;
        return baseline;
    }
    // Store exactly what the native float field can retain, rather than a double it will round.
    public float ApplySingle(float current, Func<double, double> calculate) =>
        (float)Apply(current, baseline => (float)calculate(baseline));
}
