/*
The MIT License (MIT)

Copyright (c) 2007 - 2026 Microting A/S

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.TimePlanningBase.Infrastructure.Data.Entities;
using NUnit.Framework;
using ServiceTimePlanningPlugin.Scheduler.Jobs;

namespace ServiceTimePlanningPlugin.Integration.Test;

/// <summary>
/// Covers SearchListJob's nightly recalculation through
/// RecalculateRecentRegistrations(), the entry point without the hourly gate.
/// The Google Sheet pull is not covered: it needs the Sheets API.
/// </summary>
[TestFixture]
public class SearchListJobTests : TestBaseSetup
{
    private async Task SeedRow(int siteId, DateTime date, bool reconciled = false)
    {
        await new PlanRegistration
        {
            SdkSitId = siteId,
            Date = date,
            PlanHours = 7,
            Reconciled = reconciled,
            ReconciledAt = reconciled ? date.AddDays(1) : null,
            PlanText = "",
            CommentOffice = "",
            CommentOfficeAll = "",
            WorkflowState = Constants.WorkflowStates.Created,
            CreatedByUserId = 1,
            UpdatedByUserId = 1
        }.Create(TimePlanningPnDbContext);
    }

    private async Task<PlanRegistration> ReloadRow(int siteId, DateTime date)
        => await TimePlanningPnDbContext.PlanRegistrations
            .AsNoTracking()
            .SingleAsync(x => x.SdkSitId == siteId && x.Date == date);

    [Test]
    public async Task NightlyRecalculation_SkipsReconciledDays_AndStillUpdatesTheOpenOnes()
    {
        const int siteId = 960;
        var today = DateTime.Today;

        // Not sheet-driven, so the recalculation takes each row's plan hours
        // from these weekday defaults: 8h every day, where every seeded row
        // says 7h. It therefore rewrites every row it reaches.
        await new AssignedSite
        {
            SiteId = siteId,
            UseGoogleSheetAsDefault = false,
            UseOnlyPlanHours = true,
            MondayPlanHours = 480,
            TuesdayPlanHours = 480,
            WednesdayPlanHours = 480,
            ThursdayPlanHours = 480,
            FridayPlanHours = 480,
            SaturdayPlanHours = 480,
            SundayPlanHours = 480,
            WorkflowState = Constants.WorkflowStates.Created,
            CreatedByUserId = 1,
            UpdatedByUserId = 1
        }.Create(TimePlanningPnDbContext);

        // The locked rows come first in the walk (it is ascending by date).
        // Were they loaded, the first one's rejected save would stay tracked
        // on the site's context and end that site's run, so the open rows
        // below would never be written either.
        var lockedDates = new[] { today.AddDays(-6), today.AddDays(-5) };
        await SeedRow(siteId, lockedDates[0]);
        await SeedRow(siteId, lockedDates[1], reconciled: true);
        await SeedRow(siteId, today.AddDays(-3));
        await SeedRow(siteId, today.AddDays(-2));

        var lockedBefore = new[]
        {
            await ReloadRow(siteId, lockedDates[0]),
            await ReloadRow(siteId, lockedDates[1]),
        };

        await new SearchListJob(DbContextHelper, null!).RecalculateRecentRegistrations();

        var lockedAfter = new[]
        {
            await ReloadRow(siteId, lockedDates[0]),
            await ReloadRow(siteId, lockedDates[1]),
        };
        var dMinus3 = await ReloadRow(siteId, today.AddDays(-3));
        var dMinus2 = await ReloadRow(siteId, today.AddDays(-2));

        Assert.Multiple(() =>
        {
            for (var i = 0; i < lockedDates.Length; i++)
            {
                Assert.That(lockedAfter[i].Version, Is.EqualTo(lockedBefore[i].Version),
                    $"the locked row on {lockedDates[i]:yyyy-MM-dd} must not be re-saved");
                Assert.That(lockedAfter[i].PlanHours, Is.EqualTo(7));
            }

            Assert.That(dMinus3.PlanHours, Is.EqualTo(8),
                "days after the boundary must still be recalculated");
            Assert.That(dMinus2.PlanHours, Is.EqualTo(8));
        });
    }
}
