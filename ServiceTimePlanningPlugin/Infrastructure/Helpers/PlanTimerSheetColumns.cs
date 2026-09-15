using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Microting.TimePlanningBase.Infrastructure.Data.Entities;

namespace ServiceTimePlanningPlugin.Infrastructure.Helpers;

/// <summary>
/// Maps the PlanTimer sheet's header row to each worker's hours and text
/// columns by header NAME, never by position. The sheet is edited by hand and
/// PushToGoogleSheet appends a missing "- timer" or "- tekst" header on its
/// own, so a fixed two-column stride drifts: one stray column makes every later
/// worker read a neighbouring header as its site name and silently miss the
/// site lookup.
/// </summary>
public static class PlanTimerSheetColumns
{
    /// <summary>Columns before this one hold the date and other non-worker data.</summary>
    private const int FirstWorkerColumn = 3;

    /// <summary>
    /// Hyphen plus the en and em dashes a spreadsheet's autocorrect types. The
    /// hyphen must stay first: SuffixedHeader puts this in a character class.
    /// </summary>
    private const string Dashes = "-–—";

    private static readonly Regex SuffixedHeader = new(
        $@"^(?<name>.*?)\s*[{Dashes}]\s*(?<kind>timer|tekst)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Bare column labels a legacy sheet may carry; never a worker's name.</summary>
    private static readonly HashSet<string> ColumnLabels = ["timer", "tekst"];

    /// <summary>A null column means the sheet has no such column for the worker.</summary>
    public sealed record WorkerColumns(string Key, string Name, int? HoursColumn, int? TextColumn);

    public sealed record Layout(IReadOnlyList<WorkerColumns> Workers, IReadOnlyList<string> Problems);

    public sealed record ResolvedWorker(WorkerColumns Columns, string SiteName, int SiteId, AssignedSite AssignedSite);

    /// <summary>
    /// Unmatched columns are routine (resigned workers, sites that do not import
    /// from the sheet); Problems need someone to fix the sheet or the sites.
    /// </summary>
    public sealed record Resolution(
        IReadOnlyList<ResolvedWorker> Workers,
        IReadOnlyList<string> Unmatched,
        IReadOnlyList<string> Problems);

    /// <summary>
    /// The comparison key for a worker or site name: lower-case with every
    /// whitespace character (non-breaking spaces included) and dash removed,
    /// so "Phien Van Le", "phien  van le" and "Julius -" match their headers.
    /// </summary>
    public static string NormalizeName(string name) =>
        new string((name ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && !Dashes.Contains(c)).ToArray())
            .ToLowerInvariant();

    public static Layout Map(IList<object> headerRow)
    {
        var byKey = new Dictionary<string, WorkerColumns>();
        var unsuffixed = new List<(int Column, string Header)>();
        var problems = new List<string>();

        for (var col = FirstWorkerColumn; col < headerRow.Count; col++)
        {
            var header = CellAt(headerRow, col).Trim();
            if (header.Length == 0)
            {
                continue;
            }

            var match = SuffixedHeader.Match(header);
            if (!match.Success)
            {
                unsuffixed.Add((col, header));
                continue;
            }

            var name = match.Groups["name"].Value.Trim();
            var key = NormalizeName(name);
            if (key.Length == 0)
            {
                problems.Add(NamesNoWorker(col, header));
                continue;
            }

            var isHours = match.Groups["kind"].Value.Equals("timer", StringComparison.OrdinalIgnoreCase);
            var worker = byKey.GetValueOrDefault(key) ?? new WorkerColumns(key, name, null, null);
            if ((isHours ? worker.HoursColumn : worker.TextColumn) is { } used)
            {
                problems.Add(Duplicate(col, header, used));
                continue;
            }

            byKey[key] = isHours ? worker with { HoursColumn = col } : worker with { TextColumn = col };
        }

        // Headers without a suffix are the legacy layout the fixed stride read:
        // the header is the hours column and the next column holds the text,
        // whatever its header says, unless a suffixed header claims it.
        var legacyTextColumns = new HashSet<int>();
        foreach (var (col, header) in unsuffixed)
        {
            var key = NormalizeName(header);
            if (legacyTextColumns.Contains(col) || ColumnLabels.Contains(key))
            {
                continue;
            }

            if (key.Length == 0)
            {
                problems.Add(NamesNoWorker(col, header));
                continue;
            }

            var worker = byKey.GetValueOrDefault(key) ?? new WorkerColumns(key, header, null, null);
            if (worker.HoursColumn is { } used)
            {
                problems.Add(Duplicate(col, header, used));
                continue;
            }

            worker = worker with { HoursColumn = col };
            if (worker.TextColumn == null && !SuffixedHeader.IsMatch(CellAt(headerRow, col + 1).Trim()))
            {
                worker = worker with { TextColumn = col + 1 };
                legacyTextColumns.Add(col + 1);
            }

            byKey[key] = worker;
        }

        return new Layout(byKey.Values.OrderBy(x => x.HoursColumn ?? x.TextColumn).ToList(), problems);
    }

    /// <summary>
    /// Pairs each worker column with the one site that imports from the sheet
    /// under that name. Only sites whose assignment has UseGoogleSheetAsDefault
    /// compete, and an active worker beats a resigned namesake, so neither a
    /// site that opted out nor a former employee makes a column ambiguous.
    /// </summary>
    public static Resolution Resolve(IReadOnlyList<WorkerColumns> workers,
        IEnumerable<(string Name, int SiteId)> sites, IEnumerable<AssignedSite> assignedSites)
    {
        var importing = assignedSites
            .Where(x => x.UseGoogleSheetAsDefault)
            .GroupBy(x => x.SiteId)
            .ToDictionary(x => x.Key, x => x.First());
        var candidatesByKey = sites
            .Where(x => importing.ContainsKey(x.SiteId))
            .ToLookup(x => NormalizeName(x.Name));
        var resolved = new List<ResolvedWorker>();
        var problems = new List<string>();

        // The Phien Van Le symptom from the site's side: an active worker who
        // should import from the sheet but no column names them.
        var workerKeys = workers.Select(x => x.Key).ToHashSet();
        var unmatched = candidatesByKey
            .Where(x => !workerKeys.Contains(x.Key))
            .SelectMany(x => x)
            .Where(x => !importing[x.SiteId].Resigned)
            .Select(x => $"site \"{x.Name}\" imports from the sheet but no column names it")
            .ToList();

        foreach (var columns in workers)
        {
            var candidates = candidatesByKey[columns.Key]
                .Select(x => new ResolvedWorker(columns, x.Name, x.SiteId, importing[x.SiteId]))
                .ToList();
            if (candidates.Count > 1 && candidates.Any(x => !x.AssignedSite.Resigned))
            {
                candidates = candidates.Where(x => !x.AssignedSite.Resigned).ToList();
            }

            if (candidates.Count == 0)
            {
                unmatched.Add($"column \"{columns.Name}\" matches no site importing from the sheet");
                continue;
            }

            if (candidates.Count > 1)
            {
                problems.Add(
                    $"column \"{columns.Name}\" matches {candidates.Count} sites ({string.Join(", ", candidates.Select(x => x.SiteName))}); none is imported");
                continue;
            }

            if (columns.HoursColumn == null)
            {
                problems.Add($"\"{columns.Name}\" has a \"- tekst\" column but no \"- timer\" column; hours are left unchanged");
            }
            else if (columns.TextColumn == null)
            {
                problems.Add($"\"{columns.Name}\" has a \"- timer\" column but no \"- tekst\" column; text is left unchanged");
            }

            resolved.Add(candidates[0]);
        }

        return new Resolution(resolved, unmatched, problems);
    }

    /// <summary>
    /// The cell value, or empty when the column is absent. The Sheets API drops
    /// trailing empty cells, so rows are often shorter than the header row.
    /// </summary>
    public static string CellAt(IList<object> row, int? col) =>
        col is { } c && c < row.Count ? row[c]?.ToString() ?? string.Empty : string.Empty;

    /// <summary>
    /// A blank cell is 0 hours; null means the cell is not a finite number.
    /// "NaN" and "Infinity" parse as doubles but cannot be stored.
    /// </summary>
    public static double? ParseHours(string cell)
    {
        var text = cell.Trim();
        if (text.Length == 0)
        {
            return 0;
        }

        if (!double.TryParse(text.Replace(",", "."), NumberStyles.AllowDecimalPoint,
                NumberFormatInfo.InvariantInfo, out var hours) || !double.IsFinite(hours))
        {
            return null;
        }

        return hours;
    }

    /// <summary>
    /// Applies one sheet cell pair to an existing registration. A null value
    /// means the sheet has no usable value, so that field is left untouched.
    /// Hours only apply to a day without plan text and never override a plan
    /// an admin changed.
    /// </summary>
    public static void ApplyTo(PlanRegistration planRegistration, string planText, double? planHours,
        string logContext)
    {
        if (planText != null)
        {
            if (planRegistration.PlanText != planText)
            {
                Console.WriteLine(
                    $"warn: PlanText for {logContext} has changed from {planRegistration.PlanText} to {planText}");
            }

            planRegistration.PlanText = planText;
        }

        if (string.IsNullOrEmpty(planRegistration.PlanText) && planHours is { } hours)
        {
            if (planRegistration.PlanHours != hours)
            {
                Console.WriteLine(
                    $"warn: PlanHours for {logContext} has changed from {planRegistration.PlanHours} to {hours}");
            }

            if (!planRegistration.PlanChangedByAdmin)
            {
                planRegistration.PlanHours = hours;
            }
        }
    }

    /// <summary>The sheet's column letter for a 0-based index: 0 is A, 26 is AA.</summary>
    public static string ColumnLetter(int col)
    {
        var letters = string.Empty;
        for (var n = col + 1; n > 0; n = (n - 1) / 26)
        {
            letters = (char)('A' + (n - 1) % 26) + letters;
        }

        return letters;
    }

    private static string NamesNoWorker(int col, string header) =>
        $"column {ColumnLetter(col)} header \"{header}\" names no worker";

    private static string Duplicate(int col, string header, int used) =>
        $"column {ColumnLetter(col)} header \"{header}\" duplicates column {ColumnLetter(used)}, which is used instead";
}
