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
using Microting.eFormApi.BasePn.Infrastructure.Database.Entities;
using Microting.TimePlanningBase.Infrastructure.Data.Entities;
using NUnit.Framework;
using ServiceTimePlanningPlugin.Scheduler.Jobs;

namespace ServiceTimePlanningPlugin.Integration.Test;

/// <summary>
/// Covers the nightly flex-chain walk. Most tests call RunWalk(lookBackDays)
/// directly; the Execute* tests go through ExecuteAt(utcNow) with a forced
/// 03:00 UTC instant, so they exercise the setting without depending on the
/// wall-clock hour.
///
/// "Today" is DateTime.UtcNow.Date, matching the job. All rows are five-minute
/// (decimal) rows on a site without one-minute intervals, so the walk applies
/// Flex = NettoHours - PlanHours, SumFlexStart = predecessor.SumFlexEnd,
/// SumFlexEnd = SumFlexStart + Flex - PaiedOutFlex (PaiedOutFlex is 0 here).
/// </summary>
[TestFixture]
public class FlexChainNightlyWalkJobTests : TestBaseSetup
{
    private FlexChainNightlyWalkJob _job = null!;
    private static int _nextSiteId = 200000;

    [SetUp]
    public void SetUpJob()
    {
        _job = new FlexChainNightlyWalkJob(DbContextHelper);
    }

    private static int NextSiteId() => _nextSiteId++;

    private static DateTime Today => DateTime.UtcNow.Date;

    private async Task<AssignedSite> SeedAssignedSite(int siteId, bool resigned = false, bool removed = false)
    {
        var assignedSite = new AssignedSite
        {
            SiteId = siteId,
            UseOneMinuteIntervals = false,
            Resigned = resigned,
            WorkflowState = Constants.WorkflowStates.Created,
            CreatedByUserId = 1,
            UpdatedByUserId = 1
        };
        await assignedSite.Create(TimePlanningPnDbContext);

        if (removed)
        {
            await assignedSite.Delete(TimePlanningPnDbContext);
        }

        return assignedSite;
    }

