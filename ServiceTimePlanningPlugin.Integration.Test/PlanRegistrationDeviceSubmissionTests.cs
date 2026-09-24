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
using ServiceTimePlanningPlugin.Infrastructure.Helpers;

namespace ServiceTimePlanningPlugin.Integration.Test;

/// <summary>
/// Covers what the device eForm handler does once it has the submitted row:
/// the day's hours, its flex, and carrying the balance to later rows.
/// EFormCompletedHandler itself needs the eForm SDK (cases, fields, field
/// values), so these tests call PlanRegistrationDeviceSubmission.ApplyAsync,
/// which is exactly the part of the handler that touches the plan.
///
/// Each test submits through its OWN context, loading the row by date as the
/// handler does, and reads results back untracked from the database.
/// </summary>
[TestFixture]
public class PlanRegistrationDeviceSubmissionTests : TestBaseSetup
{
    private static int _nextSiteId = 300000;

    private static int NextSiteId() => _nextSiteId++;

    private async Task SeedAssignedSite(int siteId, bool useOneMinute)
    {
        await new AssignedSite
        {
            SiteId = siteId,
            UseOneMinuteIntervals = useOneMinute,
            UseOneMinuteIntervalsFrom = useOneMinute ? DateTime.Today.AddYears(-1) : null,
            WorkflowState = Constants.WorkflowStates.Created,
            CreatedByUserId = 1,
            UpdatedByUserId = 1
        }.Create(TimePlanningPnDbContext);
    }

    private async Task SeedRow(int siteId, DateTime date, Action<PlanRegistration> configure)
    {
        var pr = new PlanRegistration
        {
            SdkSitId = siteId,
            Date = date,
            PlanText = "",
            CommentOffice = "",
            CommentOfficeAll = "",
            WorkflowState = Constants.WorkflowStates.Created,
            CreatedByUserId = 1,
            UpdatedByUserId = 1
        };
        configure(pr);
        await pr.Create(TimePlanningPnDbContext);
    }

    private async Task Submit(int siteId, DateTime date)
    {
        await using var db = DbContextHelper.GetDbContext();
        var assignedSite = await db.AssignedSites.FirstOrDefaultAsync(x => x.SiteId == siteId);
        var row = await db.PlanRegistrations.FirstAsync(x => x.SdkSitId == siteId && x.Date == date);
        await PlanRegistrationDeviceSubmission.ApplyAsync(db, assignedSite, row);
    }

    private async Task<PlanRegistration> Reload(int siteId, DateTime date)
        => await TimePlanningPnDbContext.PlanRegistrations
            .AsNoTracking()
            .SingleAsync(x => x.SdkSitId == siteId && x.Date == date);

    /// <summary>A closed five-minute predecessor with the given closing balance.</summary>
    private Task SeedFiveMinuteAnchor(int siteId, DateTime date, double sumFlexEnd)
        => SeedRow(siteId, date, pr =>
        {
            pr.PlanHours = 8;
            pr.NettoHours = 8;
            pr.Flex = 0;
            pr.SumFlexStart = sumFlexEnd;
            pr.SumFlexEnd = sumFlexEnd;
        });

    // ------------------------------------------------------------------
    // B1: the five-minute netto of the submitted day
    // ------------------------------------------------------------------

    /// <summary>
    /// Two closed shifts: 08:00-16:00 with a 30-minute break (7.5 h) and
    /// 17:00-20:00 with no break (3 h). The old inline formula and
    /// FlexChain.ComputeNettoMinutesFlagOff agree on this row. It pins that
    /// a normal day does not move when the formula changes.
    /// </summary>
    [Test]
    public async Task ClosedShifts_FiveMinute_NettoIsTheIdSpanMinusBreaks()
    {
        var siteId = NextSiteId();
        var today = DateTime.Today;
        await SeedAssignedSite(siteId, useOneMinute: false);
        await SeedFiveMinuteAnchor(siteId, today.AddDays(-3), sumFlexEnd: 2.0);
        await SeedRow(siteId, today.AddDays(-2), pr =>
        {
            pr.PlanHours = 8;
            pr.Start1Id = 97;  // 08:00
            pr.Stop1Id = 193;  // 16:00
            pr.Pause1Id = 7;   // 30 min
            pr.Start2Id = 205; // 17:00
            pr.Stop2Id = 241;  // 20:00
            pr.Pause2Id = 1;   // 0 min
        });

        await Submit(siteId, today.AddDays(-2));

        var row = await Reload(siteId, today.AddDays(-2));
        Assert.Multiple(() =>
        {
            Assert.That(row.NettoHours, Is.EqualTo(10.5).Within(1e-9));
            Assert.That(row.Flex, Is.EqualTo(2.5).Within(1e-9));
            Assert.That(row.SumFlexStart, Is.EqualTo(2.0).Within(1e-9));
            Assert.That(row.SumFlexEnd, Is.EqualTo(4.5).Within(1e-9));
        });
    }

    /// <summary>
    /// R1: a shift the worker started but never stopped counts 0 hours, and
    /// its break is ignored. The old formula took Stop1Id - Start1Id with
    /// Stop1Id = 0, i.e. (0 - 97 - 6) * 5 min = -8.58 h.
    /// </summary>
    [Test]
    public async Task StartWithoutStop_FiveMinute_CountsZeroHours_AndFlexIsMinusPlanHours()
    {
        var siteId = NextSiteId();
        var today = DateTime.Today;
        await SeedAssignedSite(siteId, useOneMinute: false);
        await SeedFiveMinuteAnchor(siteId, today.AddDays(-3), sumFlexEnd: 2.0);
        await SeedRow(siteId, today.AddDays(-2), pr =>
        {
            pr.PlanHours = 7.5;
            pr.Start1Id = 97; // 08:00
            pr.Stop1Id = 0;   // never stopped
            pr.Pause1Id = 7;  // 30 min, ignored on an open shift
        });

        await Submit(siteId, today.AddDays(-2));

        var row = await Reload(siteId, today.AddDays(-2));
        Assert.Multiple(() =>
        {
            Assert.That(row.NettoHours, Is.EqualTo(0).Within(1e-9));
            Assert.That(row.Flex, Is.EqualTo(-7.5).Within(1e-9));
            Assert.That(row.SumFlexStart, Is.EqualTo(2.0).Within(1e-9));
            Assert.That(row.SumFlexEnd, Is.EqualTo(-5.5).Within(1e-9));
        });
    }

    /// <summary>R1: a stop before its start counts 0 hours, never negative.</summary>
    [Test]
    public async Task StopBeforeStart_FiveMinute_CountsZeroHours()
    {
        var siteId = NextSiteId();
        var today = DateTime.Today;
        await SeedAssignedSite(siteId, useOneMinute: false);
        await SeedFiveMinuteAnchor(siteId, today.AddDays(-3), sumFlexEnd: 2.0);
        await SeedRow(siteId, today.AddDays(-2), pr =>
        {
            pr.PlanHours = 7.5;
            pr.Start1Id = 193; // 16:00
            pr.Stop1Id = 97;   // 08:00
            pr.Pause1Id = 1;
        });

        await Submit(siteId, today.AddDays(-2));

        var row = await Reload(siteId, today.AddDays(-2));
        Assert.Multiple(() =>
        {
            Assert.That(row.NettoHours, Is.EqualTo(0).Within(1e-9));
            Assert.That(row.Flex, Is.EqualTo(-7.5).Within(1e-9));
            Assert.That(row.SumFlexEnd, Is.EqualTo(-5.5).Within(1e-9));
        });
    }
}
