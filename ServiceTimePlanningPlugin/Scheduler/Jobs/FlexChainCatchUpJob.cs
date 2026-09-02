using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.TimePlanningBase.Infrastructure.Data;
using Microting.TimePlanningBase.Infrastructure.Data.Entities;
using Microting.TimePlanningBase.Infrastructure.Helpers;
using Sentry;
using ServiceTimePlanningPlugin.Infrastructure.Helpers;

namespace ServiceTimePlanningPlugin.Scheduler.Jobs;

/// <summary>
/// The daily flex-chain catch-up pass.
///
/// PlanRegistration rows are pre-created up to 180 days ahead holding zero
/// flex, which is correct at write time -- future rows carry no flex by
/// design. Nothing ever revisits a row once its date arrives, so it sits at
/// zero forever, and any later window that seeds from it inherits the hole.
/// The result is internally self-consistent, so nothing detects it.
///
/// This job walks each site forward from <see cref="AssignedSite.FlexChainComputedThrough"/>
/// (or its earliest registration, when the cursor has never been set) through
/// today, filling in the real chain via the shared <see cref="FlexChain"/>
/// helper, then advances the cursor. Runs once per day (gated to a single
/// UTC hour that <see cref="SearchListJob"/> does not use) via the same
/// 60-minute service timer.
///
/// This job writes EVERY site EVERY night. Shipping the code and running it
/// are deliberately two separate decisions: it is gated OFF by default via
/// the <c>TimePlanningBaseSettings:FlexChainCatchUpEnabled</c> plugin
/// configuration value (the same "TimePlanningBaseSettings:*" PluginConfigurationValues
/// idiom this service already uses for MaxParallelism/NumberOfWorkers in
/// Core.cs, GoogleSheetId in SearchListJob, and EformId/FolderId/InfoeFormId
/// in EFormCompletedHandler -- a missing/absent value has always meant "this
/// feature is off" in this codebase, never "on"). An operator must set it to
/// "1" to turn the job on after an observation period.
/// </summary>
public class FlexChainCatchUpJob(DbContextHelper dbContextHelper) : IJob
{
    private const string EnabledSettingName = "TimePlanningBaseSettings:FlexChainCatchUpEnabled";

    public async Task Execute()
    {
        // The enable gate comes first, before the hour check and before any
        // per-site work, so a disabled job costs at most the one lightweight
        // config lookup below -- never the AssignedSites listing or any row
        // work. Default is OFF: a missing value, an empty value, or anything
        // other than "1" all mean disabled.
        var dbContext = dbContextHelper.GetDbContext();
        var enabledSetting = await dbContext.PluginConfigurationValues
            .FirstOrDefaultAsync(x => x.Name == EnabledSettingName);

        if (enabledSetting?.Value != "1")
        {
            Console.WriteLine(
                $"info: FlexChainCatchUpJob is disabled ({EnabledSettingName} is not set to \"1\") -- skipping.");
            return;
        }

        if (DateTime.UtcNow.Hour != 3)
        {
            return;
        }

        await RunCatchUp();
    }

    /// <summary>
    /// The actual per-site catch-up walk, without the enable gate or the
    /// hourly schedule gate -- <see cref="Execute"/> is what the service
    /// timer calls; this is the entry point integration tests call directly
    /// so a test run does not depend on the wall-clock hour or on seeding a
    /// PluginConfigurationValues row just to turn the job on.
    /// </summary>
    public async Task RunCatchUp()
    {
        var dbContext = dbContextHelper.GetDbContext();

        var today = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, 0, 0, 0);

        // Only the site ids are read from this context. Each site below gets
        // its OWN fresh DbContext (matching SearchListJob's case-18 job)
        // rather than sharing one context across the whole estate -- a
        // long-lived context accumulates every tracked row from every site
        // for the life of the run, which both grows memory unboundedly across
        // ~214 tenant databases and risks one site's tracked entities
        // interfering with another's change-tracking.
        var siteIds = await dbContext.AssignedSites
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .Select(x => x.SiteId)
            .ToListAsync();

