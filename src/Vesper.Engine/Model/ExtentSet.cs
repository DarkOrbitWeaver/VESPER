using System.Globalization;

namespace Vesper.Engine;

/// <summary>Half-open byte range [Start, End).</summary>
public readonly record struct ByteRange(long Start, long End)
{
    public long Length => End - Start;
    public bool IsEmpty => End <= Start;
    public override string ToString() => $"{Start}-{End}";
}

/// <summary>
/// Sorted, non-overlapping, non-adjacent byte ranges. Used for committed (durable) extents
/// and for pending (written but not yet durable) extents.
///
/// Instances that have been published as a job's committed extents are treated as immutable:
/// a checkpoint builds a clone, persists it, and only then swaps the reference.
/// </summary>
public sealed class ExtentSet
{
    private List<ByteRange> _ranges = [];

    public IReadOnlyList<ByteRange> Ranges => _ranges;
    public long TotalBytes { get; private set; }
    public bool IsEmpty => _ranges.Count == 0;
    public long End => _ranges.Count == 0 ? 0 : _ranges[^1].End;

    public void Add(ByteRange range)
    {
        if (range.IsEmpty) return;
        if (range.Start < 0) throw new ArgumentOutOfRangeException(nameof(range), "Negative offset.");

        var merged = new List<ByteRange>(_ranges.Count + 1);
        ByteRange current = range;
        bool placed = false;
        foreach (ByteRange existing in _ranges)
        {
            if (existing.End < current.Start)
            {
                merged.Add(existing);                      // strictly before, not even adjacent
            }
            else if (existing.Start > current.End)
            {
                if (!placed) { merged.Add(current); placed = true; }
                merged.Add(existing);                      // strictly after
            }
            else
            {
                // Overlapping or touching: absorb into the range being inserted.
                current = new ByteRange(Math.Min(existing.Start, current.Start), Math.Max(existing.End, current.End));
            }
        }
        if (!placed) merged.Add(current);
        Replace(merged);
    }

    /// <summary>Subtracts a range. Used when a stored piece fails verification.</summary>
    public void Remove(ByteRange range)
    {
        if (range.IsEmpty) return;
        var result = new List<ByteRange>(_ranges.Count + 1);
        foreach (ByteRange existing in _ranges)
        {
            if (existing.End <= range.Start || existing.Start >= range.End) { result.Add(existing); continue; }
            if (existing.Start < range.Start) result.Add(new ByteRange(existing.Start, range.Start));
            if (existing.End > range.End) result.Add(new ByteRange(range.End, existing.End));
        }
        Replace(result);
    }

    /// <summary>True when [start, end) lies entirely inside the set.</summary>
    public bool Covers(long start, long end)
    {
        if (end <= start) return true;
        foreach (ByteRange r in _ranges)
            if (r.Start <= start && r.End >= end) return true;
        return false;
    }

    /// <summary>The parts of [0, total) that are not covered.</summary>
    public IEnumerable<ByteRange> Gaps(long total)
    {
        long cursor = 0;
        foreach (ByteRange r in _ranges)
        {
            if (r.Start >= total) break;
            if (r.Start > cursor) yield return new ByteRange(cursor, r.Start);
            cursor = Math.Max(cursor, r.End);
        }
        if (cursor < total) yield return new ByteRange(cursor, total);
    }

    /// <summary>Length of the unbroken run starting at offset zero.</summary>
    public long ContiguousFromZero() => _ranges.Count > 0 && _ranges[0].Start == 0 ? _ranges[0].End : 0;

    /// <summary>Drops everything at or beyond <paramref name="length"/>.</summary>
    public void ClampTo(long length) => Remove(new ByteRange(length, long.MaxValue));

    public ExtentSet Clone()
    {
        var copy = new ExtentSet();
        copy.Replace([.. _ranges]);
        return copy;
    }

    public string Serialize() => string.Join(',', _ranges.Select(r => $"{r.Start}-{r.End}"));

    public static bool TryParse(string? text, out ExtentSet set)
    {
        set = new ExtentSet();
        if (string.IsNullOrWhiteSpace(text)) return true;

        var ranges = new List<ByteRange>();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dash = part.IndexOf('-');
            if (dash <= 0
                || !long.TryParse(part.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out long start)
                || !long.TryParse(part.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out long end)
                || end <= start)
            {
                return false;
            }
            ranges.Add(new ByteRange(start, end));
        }

        ranges.Sort(static (left, right) =>
        {
            int byStart = left.Start.CompareTo(right.Start);
            return byStart != 0 ? byStart : left.End.CompareTo(right.End);
        });

        var normalized = new List<ByteRange>(ranges.Count);
        foreach (ByteRange range in ranges)
        {
            if (normalized.Count == 0 || range.Start > normalized[^1].End)
            {
                normalized.Add(range);
                continue;
            }

            ByteRange previous = normalized[^1];
            normalized[^1] = new ByteRange(previous.Start, Math.Max(previous.End, range.End));
        }

        long total = 0;
        try
        {
            checked
            {
                foreach (ByteRange range in normalized) total += range.Length;
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        set._ranges = normalized;
        set.TotalBytes = total;
        return true;
    }

    private void Replace(List<ByteRange> ranges)
    {
        _ranges = ranges;
        long total = 0;
        foreach (ByteRange r in ranges) total += r.Length;
        TotalBytes = total;
    }
}
