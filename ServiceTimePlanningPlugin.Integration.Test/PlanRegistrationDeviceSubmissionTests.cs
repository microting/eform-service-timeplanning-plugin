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

    // ------------------------------------------------------------------
    // R4: the walk after the submitted day
    // ------------------------------------------------------------------

    /// <summary>
    /// The balance reaches the worker's LAST row, a pre-created future day
    /// included. (The old unbounded walk already did this; this guards it
    /// through the switch to RunForwardAsync.)
    /// </summary>
    [Test]
    public async Task LaterRows_CarryTheBalance_ThroughAFuturePreCreatedRow()
    {
        var siteId = NextSiteId();
        var today = DateTime.Today;
        await SeedAssignedSite(siteId, useOneMinute: false);
        await SeedFiveMinuteAnchor(siteId, today.AddDays(-3), sumFlexEnd: 2.0);
        await SeedRow(siteId, today.AddDays(-2), pr =>
        {
            pr.PlanHours = 7;
            pr.Start1Id = 97;  // 08:00
            pr.Stop1Id = 193;  // 16:00
            pr.Pause1Id = 7;   // 30 min -> 7.5 h, flex +0.5
        });
        // Stale chain on both later rows: never carried forward.
        await SeedRow(siteId, today.AddDays(-1), pr =>
        {
            pr.PlanHours = 7;
            pr.NettoHours = 8;
        });
        await SeedRow(siteId, today.AddDays(10), pr =>
        {
            pr.PlanHours = 7;
        });

        await Submit(siteId, today.AddDays(-2));

        var submitted = await Reload(siteId, today.AddDays(-2));
        var dMinus1 = await Reload(siteId, today.AddDays(-1));
        var future = await Reload(siteId, today.AddDays(10));
        Assert.Multiple(() =>
        {
            Assert.That(submitted.SumFlexEnd, Is.EqualTo(2.5).Within(1e-9));
            Assert.That(dMinus1.SumFlexStart, Is.EqualTo(submitted.SumFlexEnd).Within(1e-9));
            Assert.That(dMinus1.Flex, Is.EqualTo(1.0).Within(1e-9));
            Assert.That(dMinus1.SumFlexEnd, Is.EqualTo(3.5).Within(1e-9));
            Assert.That(dMinus1.NettoHours, Is.EqualTo(8).Within(1e-9), "a later row's hours are never recomputed");
            Assert.That(future.SumFlexStart, Is.EqualTo(dMinus1.SumFlexEnd).Within(1e-9));
            Assert.That(future.SumFlexEnd, Is.EqualTo(-3.5).Within(1e-9));
        });
    }

    /// <summary>
    /// R2, the incident scenario: a later one-minute row whose device stamps
    /// (08:00-17:00, 9 h) disagree with the hours an office user set (8 h,
    /// stored). The old walk ran ApplyNettoFlexChainSecondPrecision on it and
    /// re-derived 9 h from the stamps. The walk must keep the stored hours
    /// and only move the balance.
    /// </summary>
    [Test]
    public async Task LaterOneMinuteRow_KeepsItsStoredHours_WhenItsStampsDisagree()
    {
        var siteId = NextSiteId();
        var today = DateTime.Today;
        await SeedAssignedSite(siteId, useOneMinute: true);
        await SeedRow(siteId, today.AddDays(-3), pr =>
        {
            pr.RegisteredUnderOneMinuteIntervals = true;
            pr.PlanHours = 8;
            pr.PlanHoursInSeconds = 28800;
            pr.NettoHours = 8;
            pr.NettoHoursInSeconds = 28800;
            pr.SumFlexStart = 1.0;
            pr.SumFlexStartInSeconds = 3600;
            pr.SumFlexEnd = 1.0;
            pr.SumFlexEndInSeconds = 3600;
        });
        await SeedRow(siteId, today.AddDays(-2), pr =>
        {
            pr.RegisteredUnderOneMinuteIntervals = true;
            pr.PlanHours = 7.5;
            pr.PlanHoursInSeconds = 27000;
            pr.Start1Id = 97;  // ids only: 08:00-16:00, 30 min break -> 27000 s
            pr.Stop1Id = 193;
            pr.Pause1Id = 7;
        });
        var laterDate = today.AddDays(-1);
        await SeedRow(siteId, laterDate, pr =>
        {
            pr.RegisteredUnderOneMinuteIntervals = true;
            pr.PlanHours = 8;
            pr.PlanHoursInSeconds = 28800;
            pr.Start1StartedAt = laterDate.AddHours(8);
            pr.Stop1StoppedAt = laterDate.AddHours(17); // stamps say 9 h
            pr.NettoHours = 8;                          // office says 8 h
            pr.NettoHoursInSeconds = 28800;
        });

        await Submit(siteId, today.AddDays(-2));

        var later = await Reload(siteId, laterDate);
        Assert.Multiple(() =>
        {
            Assert.That(later.NettoHoursInSeconds, Is.EqualTo(28800), "stored hours must survive the walk");
            Assert.That(later.NettoHours, Is.EqualTo(8).Within(1e-9));
            Assert.That(later.SumFlexStartInSeconds, Is.EqualTo(3600));
            Assert.That(later.FlexInSeconds, Is.EqualTo(0));
            Assert.That(later.SumFlexEndInSeconds, Is.EqualTo(3600));
        });
    }

    /// <summary>
    /// A resubmission that leaves the day's closing balance unchanged must not
    /// rewrite later rows: no Version bump and no version row. The old walk
    /// called PnBase.Update on every later row, which always bumps Version.
    /// </summary>
    [Test]
    public async Task LaterRowsWhoseBalanceIsUnchanged_AreNotRewritten()
    {
        var siteId = NextSiteId();
        var today = DateTime.Today;
        await SeedAssignedSite(siteId, useOneMinute: false);
        await SeedFiveMinuteAnchor(siteId, today.AddDays(-3), sumFlexEnd: 1.0);
        await SeedRow(siteId, today.AddDays(-2), pr =>
        {
            pr.PlanHours = 7.5;
            pr.Start1Id = 97;
            pr.Stop1Id = 193;
            pr.Pause1Id = 7; // 7.5 h, flex 0
            pr.NettoHours = 7.5;
            pr.SumFlexStart = 1.0;
            pr.SumFlexEnd = 1.0;
        });
        await SeedRow(siteId, today.AddDays(-1), pr =>
        {
            pr.PlanHours = 8;
            pr.NettoHours = 8;
            pr.SumFlexStart = 1.0;
            pr.SumFlexEnd = 1.0;
        });
        var before = await Reload(siteId, today.AddDays(-1));
        var versionRowsBefore = await TimePlanningPnDbContext.PlanRegistrationVersions
            .CountAsync(x => x.PlanRegistrationId == before.Id);

        await Submit(siteId, today.AddDays(-2));

        var after = await Reload(siteId, today.AddDays(-1));
        var versionRowsAfter = await TimePlanningPnDbContext.PlanRegistrationVersions
            .CountAsync(x => x.PlanRegistrationId == before.Id);
        Assert.Multiple(() =>
        {
            Assert.That(after.Version, Is.EqualTo(before.Version));
            Assert.That(versionRowsAfter, Is.EqualTo(versionRowsBefore));
            Assert.That(after.SumFlexStart, Is.EqualTo(1.0).Within(1e-9));
        });
    }
}
