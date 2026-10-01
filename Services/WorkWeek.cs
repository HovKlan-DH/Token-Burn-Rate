using System;
using System.Collections.Generic;
using System.Numerics;

namespace TokenBurnRate.Services;

/// <summary>
/// The days of the week that count as workdays for My Pace, ticked one by one in the
/// context menu's "Workdays in a week" - any combination, so a Tuesday-Saturday week or a
/// Monday/Wednesday/Friday one is as expressible as Monday-Friday, and no day is assumed to
/// be the weekend.
///
/// Replaces a count of "the first N days from Monday", which could only describe weeks that
/// start on Monday and run without a gap. A stored count is still read once and converted -
/// see <see cref="FirstDaysFromMonday"/>.
///
/// Never empty in practice: the menu refuses to untick the last day, and a stored list that
/// names no valid day is treated as absent. The default value of the struct is empty all
/// the same, so nothing may rely on that alone.
/// </summary>
public readonly record struct WorkWeek
{
    private const int AllDaysMask = 0b111_1111;

    /// <summary>One bit per day, indexed by <see cref="DayOfWeek"/> (Sunday = bit 0).</summary>
    private readonly int _mask;

    private WorkWeek(int mask) => _mask = mask & AllDaysMask;

    /// <summary>Every day of the week - what My Pace uses until a selection is made.</summary>
    public static WorkWeek EveryDay => new(AllDaysMask);

    /// <summary>Monday first, the order the menu lists the days in and the state file stores them.</summary>
    public static IReadOnlyList<DayOfWeek> MondayFirst { get; } = new[]
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    };

    public bool Contains(DayOfWeek day) => (_mask & Bit(day)) != 0;

    public int Count => BitOperations.PopCount((uint)_mask);

    public bool IsEmpty => _mask == 0;

    /// <summary>This week with <paramref name="day"/> added or removed.</summary>
    public WorkWeek With(DayOfWeek day, bool isWorkday) =>
        new(isWorkday ? _mask | Bit(day) : _mask & ~Bit(day));

    /// <summary>
    /// The week the old "Workdays in a week" count described: the first
    /// <paramref name="count"/> days counting from Monday, so 5 is Monday-Friday.
    /// </summary>
    public static WorkWeek FirstDaysFromMonday(int count)
    {
        var week = new WorkWeek(0);
        for (var i = 0; i < Math.Clamp(count, 1, 7); i++)
            week = week.With(MondayFirst[i], true);
        return week;
    }

    /// <summary>
    /// Reads the day names the state file stores (see <see cref="ToNames"/>). Each entry must
    /// be one whole English day name, matched without regard to case and surrounding spaces;
    /// anything else is skipped, since the file is hand-editable. Null when no valid day is
    /// left, so the caller falls back to its default rather than to a week with no workdays
    /// in it.
    ///
    /// Compared as plain strings, not through Enum.TryParse: that also accepts numbers and
    /// comma-joined lists and ORs their values together, so "Monday,Friday" became 1|5 =
    /// Friday - a different week from the one typed, with nothing to say so.
    /// </summary>
    public static WorkWeek? FromNames(IEnumerable<string?>? names)
    {
        if (names is null) return null;

        var week = new WorkWeek(0);
        foreach (var name in names)
            foreach (var day in MondayFirst)
                if (string.Equals(name?.Trim(), day.ToString(), StringComparison.OrdinalIgnoreCase))
                    week = week.With(day, true);
        return week.IsEmpty ? null : week;
    }

    /// <summary>The chosen days as English day names, Monday first - the state file's form.</summary>
    public string[] ToNames()
    {
        var names = new List<string>(Count);
        foreach (var day in MondayFirst)
            if (Contains(day)) names.Add(day.ToString());
        return names.ToArray();
    }

    private static int Bit(DayOfWeek day) => 1 << (int)day;
}