        foreach (var siteId in siteIds)
        {
            try
            {
                var siteDbContext = dbContextHelper.GetDbContext();

                var assignedSite = await siteDbContext.AssignedSites
                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                    .FirstOrDefaultAsync(x => x.SiteId == siteId);

                if (assignedSite == null)
                {
                    // Removed or otherwise gone between the listing query above
                    // and now -- nothing to catch up.
                    continue;
                }

                await CatchUpSite(siteDbContext, assignedSite, today);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"fail: FlexChainCatchUpJob failed for AssignedSite.SiteId: {siteId}. {ex.Message}");
                Console.WriteLine($"fail: {ex.StackTrace}");
                SentrySdk.CaptureException(ex);
            }
        }
    }

    private static async Task CatchUpSite(TimePlanningPnDbContext dbContext, AssignedSite assignedSite, DateTime today)
    {
        DateTime walkStart;

        if (assignedSite.FlexChainComputedThrough.HasValue)
        {
            walkStart = assignedSite.FlexChainComputedThrough.Value.Date.AddDays(1);
        }
        else
        {
            var earliestDate = await dbContext.PlanRegistrations
                .AsNoTracking()
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Where(x => x.SdkSitId == assignedSite.SiteId)
                .OrderBy(x => x.Date)
                .Select(x => (DateTime?)x.Date)
                .FirstOrDefaultAsync();

            if (earliestDate == null)
            {
                // No registrations at all for this site -- nothing to catch up,
                // and the cursor stays null so a later registration is still walked.
                return;
            }

            walkStart = earliestDate.Value;
        }

        if (walkStart > today)
        {
            // Already caught up through today (or beyond, which should not
            // happen but is harmless to no-op on).
            return;
        }

        // The single row immediately before the walk start, to seed the chain
        // from. Carried in memory from here on -- never re-read per row.
        var preTimePlanning = await dbContext.PlanRegistrations
            .AsNoTracking()
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .Where(x => x.SdkSitId == assignedSite.SiteId)
            .Where(x => x.Date < walkStart)
            .OrderByDescending(x => x.Date)
            .FirstOrDefaultAsync();

        // ASCENDING by Date, always -- the chain seeds each row from the one
        // before it. A descending walk seeds every row from its predecessor's
        // pre-update value and never converges. Never touches a row dated
        // after today -- future rows carry no flex by design.
        var rows = await dbContext.PlanRegistrations
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .Where(x => x.SdkSitId == assignedSite.SiteId)
            .Where(x => x.Date >= walkStart && x.Date <= today)
            .OrderBy(x => x.Date)
            .ToListAsync();

        // Built ONCE per site, before the row loop -- never per row, since it
        // issues a query. See OneMinuteModeTimeline class docs.
        var timeline = await OneMinuteModeTimeline.BuildAsync(dbContext, assignedSite);

        foreach (var row in rows)
        {
            // Full precedence (write-time marker -> effective date -> audit
            // trail) via the timeline -- never branch on
            // AssignedSite.UseOneMinuteIntervals directly, which would
            // retroactively recalculate rows under the site's CURRENT flag.
            var rowIsOneMinute = timeline.WasOneMinuteForRow(row);

            if (rowIsOneMinute)
            {
                FlexChain.ApplyNettoFlexChainSecondPrecision(
                    row, preTimePlanning, timeline.WasOneMinuteFor(preTimePlanning));
            }
            else
            {
                FlexChain.ApplyNettoFlexChainDecimal(row, preTimePlanning);
            }

            await row.Update(dbContext).ConfigureAwait(false);

            // Seed the NEXT row from THIS row's just-written values, carried
            // in memory -- never re-read from the database.
            preTimePlanning = row;
        }

        // Advance the cursor only after every row in the window is saved, so
        // a crash mid-walk leaves the cursor behind rather than ahead. A
        // cursor ahead of reality silently declares holes computed and they
        // are never revisited.
        assignedSite.FlexChainComputedThrough = today;
        await assignedSite.Update(dbContext).ConfigureAwait(false);
    }
}
