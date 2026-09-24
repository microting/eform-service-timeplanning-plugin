// The single-worker lock rule (LockedThroughAsync, IsLocked, WhereOpen) lives
// in the base package's DayLock, shared with eform-angular-timeplanning-plugin
// and with FlexChainRecompute's walk, so every writer agrees on which days are
// locked. What stays here is the multi-site boundary lookup the jobs and the
// interceptor need; its BoundaryRows definition must keep matching DayLock's
// (Reconciled and not soft-deleted).
#nullable enable
namespace ServiceTimePlanningPlugin.Infrastructure.Helpers;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.TimePlanningBase.Infrastructure.Data;
using Microting.TimePlanningBase.Infrastructure.Data.Entities;
using Microting.TimePlanningBase.Infrastructure.Helpers;

/// <summary>
/// The single source of truth for "is this day locked".
///
/// The lock is DERIVED, never stored. A worker's boundary is the latest date
/// they have a Reconciled registration on; every day at or before it is locked.
/// Earlier days are therefore locked WITHOUT being marked Reconciled, and a day
/// below the boundary cannot be unlocked because unlocking it would not move
/// MAX(Date) -- both requirements fall out of the model instead of needing a
/// job to keep flags in sync.
/// </summary>
public static class DayLockHelper
{
    /// <summary>
    /// The latest reconciled date for one worker, or null when they have none.
    /// Soft-deleted rows never hold the boundary.
    /// </summary>
    public static Task<DateTime?> LockedThroughAsync(TimePlanningPnDbContext db, int sdkSitId)
        => DayLock.LockedThroughAsync(db, sdkSitId);

    /// <summary>
    /// Boundaries for many workers in ONE query. Callers that render a grid
    /// resolve this once per request rather than once per day cell.
    /// Every requested site gets an entry; sites with no reconciled day map to null.
    /// </summary>
    public static async Task<Dictionary<int, DateTime?>> LockedThroughForSitesAsync(
        TimePlanningPnDbContext db, IReadOnlyCollection<int> sdkSitIds,
        CancellationToken cancellationToken = default)
    {
        var found = await BoundaryRows(db)
            .Where(x => sdkSitIds.Contains(x.SdkSitId))
            .GroupBy(x => x.SdkSitId)
            .Select(g => new { SdkSitId = g.Key, Max = g.Max(x => x.Date) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var map = found.ToDictionary(x => x.SdkSitId, x => (DateTime?)x.Max);
        foreach (var id in sdkSitIds)
        {
            map.TryAdd(id, null);
        }
        return map;
    }

    /// <summary>
    /// Pure predicate, so callers can resolve the boundary once and test many
    /// dates against it without touching the database again.
    /// </summary>
    public static bool IsLocked(DateTime? lockedThrough, DateTime date)
        => DayLock.IsLocked(lockedThrough, date);

    /// <summary>
    /// <see cref="IsLocked(DateTime?, DateTime)"/> against a boundary map from
    /// <see cref="LockedThroughForSitesAsync"/>. A site missing from the map
    /// has no boundary, so its days are open.
    /// </summary>
    public static bool IsLocked(
        IReadOnlyDictionary<int, DateTime?> lockedThroughBySite, int sdkSitId, DateTime date)
        => IsLocked(lockedThroughBySite.GetValueOrDefault(sdkSitId), date);

    /// <summary>
    /// The rows NOT locked by <paramref name="lockedThrough"/>, as a filter the
    /// database runs. Exactly equivalent to <c>!IsLocked(lockedThrough, x.Date)</c>,
    /// time of day included: date.Date &lt;= lockedThrough.Date holds exactly
    /// when date &lt; lockedThrough.Date + 1 day.
    ///
    /// For bulk writers: a locked row that is never loaded is never tracked,
    /// so no later SaveChanges on the context can flush a change into it.
    /// </summary>
    public static IQueryable<PlanRegistration> WhereOpen(
        this IQueryable<PlanRegistration> query, DateTime? lockedThrough)
        => DayLock.OpenRows(query, lockedThrough);

    /// <summary>
    /// What counts as a boundary row, in one place: Reconciled and not
    /// soft-deleted. Both public queries compose their own site predicate
    /// over this so the two never drift apart.
    /// </summary>
    private static IQueryable<PlanRegistration> BoundaryRows(TimePlanningPnDbContext db)
        => db.PlanRegistrations
            .Where(x => x.Reconciled)
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed);
}
