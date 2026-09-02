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
/// Covers the daily flex-chain catch-up job (FlexChainCatchUpJob.RunCatchUp --
/// the un-gated entry point; Execute() additionally requires
/// DateTime.UtcNow.Hour == 3, which would make these tests depend on the
/// wall-clock hour they happen to run at).
///
/// "Today" throughout is the real DateTime.Now.Date, since the job reads the
/// system clock directly rather than taking an injected clock -- matching
/// SearchListJob's own convention in this repo.
/// </summary>
[TestFixture]
public class FlexChainCatchUpJobTests : TestBaseSetup
{
    private FlexChainCatchUpJob _job = null!;
    private static int _nextSiteId = 100000;

    [SetUp]
    public void SetUpJob()
    {
        _job = new FlexChainCatchUpJob(DbContextHelper);
    }

    private static int NextSiteId() => _nextSiteId++;

    private async Task<AssignedSite> SeedAssignedSite(
        int siteId,
        bool useOneMinute = false,
        DateTime? computedThrough = null,
        DateTime? useOneMinuteFrom = null)
    {
        var assignedSite = new AssignedSite
        {
            SiteId = siteId,
            UseOneMinuteIntervals = useOneMinute,
            UseOneMinuteIntervalsFrom = useOneMinuteFrom,
            FlexChainComputedThrough = computedThrough,
            WorkflowState = Constants.WorkflowStates.Created,
            CreatedByUserId = 1,
            UpdatedByUserId = 1
        };
        await assignedSite.Create(TimePlanningPnDbContext);
        return assignedSite;
    }

    /// <summary>
    /// Seeds a five-minute (decimal-mode) row. NettoHours is taken AS-IS by
    /// ApplyNettoFlexChainDecimal (it does not recompute from tick ids), so
    /// this simulates a row whose real work was already registered but whose
    /// running SumFlex chain was never (re)computed -- the exact hole this
    /// job repairs. SumFlexStart/SumFlexEnd default to 0 unless overridden,
    /// modelling the pre-created zero-flex placeholder.
    /// </summary>
    private async Task<PlanRegistration> SeedFiveMinuteRow(
        int siteId, DateTime date, double planHours, double nettoHours,
        double sumFlexStart = 0, double sumFlexEnd = 0)
    {
        var pr = new PlanRegistration
        {
            SdkSitId = siteId,
            Date = date,
            PlanHours = planHours,
            NettoHours = nettoHours,
            SumFlexStart = sumFlexStart,
            SumFlexEnd = sumFlexEnd,
            PlanText = "",
            CommentOffice = "",
            CommentOfficeAll = "",
            WorkflowState = Constants.WorkflowStates.Created,
            CreatedByUserId = 1,
            UpdatedByUserId = 1
        };
        await pr.Create(TimePlanningPnDbContext);
        return pr;
    }

    /// <summary>
    /// Seeds a one-minute-mode row via exact DateTime shift stamps (what
    /// ApplyNettoFlexChainSecondPrecision actually computes NettoHours from).
    /// Deliberately does NOT set RegisteredUnderOneMinuteIntervals -- these
    /// are pre-created placeholder rows with no write-time marker, so their
    /// mode must resolve via the timeline (effective date / audit trail),
    /// exactly like the real holes this job repairs.
    /// </summary>
    private async Task<PlanRegistration> SeedOneMinuteShapedRow(
        int siteId, DateTime date, double planHours, DateTime start1, DateTime stop1)
    {
        var pr = new PlanRegistration
        {
            SdkSitId = siteId,
            Date = date,
            PlanHours = planHours,
            Start1StartedAt = start1,
            Stop1StoppedAt = stop1,
            PlanText = "",
            CommentOffice = "",
            CommentOfficeAll = "",
            WorkflowState = Constants.WorkflowStates.Created,
            CreatedByUserId = 1,
            UpdatedByUserId = 1
        };
        await pr.Create(TimePlanningPnDbContext);
        return pr;
    }

    private async Task<PlanRegistration> ReloadRow(int siteId, DateTime date)
        => await TimePlanningPnDbContext.PlanRegistrations
            .AsNoTracking()
            .SingleAsync(x => x.SdkSitId == siteId && x.Date == date);

