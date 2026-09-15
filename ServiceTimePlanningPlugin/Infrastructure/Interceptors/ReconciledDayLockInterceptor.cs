// NOTE: this is a deliberate copy of the same file in
// eform-angular-timeplanning-plugin
// (eFormAPI/Plugins/TimePlanning.Pn/TimePlanning.Pn/Infrastructure/Interceptors/ReconciledDayLockInterceptor.cs).
// The two repos share only the base NuGet package. If you change the lock logic
// here, change the twin too: a divergence lets background jobs write days the
// web refuses.
//
// Besides this header and the namespace, only comments about each repo's own
// hosting differ (the race note's up-front checks, the stateless note and the
// synchronous SaveChanges path). The logic is identical.
#nullable enable
namespace ServiceTimePlanningPlugin.Infrastructure.Interceptors;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microting.TimePlanningBase.Infrastructure.Data;
using PlanRegistrationEntity = Microting.TimePlanningBase.Infrastructure.Data.Entities.PlanRegistration;

/// <summary>
/// Thrown when a write would touch a day at or before the worker's reconciled
/// boundary. Distinct from a generic InvalidOperationException so the friendly
/// guards in the services can tell "the lock stopped this" from "something else
/// broke" -- and so a stray one is legible in Sentry.
///
/// After this is thrown, the rejected entry stays tracked on the DbContext (EF
/// never rolls back change-tracker state just because SaveChanges threw). A
/// later SaveChanges call on the SAME context will see that entry again and
/// rethrow. Callers that catch this and want to keep using the context must
/// either stop trying to save through it, or explicitly detach/reload the
/// rejected entry first.
/// </summary>
public class DayLockedException(int sdkSitId, DateTime date, DateTime lockedThrough)
    : InvalidOperationException(
        $"Day {date:yyyy-MM-dd} for site {sdkSitId} is locked: reconciled through {lockedThrough:yyyy-MM-dd}.")
{
    public int SdkSitId { get; } = sdkSitId;
    public DateTime Date { get; } = date;
    public DateTime LockedThrough { get; } = lockedThrough;
}

/// <summary>
/// Enforces invariant I3 on every PlanRegistration write path; see the race
/// note below for the one gap.
///
/// RACE NOTE. The boundary query and the write are separate statements with no
/// transaction around them, so a reconcile committed between the two lets one
/// write through onto a day that just became locked. This is race-only and
/// accepted. The jobs' up-front skips read the boundary earlier, but only to
/// avoid loading locked rows: a reconcile committed after a skip decision is
/// still refused here, at save time, as a DayLockedException.
///
/// STATELESS BY DESIGN. One instance is attached to every context
/// DbContextHelper builds, including the concurrent per-site contexts of
/// SearchListJob's Parallel.ForEach, so an interceptor holding state would
/// share it between them.
/// Everything this needs is read from the change tracker on each call. Since
/// there is no state, one shared <see cref="Instance"/> is used everywhere
/// instead of a `new` per registration.
///
/// The boundary is resolved ONCE per SaveChanges for the distinct sites in the
/// change set, not once per row.
/// </summary>
public class ReconciledDayLockInterceptor : SaveChangesInterceptor
{
    public static readonly ReconciledDayLockInterceptor Instance = new();

    /// <summary>
    /// Bookkeeping columns PnBase.Update/Delete touch on every single save
    /// (UpdateInternal bumps Version and UpdatedAt unconditionally, and
    /// callers routinely stamp UpdatedByUserId), regardless of what the
    /// caller actually meant to change. Both permitted-write checks below
    /// need "what did the caller actually change", so both exclude these.
    /// </summary>
    private static readonly HashSet<string> BookkeepingProperties =
    [
        nameof(PlanRegistrationEntity.UpdatedAt),
        nameof(PlanRegistrationEntity.Version),
        nameof(PlanRegistrationEntity.UpdatedByUserId),
    ];

    private static readonly HashSet<string> UnlockProperties =
    [
        nameof(PlanRegistrationEntity.Reconciled),
        nameof(PlanRegistrationEntity.ReconciledAt),
    ];

