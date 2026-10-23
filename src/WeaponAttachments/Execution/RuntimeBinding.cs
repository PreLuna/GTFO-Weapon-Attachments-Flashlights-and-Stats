using System;
using System.Collections.Generic;
using System.Linq;
using PreLuna.WeaponAttachments.Definitions;
using PreLuna.WeaponAttachments.Loadouts;
using PreLuna.WeaponAttachments.Modifiers;

namespace PreLuna.WeaponAttachments.Execution;

public enum RuntimeEvent { Shot, Hit, WeakpointHit, Kill }
public interface IAmmoEndpoint
{
    int Clip { get; set; }
    int ClipCapacity { get; }
    double Reserve { get; set; }
    int ReserveCapacity { get; }
}

// This object owns only this instance's selection and trigger state, never an EWC template or controller.
public sealed class RuntimeBinding
{
    private readonly AttachmentCatalog _catalog;
    private readonly LoadoutManager _loadouts;
    private readonly ModifierCalculator _calculator;
    private sealed record RecordedEvent(long Serial, double Time, long Count, int Consecutive);
    private readonly Dictionary<RuntimeEvent, Queue<RecordedEvent>> _history = new();
    private readonly Dictionary<RuntimeEvent, long> _totals = new();
    private readonly Dictionary<RuntimeEvent, long> _latest = new();
    private readonly Dictionary<string, long> _lastEvents = new(StringComparer.Ordinal);
    private long _eventSerial;
    private long _revision = -1;
    private long _generation = -1;
    private int _suppression;
    private readonly Dictionary<string, StatInput> _observed = new(StringComparer.Ordinal);
    public IReadOnlyList<StatInput> ObservedUpstream => Array.AsReadOnly(_observed.Values.ToArray());
    public IReadOnlyCollection<string> ImplementedCapabilities => _capabilities;
    public IDisposable ExcludeOwnEffects() { _suppression++; return new Exclusion(this); }
    private sealed class Exclusion : IDisposable
    {
        private RuntimeBinding? _binding;
        public Exclusion(RuntimeBinding binding) { _binding = binding; }
        public void Dispose() { if (_binding != null) { _binding._suppression--; _binding = null; } }
    }
    private LoadoutSnapshot _active = null!;
    private AttachmentDefinition[] _definitions = Array.Empty<AttachmentDefinition>();
    private IReadOnlyCollection<string> _capabilities;
    private IReadOnlyCollection<string> _weaponCapabilities;
    private double _lastTime;
    private int _consecutive;
    private bool _shotHit, _shotWeak;
    private long _shotSerial;
    public ConfigurationTarget Target { get; }
    public WeaponIdentity Weapon { get; }
    public string InstanceId { get; }
    public bool HasSelection => _definitions.Length > 0;
    public IReadOnlyList<string> UnavailableSelections { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<CalculationIssue> LastCalculationIssues { get; private set; } = Array.Empty<CalculationIssue>();
    public IReadOnlyList<AttachmentDefinition> Definitions { get { Refresh(); return Array.AsReadOnly(_definitions); } }
    public long UpstreamVersion { get; private set; }

    public RuntimeBinding(AttachmentCatalog catalog, LoadoutManager loadouts, ModifierCalculator calculator,
        ConfigurationTarget target, WeaponIdentity weapon, string instanceId, IReadOnlyCollection<string> capabilities,
        IReadOnlyCollection<string>? weaponCapabilities = null)
    {
        _catalog = catalog; _loadouts = loadouts; _calculator = calculator;
        Target = target; Weapon = weapon; InstanceId = instanceId;
        _capabilities = capabilities.ToArray(); _weaponCapabilities = (weaponCapabilities ?? Array.Empty<string>()).ToArray();
        Refresh();
    }
    public void SetCapabilities(IReadOnlyCollection<string> capabilities, IReadOnlyCollection<string>? weaponCapabilities = null)
    {
        if (_capabilities.SequenceEqual(capabilities) && (weaponCapabilities == null || _weaponCapabilities.SequenceEqual(weaponCapabilities))) return;
        _capabilities = capabilities.ToArray();
        if (weaponCapabilities != null) _weaponCapabilities = weaponCapabilities.ToArray();
        _revision = -1; Refresh();
    }
    public void Refresh()
    {
        if (_revision == _loadouts.Revision && _generation == _loadouts.ReloadGeneration) return;
        _revision = _loadouts.Revision;
        _generation = _loadouts.ReloadGeneration;
        var selected = _loadouts.GetCommitted(Target, Weapon, _weaponCapabilities, _capabilities);
        bool WithinHistoryLimit(string id) => !(_catalog.Find(id)?.Definition.Modifiers.Select(item => item.Condition)
            .Concat(_catalog.Find(id)!.Definition.Mechanisms.Select(item => item.Condition))
            .Any(condition => condition?.Parameters.ContainsKey("window_seconds") == true &&
                condition.Parameters.TryGetValue("count", out double count) && count > 8192) ?? false);
        var unavailable = selected.Selections.Where(item => !item.Applicable || !item.RuntimeReady || !WithinHistoryLimit(item.RequestedId))
            .Select(item => item.RequestedId + ":" + (!WithinHistoryLimit(item.RequestedId) ? "TIMED_COUNT_LIMIT" : item.Code) +
                ":" + string.Join(",", item.MissingCapabilities)).ToArray();
        _active = selected with { Selections = Array.AsReadOnly(selected.Selections.Select(item => item.RuntimeReady && item.Applicable && WithinHistoryLimit(item.RequestedId) ? item :
            item with { RequestedId = SlotCatalog.OriginalId(item.Slot) }).ToArray()) };
        var definitions = _active.Selections.Select(item => _catalog.Find(item.RequestedId)!).Where(item => !item.IsOriginal).Select(item => item.Definition).ToArray();
        bool changed = !_definitions.SequenceEqual(definitions);
        _definitions = definitions;
        UnavailableSelections = Array.AsReadOnly(unavailable);
        if (changed) ResetTriggers();
    }
    public bool HasStat(string stat)
    {
        Refresh();
        return _definitions.Any(item => item.Modifiers.Any(modifier => modifier.Stat == stat));
    }
    public double Calculate(string stat, double upstream, double time, bool aiming = false, bool crouched = false, StatValueForm form = StatValueForm.Direct)
    {
        Refresh();
        if (double.IsFinite(upstream) && upstream >= 0) _observed[stat] = new(stat, upstream, Form: form);
        if (_suppression > 0 || !HasStat(stat)) return upstream;
        var snapshot = new UpstreamSnapshot(Target, Weapon, InstanceId, "gtfo.runtime", ++UpstreamVersion,
            new[] { new StatInput(stat, upstream, Form: form) }, weaponCapabilities: _weaponCapabilities);
        var result = _calculator.CalculateSelectedStats(_active, snapshot, new[] { stat }, Conditions(time, aiming, crouched));
        LastCalculationIssues = result.Issues;
        var row = result.Stats.FirstOrDefault(item => item.Stat == stat);
        return row?.CanCalculate == true && row.Value.HasValue ? row.Value.Value : upstream;
    }
    public ConditionSnapshot Conditions(double time, bool aiming, bool crouched, IEnumerable<ConditionDefinition>? previewConditions = null)
    {
        CheckTime(time);
        var states = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var condition in _definitions.SelectMany(item => item.Modifiers.Select(modifier => modifier.Condition)
            .Concat(item.Mechanisms.Select(mechanism => mechanism.Condition))).Where(item => item != null).Cast<ConditionDefinition>()
            .Concat(previewConditions ?? Array.Empty<ConditionDefinition>()).DistinctBy(ConditionSnapshot.Key))
        {
            bool? active = condition.Id switch
            {
                "always" => true, "aiming" => aiming, "crouched" => crouched,
                "shots_fired" => Count(RuntimeEvent.Shot, condition, time) >= condition.Parameters["count"],
                "weakpoint_hits" => Count(RuntimeEvent.WeakpointHit, condition, time) >= condition.Parameters["count"],
                "kills" => Count(RuntimeEvent.Kill, condition, time) >= condition.Parameters["count"],
                "consecutive_hits" => _consecutive >= condition.Parameters["count"] &&
                    (!condition.Parameters.ContainsKey("window_seconds") || Count(RuntimeEvent.Hit, condition, time) >= condition.Parameters["count"]),
                _ => null
            };
            if (active.HasValue) states[ConditionSnapshot.Key(condition)] = active.Value;
        }
        return new(states);
    }
    public void BeginShot(double time)
    {
        Refresh(); _shotHit = false; _shotWeak = false; Record(RuntimeEvent.Shot, time); _shotSerial = _eventSerial;
    }
    public void FinishShot() { if (!_shotHit) _consecutive = 0; }
    public void RecordHit(double time, bool weakpoint)
    {
        if (!_shotHit) { _shotHit = true; _consecutive = Math.Min(int.MaxValue - 1, _consecutive + 1); Record(RuntimeEvent.Hit, time); }
        if (weakpoint && !_shotWeak) { _shotWeak = true; Record(RuntimeEvent.WeakpointHit, time); }
    }
    public void RecordKill(double time) => Record(RuntimeEvent.Kill, time);
    public int ReturnAmmo(RuntimeEvent trigger, long triggerId, IAmmoEndpoint endpoint, double time, bool aiming, bool crouched, Func<double> random)
    {
        Refresh();
        var conditions = Conditions(time, aiming, crouched);
        int added = 0;
        foreach (var definition in _definitions)
        for (int i = 0; i < definition.Mechanisms.Count; i++)
        {
            var mechanism = definition.Mechanisms[i];
            if (mechanism.Capability is not ("ammo_return_to_clip" or "ammo_return_to_reserve")) continue;
            RuntimeEvent expected = mechanism.Condition?.Id switch
            {
                "kills" => RuntimeEvent.Kill, "weakpoint_hits" => RuntimeEvent.WeakpointHit,
                "consecutive_hits" => RuntimeEvent.Hit, _ => RuntimeEvent.Shot
            };
            string key = definition.Id + ":" + i + ":" + trigger;
            if (trigger != expected || (_lastEvents.TryGetValue(key, out long last) && triggerId <= last)) continue;
            _lastEvents[key] = triggerId;
            if (conditions.Evaluate(mechanism.Condition) != ConditionActivity.Active) continue;
            if (mechanism.Condition?.Parameters.TryGetValue("count", out double count) == true)
            {
                var recorded = _history.TryGetValue(expected, out var past) ? past.FirstOrDefault(item => item.Serial == triggerId) : null;
                long events = recorded == null ? 0 : expected == RuntimeEvent.Hit ? recorded.Consecutive :
                    mechanism.Condition.Parameters.TryGetValue("window_seconds", out double window) ?
                        past!.LongCount(item => item.Serial <= triggerId && item.Time >= recorded.Time - window) : recorded.Count;
                if (events == 0 || events % (long)count != 0) continue;
            }
            double chance = mechanism.Parameters.GetValueOrDefault("chance_percent", 100);
            if (chance <= 0 || (chance < 100 && random() * 100 >= chance)) continue;
            int rounds = checked((int)mechanism.Parameters["rounds"]);
            if (mechanism.Capability == "ammo_return_to_clip")
            {
                int amount = (int)Math.Min(rounds, Math.Max(0L, (long)endpoint.ClipCapacity - endpoint.Clip));
                if (amount > 0) { endpoint.Clip = checked(endpoint.Clip + amount); added = (int)Math.Min(int.MaxValue, (long)added + amount); }
            }
            else
            {
                double amount = Math.Min(rounds, Math.Max(0, endpoint.ReserveCapacity - endpoint.Reserve));
                if (amount > 0) { endpoint.Reserve += amount; added = (int)Math.Min(int.MaxValue, (long)added + (int)amount); }
            }
        }
        return added;
    }
    public double ArmorMultiplier(double upstream, double time, bool aiming = false, bool crouched = false)
    {
        Refresh();
        if (!double.IsFinite(upstream) || upstream < 0 || upstream >= 1) return upstream;
        var states = Conditions(time, aiming, crouched);
        double remain = 1;
        foreach (var mechanism in _definitions.SelectMany(item => item.Mechanisms).Where(item =>
            item.Capability == "armor_pierce" && states.Evaluate(item.Condition) == ConditionActivity.Active))
            remain *= 1 - mechanism.Parameters["ignored_armor_percent"] / 100;
        return upstream + (1 - upstream) * (1 - remain);
    }
    public long EventSerial => _eventSerial;
    public long LastEventSerial(RuntimeEvent kind) => _latest.GetValueOrDefault(kind);
    public long ShotSerial => _shotSerial;
    public void CancelShot()
    {
        if (_shotHit || !_history.TryGetValue(RuntimeEvent.Shot, out var events) || !events.Any(item => item.Serial == _shotSerial)) return;
        _history[RuntimeEvent.Shot] = new(events.Where(item => item.Serial != _shotSerial));
        _totals[RuntimeEvent.Shot] = Math.Max(0, _totals.GetValueOrDefault(RuntimeEvent.Shot) - 1);
    }
    private void Record(RuntimeEvent kind, double time)
    {
        CheckTime(time); _eventSerial++; _latest[kind] = _eventSerial;
        _totals[kind] = Math.Min(long.MaxValue - 1, _totals.GetValueOrDefault(kind) + 1);
        if (!_history.TryGetValue(kind, out var queue)) _history[kind] = queue = new();
        queue.Enqueue(new(_eventSerial, time, _totals[kind], _consecutive));
        // A bounded runtime history is separate from permanent counters; large timed definitions are not advertised as supported.
        while (queue.Count > 8192) queue.Dequeue();
    }
    private long Count(RuntimeEvent kind, ConditionDefinition condition, double time)
    {
        if (!condition.Parameters.TryGetValue("window_seconds", out double window)) return _totals.GetValueOrDefault(kind);
        return _history.TryGetValue(kind, out var queue) ? queue.LongCount(value => value.Time >= time - window) : 0;
    }
    private void CheckTime(double time)
    {
        if (!double.IsFinite(time) || time < 0) throw new ArgumentException("运行时时钟必须是非负有限数。");
        if (time < _lastTime) ResetTriggers();
        _lastTime = time;
    }
    public void ResetTriggers()
    {
        _history.Clear(); _totals.Clear(); _latest.Clear(); _lastEvents.Clear(); _consecutive = 0; _shotHit = false; _shotWeak = false; _lastTime = 0; _eventSerial = 0; _shotSerial = 0;
    }
}