    /// <summary>
    /// A five-minute row with its chain values stored exactly as given, so a
    /// test can seed both consistent rows and breaks.
    /// </summary>
    private async Task<PlanRegistration> SeedRow(
        int siteId, DateTime date, double planHours, double nettoHours,
        double flex, double sumFlexStart, double sumFlexEnd, bool reconciled = false)
    {
        var pr = new PlanRegistration
        {
            SdkSitId = siteId,
            Date = date,
            PlanHours = planHours,
            NettoHours = nettoHours,
            Flex = flex,
            SumFlexStart = sumFlexStart,
            SumFlexEnd = sumFlexEnd,
            Reconciled = reconciled,
            ReconciledAt = reconciled ? DateTime.UtcNow.AddHours(-1) : null,
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

    private async Task SetLookBackDays(string value)
    {
        TimePlanningPnDbContext.PluginConfigurationValues.Add(new PluginConfigurationValue
        {
            Name = "TimePlanningBaseSettings:FlexChainNightlyWalkDays",
            Value = value,
            WorkflowState = Constants.WorkflowStates.Created,
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = 1,
            UpdatedByUserId = 1,
            Version = 1
        });
        await TimePlanningPnDbContext.SaveChangesAsync();
    }

    // ------------------------------------------------------------------
    // (a) A break inside the window is healed and carried through a row
    // pre-created 30 days ahead.
    // ------------------------------------------------------------------
    [Test]
    public async Task BreakInsideWindow_IsHealed_AndFutureRowIsCarried()
    {
        var siteId = NextSiteId();
        var today = Today;
        await SeedAssignedSite(siteId);

        // Seed, just before the window: balance 5.
        await SeedRow(siteId, today.AddDays(-8), planHours: 7, nettoHours: 8, flex: 1, sumFlexStart: 4, sumFlexEnd: 5);
        // Consistent: 5 -> 6.
        var dMinus3Before = await SeedRow(siteId, today.AddDays(-3), 7, 8, flex: 1, sumFlexStart: 5, sumFlexEnd: 6);
        // The break: starts at 0 instead of 6.
        await SeedRow(siteId, today.AddDays(-2), 7, 8, flex: 1, sumFlexStart: 0, sumFlexEnd: 1);
        // Pre-created future row, nothing planned or worked yet, still holding zero.
        await SeedRow(siteId, today.AddDays(30), 0, 0, flex: 0, sumFlexStart: 0, sumFlexEnd: 0);

        var written = await _job.RunWalk(7);

        var dMinus3 = await ReloadRow(siteId, today.AddDays(-3));
        var dMinus2 = await ReloadRow(siteId, today.AddDays(-2));
        var future = await ReloadRow(siteId, today.AddDays(30));

        Assert.Multiple(() =>
        {
            Assert.That(written, Is.EqualTo(2), "the break and the future row");
            Assert.That(dMinus3.Version, Is.EqualTo(dMinus3Before.Version), "an unchanged row is not written");
            Assert.That(dMinus2.SumFlexStart, Is.EqualTo(6.0).Within(1e-9));
            Assert.That(dMinus2.SumFlexEnd, Is.EqualTo(7.0).Within(1e-9));
            Assert.That(future.SumFlexStart, Is.EqualTo(7.0).Within(1e-9));
            Assert.That(future.SumFlexEnd, Is.EqualTo(7.0).Within(1e-9));
        });
    }

    // ------------------------------------------------------------------
    // (b) A break older than the window is left as stored; the walk seeds
    // from the stored row just before the window.
    // ------------------------------------------------------------------
    [Test]
    public async Task BreakOlderThanWindow_IsLeftAsStored()
    {
        var siteId = NextSiteId();
        var today = Today;
        await SeedAssignedSite(siteId);

        await SeedRow(siteId, today.AddDays(-11), 7, 8, flex: 1, sumFlexStart: 0, sumFlexEnd: 1);
        // The old break: should start at 1, starts at 0.
        var oldBreakBefore = await SeedRow(siteId, today.AddDays(-10), 7, 8, flex: 1, sumFlexStart: 0, sumFlexEnd: 1);
        // Consistent with the stored old break: 1 -> 2. The walk's seed.
        var seedBefore = await SeedRow(siteId, today.AddDays(-8), 7, 8, flex: 1, sumFlexStart: 1, sumFlexEnd: 2);
        // Inside the window, stale: should continue from 2.
        await SeedRow(siteId, today.AddDays(-5), 7, 8, flex: 1, sumFlexStart: 0, sumFlexEnd: 1);

        var written = await _job.RunWalk(7);

        var oldBreak = await ReloadRow(siteId, today.AddDays(-10));
        var seed = await ReloadRow(siteId, today.AddDays(-8));
        var dMinus5 = await ReloadRow(siteId, today.AddDays(-5));

        Assert.Multiple(() =>
        {
            Assert.That(written, Is.EqualTo(1));
            Assert.That(oldBreak.Version, Is.EqualTo(oldBreakBefore.Version));
            Assert.That(oldBreak.SumFlexStart, Is.EqualTo(0.0).Within(1e-9), "a break older than the window is not healed");
            Assert.That(oldBreak.SumFlexEnd, Is.EqualTo(1.0).Within(1e-9));
            Assert.That(seed.Version, Is.EqualTo(seedBefore.Version));
            Assert.That(dMinus5.SumFlexStart, Is.EqualTo(2.0).Within(1e-9), "seeded from the stored row at today-8");
            Assert.That(dMinus5.SumFlexEnd, Is.EqualTo(3.0).Within(1e-9));
        });
    }

    // ------------------------------------------------------------------
    // (c) A reconciled day inside the window is not written, and the walk
    // continues from its stored balance.
    // ------------------------------------------------------------------
    [Test]
    public async Task ReconciledDayInsideWindow_IsNotWritten_AndWalkContinuesFromIt()
    {
        var siteId = NextSiteId();
        var today = Today;
        await SeedAssignedSite(siteId);

        // Rows on or before the boundary are seeded before it: once the
        // boundary exists, creating a row inside the locked range is refused.
        await SeedRow(siteId, today.AddDays(-9), 7, 8, flex: 1, sumFlexStart: 4, sumFlexEnd: 5);
        // Inside the window but locked, and broken (should start at 5).
        var lockedBreakBefore = await SeedRow(siteId, today.AddDays(-6), 7, 8, flex: 1, sumFlexStart: 0, sumFlexEnd: 1);
        // The reconciled boundary, holding a balance the chain would not
        // reproduce (it would give 1 -> 2).
        var boundaryBefore = await SeedRow(siteId, today.AddDays(-4), 7, 8, flex: 1,
            sumFlexStart: 20, sumFlexEnd: 21, reconciled: true);
        // Open days after the boundary, still holding zero.
        await SeedRow(siteId, today.AddDays(-3), 7, 8, flex: 0, sumFlexStart: 0, sumFlexEnd: 0);
        await SeedRow(siteId, today.AddDays(-1), 7, 8, flex: 0, sumFlexStart: 0, sumFlexEnd: 0);

        var written = await _job.RunWalk(7);

        var lockedBreak = await ReloadRow(siteId, today.AddDays(-6));
        var boundary = await ReloadRow(siteId, today.AddDays(-4));
        var dMinus3 = await ReloadRow(siteId, today.AddDays(-3));
        var dMinus1 = await ReloadRow(siteId, today.AddDays(-1));

        Assert.Multiple(() =>
        {
            Assert.That(written, Is.EqualTo(2));
            Assert.That(boundary.Version, Is.EqualTo(boundaryBefore.Version), "the reconciled day is not written");
            Assert.That(boundary.SumFlexStart, Is.EqualTo(20.0).Within(1e-9));
            Assert.That(boundary.SumFlexEnd, Is.EqualTo(21.0).Within(1e-9));
            Assert.That(lockedBreak.Version, Is.EqualTo(lockedBreakBefore.Version), "a locked day is not written");
            Assert.That(lockedBreak.SumFlexStart, Is.EqualTo(0.0).Within(1e-9));
            Assert.That(dMinus3.SumFlexStart, Is.EqualTo(21.0).Within(1e-9), "continues from the reconciled balance");
            Assert.That(dMinus3.SumFlexEnd, Is.EqualTo(22.0).Within(1e-9));
            Assert.That(dMinus1.SumFlexStart, Is.EqualTo(22.0).Within(1e-9));
            Assert.That(dMinus1.SumFlexEnd, Is.EqualTo(23.0).Within(1e-9));
        });
    }

    // ------------------------------------------------------------------
    // (d) An already-consistent chain writes nothing.
    // ------------------------------------------------------------------
    [Test]
    public async Task ConsistentChain_WritesNothing()
    {
        var siteId = NextSiteId();
        var today = Today;
        await SeedAssignedSite(siteId);

        var rows = new[]
        {
            await SeedRow(siteId, today.AddDays(-9), 7, 8, flex: 1, sumFlexStart: 0, sumFlexEnd: 1),
            await SeedRow(siteId, today.AddDays(-5), 7, 8, flex: 1, sumFlexStart: 1, sumFlexEnd: 2),
            await SeedRow(siteId, today.AddDays(-2), 7, 8, flex: 1, sumFlexStart: 2, sumFlexEnd: 3),
            await SeedRow(siteId, today.AddDays(10), 0, 0, flex: 0, sumFlexStart: 3, sumFlexEnd: 3)
        };

        var written = await _job.RunWalk(7);

        Assert.That(written, Is.EqualTo(0));
        foreach (var before in rows)
        {
            var after = await ReloadRow(siteId, before.Date);
            Assert.That(after.Version, Is.EqualTo(before.Version),
                $"the row on {before.Date:yyyy-MM-dd} must not be written");
        }
    }

    // ------------------------------------------------------------------
    // (e) Resigned and removed workers are not walked.
    // ------------------------------------------------------------------
    [Test]
    public async Task ResignedAndRemovedWorkers_AreNotWalked()
    {
        var today = Today;
        var activeSiteId = NextSiteId();
        var resignedSiteId = NextSiteId();
        var removedSiteId = NextSiteId();

        await SeedAssignedSite(activeSiteId);
        await SeedAssignedSite(resignedSiteId, resigned: true);
        await SeedAssignedSite(removedSiteId, removed: true);

        foreach (var siteId in new[] { activeSiteId, resignedSiteId, removedSiteId })
        {
            await SeedRow(siteId, today.AddDays(-3), 7, 8, flex: 1, sumFlexStart: 0, sumFlexEnd: 1);
            // The break: should start at 1.
            await SeedRow(siteId, today.AddDays(-2), 7, 8, flex: 1, sumFlexStart: 0, sumFlexEnd: 1);
        }

        var written = await _job.RunWalk(7);

        var active = await ReloadRow(activeSiteId, today.AddDays(-2));
        var resigned = await ReloadRow(resignedSiteId, today.AddDays(-2));
        var removed = await ReloadRow(removedSiteId, today.AddDays(-2));

        Assert.Multiple(() =>
        {
            Assert.That(written, Is.EqualTo(1), "only the active worker's break");
            Assert.That(active.SumFlexStart, Is.EqualTo(1.0).Within(1e-9));
            Assert.That(active.SumFlexEnd, Is.EqualTo(2.0).Within(1e-9));
            Assert.That(resigned.Version, Is.EqualTo(1));
            Assert.That(resigned.SumFlexStart, Is.EqualTo(0.0).Within(1e-9));
            Assert.That(removed.Version, Is.EqualTo(1));
            Assert.That(removed.SumFlexStart, Is.EqualTo(0.0).Within(1e-9));
        });
    }

    // ------------------------------------------------------------------
    // (f) Execute reads the setting: "0" disables the walk.
    // ------------------------------------------------------------------
    [Test]
    public async Task Execute_WithLookBackZero_WritesNothing()
    {
        var siteId = NextSiteId();
        var today = Today;
        await SetLookBackDays("0");
        await SeedAssignedSite(siteId);
        await SeedRow(siteId, today.AddDays(-3), 7, 8, flex: 1, sumFlexStart: 0, sumFlexEnd: 1);
        var breakBefore = await SeedRow(siteId, today.AddDays(-2), 7, 8, flex: 1, sumFlexStart: 0, sumFlexEnd: 1);

        await _job.ExecuteAt(today.AddHours(3));

        var breakAfter = await ReloadRow(siteId, today.AddDays(-2));
        Assert.Multiple(() =>
        {
            Assert.That(breakAfter.Version, Is.EqualTo(breakBefore.Version));
            Assert.That(breakAfter.SumFlexStart, Is.EqualTo(0.0).Within(1e-9));
        });
    }

    // ------------------------------------------------------------------
    // (g) Execute with no setting walks 7 days: a break on today-7 is
    // healed, one on today-8 is not.
    // ------------------------------------------------------------------
    [Test]
    public async Task Execute_WithNoSetting_DefaultsToSevenDays()
    {
        var siteId = NextSiteId();
        var today = Today;
        await SeedAssignedSite(siteId);
        await SeedRow(siteId, today.AddDays(-9), 7, 8, flex: 1, sumFlexStart: 0, sumFlexEnd: 1);
        // Break just outside the default window: should start at 1.
        var outsideBefore = await SeedRow(siteId, today.AddDays(-8), 7, 8, flex: 1, sumFlexStart: 10, sumFlexEnd: 11);
        // Break on the first day of the window: should start at 11 (the stored today-8).
        await SeedRow(siteId, today.AddDays(-7), 7, 8, flex: 1, sumFlexStart: 0, sumFlexEnd: 1);

        await _job.ExecuteAt(today.AddHours(3));

        var outside = await ReloadRow(siteId, today.AddDays(-8));
        var firstDay = await ReloadRow(siteId, today.AddDays(-7));

        Assert.Multiple(() =>
        {
            Assert.That(outside.Version, Is.EqualTo(outsideBefore.Version));
            Assert.That(outside.SumFlexStart, Is.EqualTo(10.0).Within(1e-9));
            Assert.That(firstDay.SumFlexStart, Is.EqualTo(11.0).Within(1e-9));
            Assert.That(firstDay.SumFlexEnd, Is.EqualTo(12.0).Within(1e-9));
        });
    }

    // ------------------------------------------------------------------
    // (h) Execute outside 03:00 UTC does nothing.
    // ------------------------------------------------------------------
    [Test]
    public async Task Execute_OutsideHourThree_WritesNothing()
    {
        var siteId = NextSiteId();
        var today = Today;
        await SeedAssignedSite(siteId);
        await SeedRow(siteId, today.AddDays(-3), 7, 8, flex: 1, sumFlexStart: 0, sumFlexEnd: 1);
        var breakBefore = await SeedRow(siteId, today.AddDays(-2), 7, 8, flex: 1, sumFlexStart: 0, sumFlexEnd: 1);

        await _job.ExecuteAt(today.AddHours(4));

        var breakAfter = await ReloadRow(siteId, today.AddDays(-2));
        Assert.That(breakAfter.Version, Is.EqualTo(breakBefore.Version));
    }
}