    /// <summary>
    /// Spec §11.2: Reconciled and TransferredToPayroll are independent in both
    /// directions -- reconciling a period must not affect export eligibility,
    /// and exporting it must not be blocked by the lock reconciling it
    /// created. PayrollExportService.ExportPayroll sets exactly these two
    /// properties, one Update per row, after the export file already exists;
    /// without this exemption exporting any reconciled period throws midway
    /// and leaves some rows flagged and some not. Do not "tighten" this away.
    /// </summary>
    private static readonly HashSet<string> PayrollFlagProperties =
    [
        nameof(PlanRegistrationEntity.TransferredToPayroll),
        nameof(PlanRegistrationEntity.TransferredToPayrollAt),
    ];

    /// <summary>One (SdkSitId, Date) pair a write touches -- see <see cref="SlotsToCheck"/>.</summary>
    private readonly record struct Slot(int SdkSitId, DateTime Date);

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        // Sync-over-async is safe here: the jobs and the Rebus handler run on
        // the thread pool with no SynchronizationContext, and
        // GuardAsync/LockedThroughForSitesAsync use ConfigureAwait(false)
        // throughout, so there is no continuation to deadlock on. Nothing in
        // this repo calls the synchronous SaveChanges today, tests included;
        // the override exists so that a future caller is guarded, not bypassed.
        GuardAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await GuardAsync(eventData.Context, cancellationToken).ConfigureAwait(false);
        return await base.SavingChangesAsync(eventData, result, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task GuardAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is not TimePlanningPnDbContext db)
        {
            return;
        }

        // PnBase.Delete is a SOFT delete: it sets WorkflowState = Removed and
        // routes through UpdateInternal, so a delete reaches here as Modified,
        // never Deleted. The Deleted arm is kept as insurance against a future
        // hard delete; do not "simplify" it away on the grounds that it never
        // fires today.
        //
        // Each entry contributes one or two slots: I3 is keyed on
        // (SdkSitId, Date), and a Modified/Deleted row can change either or
        // both -- moving a row's Date past the boundary, or its SdkSitId to
        // another worker, must not let it escape a lock it started inside.
        var entrySlots = db.ChangeTracker.Entries<PlanRegistrationEntity>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToDictionary(e => e, SlotsToCheck);

        if (entrySlots.Count == 0)
        {
            return;
        }

        var siteIds = entrySlots.Values
            .SelectMany(slots => slots)
            .Select(s => s.SdkSitId)
            .Distinct()
            .ToList();

        // Query `db` itself. An earlier draft opened a second context here;
        // that is a production outage, because ServerVersion.AutoDetect opens a
        // connection and runs a version query, and this guard runs on EVERY
        // save -- including the four Update calls inside the per-day loop of
        // UpdatePlanRegistrationsInPeriod, which runs on every dashboard load.
        // 50 workers x 31 days would mean thousands of extra connections per
        // page view.
        //
        // Querying `db` is safe: LockedThroughForSitesAsync projects into an
        // anonymous type so it tracks nothing and cannot pollute the change
        // tracker; a query never re-enters SaveChanges so there is no
        // recursion; and it reuses the open connection and any ambient
        // transaction, which a separate context could not see.
        var boundaries = await DayLockHelper
            .LockedThroughForSitesAsync(db, siteIds, cancellationToken)
            .ConfigureAwait(false);