    private async Task<AssignedSite> ReloadAssignedSite(int siteId)
        => await TimePlanningPnDbContext.AssignedSites
            .AsNoTracking()
            .SingleAsync(x => x.SiteId == siteId);

    // ------------------------------------------------------------------
    // A multi-day gap is filled and the cursor advances to today.
    // ------------------------------------------------------------------
    [Test]
    public async Task MultiDayGap_IsFilled_AndCursorAdvancesToToday()
    {
        var siteId = NextSiteId();
        var today = DateTime.Today;

        await SeedAssignedSite(siteId, useOneMinute: false, computedThrough: today.AddDays(-4));

        // The already-computed anchor row (at the cursor date) the walk seeds from.
        await SeedFiveMinuteRow(siteId, today.AddDays(-4), planHours: 8, nettoHours: 8,
            sumFlexStart: 4.0, sumFlexEnd: 5.0);

        // The gap: three past days plus today, each holding zero SumFlex (the hole),
        // but with real NettoHours already registered (+1h flex/day).
        await SeedFiveMinuteRow(siteId, today.AddDays(-3), planHours: 7, nettoHours: 8);
        await SeedFiveMinuteRow(siteId, today.AddDays(-2), planHours: 7, nettoHours: 8);
        await SeedFiveMinuteRow(siteId, today.AddDays(-1), planHours: 7, nettoHours: 8);
        await SeedFiveMinuteRow(siteId, today, planHours: 7, nettoHours: 8);

        await _job.RunCatchUp();

        var dMinus3 = await ReloadRow(siteId, today.AddDays(-3));
        var dMinus2 = await ReloadRow(siteId, today.AddDays(-2));
        var dMinus1 = await ReloadRow(siteId, today.AddDays(-1));
        var dToday = await ReloadRow(siteId, today);
        var assignedSite = await ReloadAssignedSite(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(dMinus3.SumFlexStart, Is.EqualTo(5.0).Within(1e-9));
            Assert.That(dMinus3.SumFlexEnd, Is.EqualTo(6.0).Within(1e-9));
            Assert.That(dMinus2.SumFlexStart, Is.EqualTo(6.0).Within(1e-9));
            Assert.That(dMinus2.SumFlexEnd, Is.EqualTo(7.0).Within(1e-9));
            Assert.That(dMinus1.SumFlexStart, Is.EqualTo(7.0).Within(1e-9));
            Assert.That(dMinus1.SumFlexEnd, Is.EqualTo(8.0).Within(1e-9));
            Assert.That(dToday.SumFlexStart, Is.EqualTo(8.0).Within(1e-9));
            Assert.That(dToday.SumFlexEnd, Is.EqualTo(9.0).Within(1e-9));
            Assert.That(assignedSite.FlexChainComputedThrough, Is.EqualTo(today));
        });
    }

    // ------------------------------------------------------------------
    // A second run is a genuine no-op: cursor already today, nothing rewritten.
    // ------------------------------------------------------------------
    [Test]
    public async Task SecondRun_IsNoOp()
    {
        var siteId = NextSiteId();
        var today = DateTime.Today;

        await SeedAssignedSite(siteId, useOneMinute: false, computedThrough: today.AddDays(-1));
        await SeedFiveMinuteRow(siteId, today.AddDays(-1), planHours: 8, nettoHours: 8,
            sumFlexStart: 0, sumFlexEnd: 0);
        await SeedFiveMinuteRow(siteId, today, planHours: 7, nettoHours: 8);

        await _job.RunCatchUp();

        var afterFirstRun = await ReloadRow(siteId, today);
        var siteAfterFirstRun = await ReloadAssignedSite(siteId);

        await _job.RunCatchUp();

        var afterSecondRun = await ReloadRow(siteId, today);
        var siteAfterSecondRun = await ReloadAssignedSite(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(afterSecondRun.Version, Is.EqualTo(afterFirstRun.Version),
                "A no-op second run must not re-save the row (Version must not increment).");
            Assert.That(afterSecondRun.SumFlexEnd, Is.EqualTo(afterFirstRun.SumFlexEnd).Within(1e-9));
            Assert.That(siteAfterSecondRun.Version, Is.EqualTo(siteAfterFirstRun.Version),
                "A no-op second run must not re-save the AssignedSite.");
            Assert.That(siteAfterSecondRun.FlexChainComputedThrough, Is.EqualTo(today));
        });
    }

    // ------------------------------------------------------------------
    // A site with no registrations neither throws nor sets a cursor.
    // ------------------------------------------------------------------
    [Test]
    public async Task SiteWithNoRegistrations_DoesNotThrow_AndDoesNotSetCursor()
    {
        var siteId = NextSiteId();
        await SeedAssignedSite(siteId, useOneMinute: false, computedThrough: null);

        Assert.DoesNotThrowAsync(async () => await _job.RunCatchUp());

        var assignedSite = await ReloadAssignedSite(siteId);
        Assert.That(assignedSite.FlexChainComputedThrough, Is.Null);
    }

    // ------------------------------------------------------------------
    // Rows dated after today are untouched.
    // ------------------------------------------------------------------
    [Test]
    public async Task RowsAfterToday_AreUntouched()
    {
        var siteId = NextSiteId();
        var today = DateTime.Today;

        await SeedAssignedSite(siteId, useOneMinute: false, computedThrough: today.AddDays(-2));
        await SeedFiveMinuteRow(siteId, today.AddDays(-2), planHours: 8, nettoHours: 8,
            sumFlexStart: 0, sumFlexEnd: 0);
        await SeedFiveMinuteRow(siteId, today.AddDays(-1), planHours: 7, nettoHours: 8);
        await SeedFiveMinuteRow(siteId, today, planHours: 7, nettoHours: 8);
        // Future placeholder rows: plan hours already scheduled, no work done yet,
        // flex chain untouched -- exactly the pre-created future rows the job
        // must never write to.
        var future1 = await SeedFiveMinuteRow(siteId, today.AddDays(1), planHours: 7, nettoHours: 0);
        var future5 = await SeedFiveMinuteRow(siteId, today.AddDays(5), planHours: 7, nettoHours: 0);

        await _job.RunCatchUp();

        var future1After = await ReloadRow(siteId, today.AddDays(1));
        var future5After = await ReloadRow(siteId, today.AddDays(5));

        Assert.Multiple(() =>
        {
            Assert.That(future1After.Version, Is.EqualTo(future1.Version));
            Assert.That(future1After.SumFlexStart, Is.EqualTo(0.0));
            Assert.That(future1After.SumFlexEnd, Is.EqualTo(0.0));
            Assert.That(future5After.Version, Is.EqualTo(future5.Version));
            Assert.That(future5After.SumFlexStart, Is.EqualTo(0.0));
            Assert.That(future5After.SumFlexEnd, Is.EqualTo(0.0));
        });
    }

    // ------------------------------------------------------------------
    // A gap spanning a UseOneMinuteIntervalsFrom boundary keeps five-minute
    // semantics before the date and one-minute semantics on and after it.
    // ------------------------------------------------------------------
    [Test]
    public async Task GapSpanningOneMinuteBoundary_KeepsFiveMinuteBeforeAndOneMinuteOnAndAfter()
    {
        var siteId = NextSiteId();
        var today = DateTime.Today;
        var boundary = today.AddDays(-1); // one-minute semantics from here onward

        await SeedAssignedSite(siteId, useOneMinute: true,
            computedThrough: today.AddDays(-3), useOneMinuteFrom: boundary);

        // Anchor (before the walk, already computed): SumFlexEnd = 2.0h.
        await SeedFiveMinuteRow(siteId, today.AddDays(-3), planHours: 8, nettoHours: 8,
            sumFlexStart: 1.0, sumFlexEnd: 2.0);

        // today-2: BEFORE the boundary -> five-minute (decimal) semantics.
        // NettoHours taken as-is: 8h worked, 7h planned => +1h flex. 2.0 -> 3.0.
        await SeedFiveMinuteRow(siteId, today.AddDays(-2), planHours: 7, nettoHours: 8);

        // today-1 (== boundary): ON the boundary -> one-minute semantics.
        // 8h worked (08:00-16:00), 7h planned => +1h flex, in seconds: 2h (7200s) -> 3h (10800s).
        await SeedOneMinuteShapedRow(siteId, boundary, planHours: 7,
            start1: boundary.AddHours(8), stop1: boundary.AddHours(16));

        // today: AFTER the boundary -> one-minute semantics.
        // 8h worked, 8h planned => flex 0. Balance stays at 3h (10800s).
        await SeedOneMinuteShapedRow(siteId, today, planHours: 8,
            start1: today.AddHours(8), stop1: today.AddHours(16));

        await _job.RunCatchUp();

        var dMinus2 = await ReloadRow(siteId, today.AddDays(-2));
        var boundaryRow = await ReloadRow(siteId, boundary);
        var todayRow = await ReloadRow(siteId, today);
        var assignedSite = await ReloadAssignedSite(siteId);

        Assert.Multiple(() =>
        {
            // Five-minute row: decimal chain, no seconds columns populated.
            Assert.That(dMinus2.SumFlexEnd, Is.EqualTo(3.0).Within(1e-9));
            Assert.That(dMinus2.SumFlexEndInSeconds, Is.EqualTo(0),
                "A five-minute row must not carry a seconds balance.");

            // Boundary row: one-minute chain, seeded from the five-minute
            // predecessor's DECIMAL balance converted to seconds (3.0h -> 10800s...
            // wait: seeded from dMinus2's 3.0h => 10800s, +1h flex => 14400s = 4.0h).
            Assert.That(boundaryRow.SumFlexEndInSeconds, Is.EqualTo(14400));
            Assert.That(boundaryRow.SumFlexEnd, Is.EqualTo(4.0).Within(1e-9));

            // Today: one-minute chain, seeded from the boundary row's SECONDS
            // balance carried in memory. Flex 0 this day => unchanged at 4.0h.
            Assert.That(todayRow.SumFlexEndInSeconds, Is.EqualTo(14400));
            Assert.That(todayRow.SumFlexEnd, Is.EqualTo(4.0).Within(1e-9));

            Assert.That(assignedSite.FlexChainComputedThrough, Is.EqualTo(today));
        });
    }

    // ------------------------------------------------------------------
    // The walk is ascending: given a three-day gap, the middle row's
    // SumFlexStart equals the first row's SumFlexEnd.
    // ------------------------------------------------------------------
    [Test]
    public async Task Walk_IsAscending_MiddleRowSeedsFromFirstRowsEnd()
    {
        var siteId = NextSiteId();
        var today = DateTime.Today;

        // No cursor -- walk starts at the earliest registration.
        await SeedAssignedSite(siteId, useOneMinute: false, computedThrough: null);

        var first = today.AddDays(-2);
        var middle = today.AddDays(-1);
        var last = today;

        // Distinct per-day flex deltas so a descending walk (which would seed
        // every row from its predecessor's PRE-update value) produces a
        // different, wrong result than an ascending one.
        await SeedFiveMinuteRow(siteId, first, planHours: 6, nettoHours: 8);   // +2h
        await SeedFiveMinuteRow(siteId, middle, planHours: 7, nettoHours: 8);  // +1h
        await SeedFiveMinuteRow(siteId, last, planHours: 8, nettoHours: 8);    // +0h

        await _job.RunCatchUp();

        var firstRow = await ReloadRow(siteId, first);
        var middleRow = await ReloadRow(siteId, middle);
        var lastRow = await ReloadRow(siteId, last);

        Assert.Multiple(() =>
        {
            Assert.That(firstRow.SumFlexStart, Is.EqualTo(0.0).Within(1e-9));
            Assert.That(firstRow.SumFlexEnd, Is.EqualTo(2.0).Within(1e-9));
            Assert.That(middleRow.SumFlexStart, Is.EqualTo(firstRow.SumFlexEnd).Within(1e-9),
                "Ascending walk: the middle row must seed from the first row's closing balance.");
            Assert.That(middleRow.SumFlexEnd, Is.EqualTo(3.0).Within(1e-9));
            Assert.That(lastRow.SumFlexStart, Is.EqualTo(middleRow.SumFlexEnd).Within(1e-9));
            Assert.That(lastRow.SumFlexEnd, Is.EqualTo(3.0).Within(1e-9));
        });
    }
}
