namespace ConstructDpr.Shared.Scheduling;

/// <summary>
/// Working-day calendar. Day index 0 is the first working day on or after the origin;
/// non-working dates snap forward to the next working day.
/// </summary>
public sealed class WorkCalendar
{
    private readonly HashSet<DayOfWeek> _workDays;
    private readonly HashSet<DateOnly> _holidays;
    private readonly DateOnly _origin;
    private readonly List<DateOnly> _forward = new();
    private readonly List<DateOnly> _backward = new();

    public WorkCalendar(IEnumerable<DayOfWeek> workDays, IEnumerable<DateOnly> holidays, DateOnly origin)
    {
        _workDays = workDays.ToHashSet();
        if (_workDays.Count == 0) throw new ArgumentException("A calendar needs at least one working day.");
        _holidays = holidays.ToHashSet();
        _origin = NextWorkday(origin);
    }

    public static IEnumerable<DayOfWeek> ParseWorkDays(string csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) && n is >= 0 and <= 6 ? (DayOfWeek?)n : null)
            .Where(d => d is not null).Select(d => d!.Value).Distinct();

    public bool IsWorkday(DateOnly d) => _workDays.Contains(d.DayOfWeek) && !_holidays.Contains(d);

    public DateOnly NextWorkday(DateOnly d)
    {
        for (var i = 0; i < 3660 && !IsWorkday(d); i++) d = d.AddDays(1);
        return d;
    }

    public DateOnly DateAt(int index)
    {
        if (index >= 0)
        {
            if (_forward.Count == 0) _forward.Add(_origin);
            while (_forward.Count <= index)
            {
                var d = _forward[^1].AddDays(1);
                while (!IsWorkday(d)) d = d.AddDays(1);
                _forward.Add(d);
            }
            return _forward[index];
        }
        var k = -index - 1;
        while (_backward.Count <= k)
        {
            var d = (_backward.Count > 0 ? _backward[^1] : _origin).AddDays(-1);
            while (!IsWorkday(d)) d = d.AddDays(-1);
            _backward.Add(d);
        }
        return _backward[k];
    }

    public int IndexOf(DateOnly date)
    {
        var target = NextWorkday(date);
        if (target >= _origin)
        {
            var i = 0;
            while (DateAt(i) < target) i++;
            return i;
        }
        var j = -1;
        while (DateAt(j) > target) j--;
        return DateAt(j) == target ? j : j + 1;
    }
}