        foreach (var (entry, slots) in entrySlots)
        {
            Slot? lockedSlot = null;
            DateTime lockedBoundary = default;

            foreach (var slot in slots)
            {
                if (boundaries.TryGetValue(slot.SdkSitId, out var boundary)
                    && DayLockHelper.IsLocked(boundary, slot.Date))
                {
                    lockedSlot = slot;
                    lockedBoundary = boundary!.Value;
                    break;
                }
            }

            if (lockedSlot is null)
            {
                continue;
            }

            // The two permitted writes inside the locked range: clearing the
            // flag on the boundary day itself (that is what unlocking IS), and
            // a payroll-flag-only write on any locked day (see
            // PayrollFlagProperties for why exporting must stay possible). Both
            // are evaluated against the entry's CURRENT site's boundary --
            // that is the only boundary either exemption is ever about -- and
            // both already fail whenever Date or SdkSitId is among the
            // modified properties (they are not in either exemption's allowed
            // property set), which is exactly what stops a locked ORIGINAL
            // slot from being exempted just because the write is moving the
            // row away from it.
            var currentSiteBoundary = boundaries.TryGetValue(entry.Entity.SdkSitId, out var csb) ? csb : null;
            if ((currentSiteBoundary is not null && IsUnlockOfBoundaryDay(entry, currentSiteBoundary.Value))
                || IsPayrollFlagOnlyWrite(entry))
            {
                continue;
            }

            throw new DayLockedException(lockedSlot.Value.SdkSitId, lockedSlot.Value.Date, lockedBoundary);
        }
    }

    /// <summary>
    /// The slot(s) I3 must check for one entry: always the CURRENT
    /// (SdkSitId, Date), and -- for Modified/Deleted only, since Added has no
    /// "before" -- the ORIGINAL one too, when it differs. Never reads
    /// OriginalValue on an Added entry; EF has no original value to give one.
    /// </summary>
    private static List<Slot> SlotsToCheck(EntityEntry<PlanRegistrationEntity> entry)
    {
        var current = new Slot(entry.Entity.SdkSitId, entry.Entity.Date);

        if (entry.State is not (EntityState.Modified or EntityState.Deleted))
        {
            return [current];
        }

        var original = new Slot(
            entry.Property(x => x.SdkSitId).OriginalValue,
            entry.Property(x => x.Date).OriginalValue);

        return original.Equals(current) ? [current] : [current, original];
    }

    /// <summary>
    /// What the caller actually changed, ignoring the bookkeeping columns
    /// every PnBase.Update/Delete call touches regardless of intent. Both
    /// permitted-write predicates below are just a subset check over this.
    /// </summary>
    private static HashSet<string> NonBookkeepingModifiedProperties(
        EntityEntry<PlanRegistrationEntity> entry)
        => entry.Properties
            .Where(p => p.IsModified)
            .Select(p => p.Metadata.Name)
            .Where(name => !BookkeepingProperties.Contains(name))
            .ToHashSet();

    private static bool IsUnlockOfBoundaryDay(
        EntityEntry<PlanRegistrationEntity> entry, DateTime boundary)
    {
        if (entry.State != EntityState.Modified)
        {
            return false;
        }
        if (entry.Entity.Date.Date != boundary.Date)
        {
            return false;
        }
        // Reconciled must be going true -> false, and nothing else about the
        // row -- besides Reconciled/ReconciledAt themselves -- may be
        // changing in the same save.
        var reconciled = entry.Property(x => x.Reconciled);
        if (!reconciled.IsModified || (bool)reconciled.CurrentValue!)
        {
            return false;
        }
        // Invariant I1: ReconciledAt non-null exactly when Reconciled. A
        // "clear Reconciled but leave ReconciledAt set" write is not a valid
        // unlock -- it would leave the row in a state I1 forbids -- so this
        // choke point refuses it rather than trusting every caller to clear
        // both together.
        if (entry.Property(x => x.ReconciledAt).CurrentValue is not null)
        {
            return false;
        }

        return NonBookkeepingModifiedProperties(entry).IsSubsetOf(UnlockProperties);
    }

    private static bool IsPayrollFlagOnlyWrite(EntityEntry<PlanRegistrationEntity> entry)
    {
        if (entry.State != EntityState.Modified)
        {
            return false;
        }

        var changed = NonBookkeepingModifiedProperties(entry);
        // changed.Count == 0 would mean nothing but bookkeeping moved -- not a
        // payroll write, and Count > 0 here already guarantees at least one of
        // TransferredToPayroll/TransferredToPayrollAt is the modified property
        // once the subset check below passes.
        return changed.Count > 0 && changed.IsSubsetOf(PayrollFlagProperties);
    }
}
