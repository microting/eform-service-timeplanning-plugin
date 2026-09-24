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
using Microting.TimePlanningBase.Infrastructure.Helpers;
using NUnit.Framework;
using ServiceTimePlanningPlugin.Infrastructure.Helpers;

namespace ServiceTimePlanningPlugin.Integration.Test;

/// <summary>
/// Covers how PlanRegistrationHelper.UpdatePlanRegistration derives the planned
/// shift and hours from a plan text imported from the Google Sheet.
/// </summary>
[TestFixture]
public class PlanRegistrationHelperPlanTextTests : TestBaseSetup
{
    private const int SiteId = 200000;

    /// <summary>
    /// Danish-locale sheets type "7,00-15,00/1". The time parts used to split
    /// only on '.', ':' and '½', so the comma threw inside the helper's
    /// catch-all and left the day with no planned shift or hours.
    /// </summary>
    [TestCase("7,00-15,00/1")]
    [TestCase("7:00-15:00/1")]
    [TestCase("7.00-15.00/1")]
    public async Task UpdatePlanRegistration_AnyTimeSeparator_DerivesTheShiftAndHours(string planText)
    {
        var (assignedSite, planRegistration, timeline) = await ArrangeAsync(planText);

        await PlanRegistrationHelper.UpdatePlanRegistration(planRegistration, TimePlanningPnDbContext,
            assignedSite, DateTime.Now.AddMonths(-1), timeline);

        Assert.That(
            (planRegistration.PlannedStartOfShift1, planRegistration.PlannedEndOfShift1,
                planRegistration.PlannedBreakOfShift1, planRegistration.PlanHours),
            Is.EqualTo((7 * 60, 15 * 60, 60, 7.0)));
    }

    /// <summary>
    /// Breaks are decimal hours, so "1.5" is 90 minutes. The lookup table this
    /// helper used to carry topped out at "1" => 60 and returned 0 for anything
    /// longer, which silently paid an hour and a half of break as worked time.
    /// </summary>
    [Test]
    public async Task UpdatePlanRegistration_BreakLongerThanAnHour_DeductsTheFullBreak()
    {
        var (assignedSite, planRegistration, timeline) = await ArrangeAsync("7:00-15:00/1.5");

        await PlanRegistrationHelper.UpdatePlanRegistration(planRegistration, TimePlanningPnDbContext,
            assignedSite, DateTime.Now.AddMonths(-1), timeline);

        Assert.That(
            (planRegistration.PlannedStartOfShift1, planRegistration.PlannedEndOfShift1,
                planRegistration.PlannedBreakOfShift1, planRegistration.PlanHours),
            Is.EqualTo((7 * 60, 15 * 60, 90, 6.5)));
    }

    /// <summary>
    /// Regression: the third, fourth and fifth segments used to be parsed only
    /// on the branch taken when the with-break pattern had FAILED, so a segment
    /// that carried a break skipped them entirely and shifts 3-5 were never
    /// written. Their break column was unreachable on either branch.
    /// </summary>
    [Test]
    public async Task UpdatePlanRegistration_FiveShiftsEachWithABreak_PopulatesEveryShift()
    {
        var (assignedSite, planRegistration, timeline) =
            await ArrangeAsync("6:00-8:00/0.5;9:00-11:00/0.5;12:00-14:00/0.5;15:00-17:00/0.5;18:00-20:00/1");

        await PlanRegistrationHelper.UpdatePlanRegistration(planRegistration, TimePlanningPnDbContext,
            assignedSite, DateTime.Now.AddMonths(-1), timeline);

        Assert.Multiple(() =>
        {
            Assert.That(
                (planRegistration.PlannedStartOfShift1, planRegistration.PlannedEndOfShift1,
                    planRegistration.PlannedBreakOfShift1),
                Is.EqualTo((6 * 60, 8 * 60, 30)));
            Assert.That(
                (planRegistration.PlannedStartOfShift2, planRegistration.PlannedEndOfShift2,
                    planRegistration.PlannedBreakOfShift2),
                Is.EqualTo((9 * 60, 11 * 60, 30)));
            Assert.That(
                (planRegistration.PlannedStartOfShift3, planRegistration.PlannedEndOfShift3,
                    planRegistration.PlannedBreakOfShift3),
                Is.EqualTo((12 * 60, 14 * 60, 30)));
            Assert.That(
                (planRegistration.PlannedStartOfShift4, planRegistration.PlannedEndOfShift4,
                    planRegistration.PlannedBreakOfShift4),
                Is.EqualTo((15 * 60, 17 * 60, 30)));
            Assert.That(
                (planRegistration.PlannedStartOfShift5, planRegistration.PlannedEndOfShift5,
                    planRegistration.PlannedBreakOfShift5),
                Is.EqualTo((18 * 60, 20 * 60, 60)));
            // 4 x 90 minutes + 60 minutes.
            Assert.That(planRegistration.PlanHours, Is.EqualTo(7.0));
        });
    }

