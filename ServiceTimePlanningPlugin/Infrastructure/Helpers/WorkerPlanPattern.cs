using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.TimePlanningBase.Infrastructure.Data;
using Microting.TimePlanningBase.Infrastructure.Data.Entities;
using Microting.TimePlanningBase.Infrastructure.Helpers;
using Sentry;

namespace ServiceTimePlanningPlugin.Infrastructure.Helpers;

/// <summary>
/// A worker's active PlanPattern with its days, loaded once and checked once.
///
/// Loaded per site rather than per row (the nightly run passes one instance
/// into every UpdatePlanRegistration call for that site). A pattern the
/// resolver rejects (RotationWeeks outside 1..8, AnchorDate not a Monday) is
/// reported here, once, and the worker is then treated as having no pattern,
/// so their rows fall back to the sheet/template behaviour and one corrupt
/// pattern cannot stop other workers.
/// </summary>
public sealed class WorkerPlanPattern
{
    public static readonly WorkerPlanPattern None = new(null);

    private WorkerPlanPattern(PlanPattern pattern)
    {
        Pattern = pattern;
    }

    /// <summary>The active, valid pattern, or null when the worker has none.</summary>
    public PlanPattern Pattern { get; }

    public bool IsActive => Pattern != null;

    /// <summary>
    /// Null when the pattern does not govern <paramref name="date"/>; "" when
    /// it governs with no plan; otherwise the plan text. See PlanPatternResolver.
    /// </summary>
    public string PlanTextFor(DateTime date) => PlanPatternResolver.PlanTextFor(Pattern, Pattern?.Days, date);

    public static async Task<WorkerPlanPattern> LoadAsync(TimePlanningPnDbContext dbContext, AssignedSite assignedSite)
    {
        if (assignedSite == null)
        {
            return None;
        }

        // AsNoTracking: the pattern is only read, and a tracked copy would be
        // flushed by the next SaveChanges on this shared context.
        var pattern = await dbContext.PlanPatterns
            .AsNoTracking()
            .Include(x => x.Days)
            .Where(x => x.AssignedSiteId == assignedSite.Id)
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (pattern == null)
        {
            return None;
        }

        try
        {
            // The resolver validates the pattern before it looks at the date.
            PlanPatternResolver.PlanTextFor(pattern, pattern.Days, pattern.AnchorDate);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Console.WriteLine(
                $"fail: PlanPattern {pattern.Id} for site {assignedSite.SiteId} is invalid and is ignored: {ex.Message}");
            SentrySdk.CaptureException(ex);
            return None;
        }

        return new WorkerPlanPattern(pattern);
    }
}
