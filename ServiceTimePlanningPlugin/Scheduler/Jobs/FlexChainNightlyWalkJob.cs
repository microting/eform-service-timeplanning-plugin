using System;
using System.Globalization;
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
/// The nightly flex-chain walk.
///
/// Once a night, for every active (not removed, not resigned) worker, carries
/// the stored flex balance forward from <c>today - N days</c> through the
/// worker's LAST row -- including rows pre-created for future dates -- via the
/// base package's shared <see cref="FlexChainRecompute.RunForwardAsync"/>. Every
/// period view then starts from a correct stored balance no matter which write
/// path changed a day in the last N days.
///
/// The walk is balance-only and writes only rows whose chain values actually
/// change; reconciled (locked) days are never written, and a boundary inside
/// the window makes the walk start the day after it, seeded from its stored
/// balance. A break older than the window is left as stored: the walk seeds
/// from the stored row just before the window.
///
/// N is read from <c>TimePlanningBaseSettings:FlexChainNightlyWalkDays</c>;
/// absent or unparsable means <see cref="DefaultLookBackDays"/>, and zero or
/// negative disables the job. Runs once per day, gated to UTC hour 3 on the
/// same 60-minute service timer as <see cref="FlexChainCatchUpJob"/>.
/// </summary>
public class FlexChainNightlyWalkJob(DbContextHelper dbContextHelper) : IJob
{
    private const string LookBackDaysSettingName = "TimePlanningBaseSettings:FlexChainNightlyWalkDays";
    private const int DefaultLookBackDays = 7;

    // Ten years. Guards DateTime.AddDays against a nonsense setting: an
    // exception here would escape the timer's async-void callback.
    private const int MaxLookBackDays = 3650;

    public Task Execute() => ExecuteAt(DateTime.UtcNow);

    /// <summary>
    /// <see cref="Execute"/> at a given UTC instant: the hour gate, then the
    /// look-back setting, then the walk. Tests pass the instant so they do not
    /// depend on the wall-clock hour.
    /// </summary>
    public async Task ExecuteAt(DateTime utcNow)
    {
        if (utcNow.Hour != 3)
        {
            return;
        }

        // Nothing may escape: Core's timer Callback is async void, so an
        // exception here would take the whole service down.
        try
        {
            var lookBackDays = await ReadLookBackDays();
            if (lookBackDays <= 0)
            {
                Console.WriteLine(
                    $"info: FlexChainNightlyWalkJob is disabled ({LookBackDaysSettingName} is {lookBackDays}) -- skipping.");
                return;
            }

            await RunWalkFrom(utcNow.Date.AddDays(-lookBackDays));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"fail: FlexChainNightlyWalkJob failed. {ex.Message}");
            Console.WriteLine($"fail: {ex.StackTrace}");
            SentrySdk.CaptureException(ex);
        }
    }

    /// <summary>
    /// The walk without the hour gate or the setting, from
    /// <c>DateTime.UtcNow.Date - lookBackDays</c>. Returns the total number of
    /// rows written across all workers.
    /// </summary>
    public Task<int> RunWalk(int lookBackDays)
        => RunWalkFrom(DateTime.UtcNow.Date.AddDays(-lookBackDays));

    private async Task<int> ReadLookBackDays()
    {
        await using var dbContext = dbContextHelper.GetDbContext();
        var setting = await dbContext.PluginConfigurationValues
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name == LookBackDaysSettingName);

        if (setting == null
            || !int.TryParse(setting.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
        {
            return DefaultLookBackDays;
        }

        return Math.Min(days, MaxLookBackDays);
    }

    private static IQueryable<AssignedSite> ActiveAssignedSites(TimePlanningPnDbContext db)
        => db.AssignedSites
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .Where(x => !x.Resigned);

    private async Task<int> RunWalkFrom(DateTime from)
    {
        int[] siteIds;
        await using (var dbContext = dbContextHelper.GetDbContext())
        {
            siteIds = await ActiveAssignedSites(dbContext)
                .Select(x => x.SiteId)
                .Distinct()
                .ToArrayAsync();
        }

        var rowsWritten = 0;
        var failures = 0;

        // Sequential on purpose: the nightly volume is small, and one worker at
        // a time keeps this job from contending with the other jobs on the timer.
        foreach (var siteId in siteIds)
        {
            try
            {
                // A fresh context per worker, so no worker's tracked rows leak
                // into the next worker's walk.
                await using var siteDbContext = dbContextHelper.GetDbContext();

                var assignedSite = await ActiveAssignedSites(siteDbContext)
                    .AsNoTracking()
                    .OrderBy(x => x.Id)
                    .FirstOrDefaultAsync(x => x.SiteId == siteId);

                if (assignedSite == null)
                {
                    // Removed or resigned between the listing above and now.
                    continue;
                }

                rowsWritten += await FlexChainRecompute.RunForwardAsync(
                    siteDbContext, assignedSite, assignedSite.SiteId, from);
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine(
                    $"fail: FlexChainNightlyWalkJob failed for AssignedSite.SiteId: {siteId}. {ex.Message}");
                Console.WriteLine($"fail: {ex.StackTrace}");
                SentrySdk.CaptureException(ex);
            }
        }

        Console.WriteLine(
            $"info: FlexChainNightlyWalkJob considered {siteIds.Length} workers from {from:yyyy-MM-dd}: {rowsWritten} rows written, {failures} failures.");

        return rowsWritten;
    }
}