    /// <summary>
    /// A planner who shortens the day in the sheet must not leave yesterday's
    /// extra shifts behind: every slot is rewritten, so the segments the text no
    /// longer mentions are cleared.
    /// </summary>
    [Test]
    public async Task UpdatePlanRegistration_ShortenedPlanText_ClearsTheShiftsItNoLongerMentions()
    {
        var (assignedSite, planRegistration, timeline) =
            await ArrangeAsync("6:00-8:00;9:00-11:00;12:00-14:00;15:00-17:00;18:00-20:00");

        await PlanRegistrationHelper.UpdatePlanRegistration(planRegistration, TimePlanningPnDbContext,
            assignedSite, DateTime.Now.AddMonths(-1), timeline);
        Assert.That(planRegistration.PlannedStartOfShift5, Is.EqualTo(18 * 60), "precondition");

        planRegistration.PlanText = "7:00-15:00/1";
        await PlanRegistrationHelper.UpdatePlanRegistration(planRegistration, TimePlanningPnDbContext,
            assignedSite, DateTime.Now.AddMonths(-1), timeline);

        Assert.Multiple(() =>
        {
            Assert.That(
                (planRegistration.PlannedStartOfShift1, planRegistration.PlannedEndOfShift1,
                    planRegistration.PlannedBreakOfShift1, planRegistration.PlanHours),
                Is.EqualTo((7 * 60, 15 * 60, 60, 7.0)));
            Assert.That(
                (planRegistration.PlannedStartOfShift2, planRegistration.PlannedEndOfShift2,
                    planRegistration.PlannedBreakOfShift2),
                Is.EqualTo((0, 0, 0)));
            Assert.That(
                (planRegistration.PlannedStartOfShift3, planRegistration.PlannedEndOfShift3,
                    planRegistration.PlannedBreakOfShift3),
                Is.EqualTo((0, 0, 0)));
            Assert.That(
                (planRegistration.PlannedStartOfShift4, planRegistration.PlannedEndOfShift4,
                    planRegistration.PlannedBreakOfShift4),
                Is.EqualTo((0, 0, 0)));
            Assert.That(
                (planRegistration.PlannedStartOfShift5, planRegistration.PlannedEndOfShift5,
                    planRegistration.PlannedBreakOfShift5),
                Is.EqualTo((0, 0, 0)));
        });
    }

    /// <summary>
    /// A comma break is as common as a comma time in Danish-locale sheets, and
    /// the two are resolved by different rules: "0,5" is half an hour of break,
    /// while "7,00" is seven o'clock.
    /// </summary>
    [TestCase("7,00-15,00/0,5", 30, 7.5)]
    [TestCase("7,00-15,00/1,5", 90, 6.5)]
    public async Task UpdatePlanRegistration_CommaDecimalBreak_DerivesTheBreak(
        string planText, int expectedBreak, double expectedHours)
    {
        var (assignedSite, planRegistration, timeline) = await ArrangeAsync(planText);

        await PlanRegistrationHelper.UpdatePlanRegistration(planRegistration, TimePlanningPnDbContext,
            assignedSite, DateTime.Now.AddMonths(-1), timeline);

        Assert.That(
            (planRegistration.PlannedBreakOfShift1, planRegistration.PlanHours),
            Is.EqualTo((expectedBreak, expectedHours)));
    }

    /// <summary>
    /// A shift ending at midnight reads as an end before its start. Summed
    /// naively it contributes a large negative and freezes the day's hours.
    /// </summary>
    [Test]
    public async Task UpdatePlanRegistration_ShiftEndingAtMidnight_CountsForwards()
    {
        var (assignedSite, planRegistration, timeline) = await ArrangeAsync("22:00-0:00");

        await PlanRegistrationHelper.UpdatePlanRegistration(planRegistration, TimePlanningPnDbContext,
            assignedSite, DateTime.Now.AddMonths(-1), timeline);

        Assert.That(planRegistration.PlanHours, Is.EqualTo(2.0));
    }

