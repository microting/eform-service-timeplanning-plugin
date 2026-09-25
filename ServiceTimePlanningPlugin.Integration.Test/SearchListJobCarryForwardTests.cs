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
/// The nightly recalculation changes one day's plan hours; the days after it
/// must carry the new balance (R4), a pre-created future day included.
/// Called through RecalculateRecentRegistrations(), the entry point without
/// the hourly gate, as SearchListJobTests does.
/// </summary>
[TestFixture]
public class SearchListJobCarryForwardTests : TestBaseSetup
{
    private async Task SeedRow(int siteId, DateTime date, double planHours, double nettoHours,
        double flex, double sumFlexStart, double sumFlexEnd)
    {
        await new PlanRegistration
        {
            SdkSitId = siteId,
            Date = date,
            PlanHours = planHours,
            NettoHours = nettoHours,
            Flex = flex,
            SumFlexStart = sumFlexStart,
            SumFlexEnd = sumFlexEnd,
            PlanText = "",
            CommentOffice = "",
            CommentOfficeAll = "",
            WorkflowState = Constants.WorkflowStates.Created,
            CreatedByUserId = 1,
            UpdatedByUserId = 1
        }.Create(TimePlanningPnDbContext);
    }

    private async Task<PlanRegistration> Reload(int siteId, DateTime date)
        => await TimePlanningPnDbContext.PlanRegistrations.AsNoTracking()
            .SingleAsync(x => x.SdkSitId == siteId && x.Date == date);

    [Test]
    public async Task NightlyRecalculation_CarriesAChangedDayForward_ToTheLastRow()
    {
        const int siteId = 961;
        var today = DateTime.Today;
        await new AssignedSite
        {
            SiteId = siteId,
            UseGoogleSheetAsDefault = false,
            UseOnlyPlanHours = true,
            MondayPlanHours = 480, TuesdayPlanHours = 480, WednesdayPlanHours = 480,
            ThursdayPlanHours = 480, FridayPlanHours = 480, SaturdayPlanHours = 480,
            SundayPlanHours = 480,
            WorkflowState = Constants.WorkflowStates.Created,
            CreatedByUserId = 1,
            UpdatedByUserId = 1
        }.Create(TimePlanningPnDbContext);

        // A consistent chain built on d-3 having 7 plan hours. Overnight d-3
        // becomes 8 h (weekday default), so its balance drops by 1 h. d-2 and
        // the future day already have 8 h, so the recalculation leaves them alone.
        await SeedRow(siteId, today.AddDays(-3), 7, 8, 1, 0, 1);
        await SeedRow(siteId, today.AddDays(-2), 8, 8, 0, 1, 1);
        await SeedRow(siteId, today.AddDays(3), 8, 0, -8, 1, -7);

        await new SearchListJob(DbContextHelper, null!).RecalculateRecentRegistrations();

        var dMinus3 = await Reload(siteId, today.AddDays(-3));
        var dMinus2 = await Reload(siteId, today.AddDays(-2));
        var future = await Reload(siteId, today.AddDays(3));
        Assert.Multiple(() =>
        {
            Assert.That(dMinus3.PlanHours, Is.EqualTo(8));
            Assert.That(dMinus3.SumFlexEnd, Is.EqualTo(0).Within(1e-9));
            Assert.That(dMinus2.SumFlexStart, Is.EqualTo(dMinus3.SumFlexEnd).Within(1e-9));
            Assert.That(dMinus2.SumFlexEnd, Is.EqualTo(0).Within(1e-9));
            Assert.That(dMinus2.NettoHours, Is.EqualTo(8).Within(1e-9), "the walk never recomputes hours");
            Assert.That(future.SumFlexStart, Is.EqualTo(dMinus2.SumFlexEnd).Within(1e-9));
            Assert.That(future.SumFlexEnd, Is.EqualTo(-8).Within(1e-9));
        });
    }
}