    /// <summary>
    /// An absence marker clears the shift columns but must NOT touch PlanHours:
    /// the sheet's separate hours column is the authority for such a row, the
    /// caller assigns it from there, and overwriting it would seed the flex
    /// chain from a zero.
    /// </summary>
    [Test]
    public async Task UpdatePlanRegistration_TextReplacedByAnAbsenceMarker_ClearsShiftsButKeepsTheHoursColumn()
    {
        var (assignedSite, planRegistration, timeline) = await ArrangeAsync("7:00-15:00/1");

        await PlanRegistrationHelper.UpdatePlanRegistration(planRegistration, TimePlanningPnDbContext,
            assignedSite, DateTime.Now.AddMonths(-1), timeline);
        Assert.That(planRegistration.PlanHours, Is.EqualTo(7.0), "precondition");

        planRegistration.PlanText = "Ferie";
        planRegistration.PlanHours = 7.4;
        await PlanRegistrationHelper.UpdatePlanRegistration(planRegistration, TimePlanningPnDbContext,
            assignedSite, DateTime.Now.AddMonths(-1), timeline);

        Assert.Multiple(() =>
        {
            Assert.That(
                (planRegistration.PlannedStartOfShift1, planRegistration.PlannedEndOfShift1,
                    planRegistration.PlannedBreakOfShift1),
                Is.EqualTo((0, 0, 0)));
            Assert.That(planRegistration.PlanHours, Is.EqualTo(7.4),
                "the hours column must survive text that is not a shift");
        });
    }

    /// <summary>
    /// The assertions above all read the tracked instance the helper was handed.
    /// This one re-reads the row from the database, because a helper that
    /// mutated the object and never persisted it would satisfy all of them.
    /// </summary>
    [Test]
    public async Task UpdatePlanRegistration_PersistsTheDerivedShiftColumns()
    {
        var (assignedSite, planRegistration, timeline) =
            await ArrangeAsync("6:00-8:00/0.5;9:00-11:00/0.5;12:00-14:00/1");

        await PlanRegistrationHelper.UpdatePlanRegistration(planRegistration, TimePlanningPnDbContext,
            assignedSite, DateTime.Now.AddMonths(-1), timeline);

        var stored = await TimePlanningPnDbContext.PlanRegistrations
            .AsNoTracking()
            .FirstAsync(x => x.Id == planRegistration.Id);

        Assert.Multiple(() =>
        {
            Assert.That(
                (stored.PlannedStartOfShift3, stored.PlannedEndOfShift3, stored.PlannedBreakOfShift3),
                Is.EqualTo((12 * 60, 14 * 60, 60)));
            // 2 x (120 - 30) + (120 - 60).
            Assert.That(stored.PlanHours, Is.EqualTo(4.0));
        });
    }

    /// <summary>
    /// A site on the sheet default, with one future day carrying the plan text
    /// under test. The date is tomorrow so the day is past the day of payment
    /// the tests hand in, which is what opens the sheet-import branch.
    /// </summary>
    private async Task<(AssignedSite, PlanRegistration, OneMinuteModeTimeline)> ArrangeAsync(string planText)
    {
        var assignedSite = new AssignedSite
        {
            SiteId = SiteId,
            WorkflowState = Constants.WorkflowStates.Created,
            CreatedByUserId = 1,
            UpdatedByUserId = 1
        };
        await assignedSite.Create(TimePlanningPnDbContext);
        var planRegistration = new PlanRegistration
        {
            SdkSitId = SiteId,
            Date = DateTime.Now.Date.AddDays(1),
            PlanText = planText,
            CommentOffice = "",
            CommentOfficeAll = "",
            WorkflowState = Constants.WorkflowStates.Created,
            CreatedByUserId = 1,
            UpdatedByUserId = 1
        };
        await planRegistration.Create(TimePlanningPnDbContext);
        var timeline = await OneMinuteModeTimeline.BuildAsync(TimePlanningPnDbContext, assignedSite);

        return (assignedSite, planRegistration, timeline);
    }
}
