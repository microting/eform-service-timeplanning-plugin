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
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.TimePlanningBase.Infrastructure.Data.Entities;
using Microting.TimePlanningBase.Infrastructure.Helpers;
using NUnit.Framework;
using ServiceTimePlanningPlugin.Infrastructure.Helpers;
using ServiceTimePlanningPlugin.Scheduler.Jobs;

namespace ServiceTimePlanningPlugin.Integration.Test;

/// <summary>
/// Kanban #5489 step 2: a worker's PlanPattern governs PlanText/PlanHours on
/// PlanRegistration rows, the nightly run keeps 180 days of rows ahead for
/// patterned workers, and the Google Sheet pull can be retired by a setting.
/// The invariant tests pin today's behaviour for workers without a pattern.
/// </summary>
[TestFixture]
public class PlanPatternTests : TestBaseSetup
{
    private const int SiteId = 300000;

    /// <summary>The Monday of next week: future, so past any day of payment used here.</summary>
    private static DateTime NextMonday()
    {
        var today = DateTime.Today;
        var daysToMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(daysToMonday == 0 ? 7 : daysToMonday);
    }

    private static DateTime DayOfPayment => DateTime.Now.AddMonths(-1);

    /// <summary>Template worker: 8h, 08:00-16:00 with a 30 min break, every day.</summary>
    private async Task<AssignedSite> CreateTemplateSiteAsync(int siteId = SiteId, bool sheet = false)
    {
        var site = new AssignedSite
        {
            SiteId = siteId,
            UseGoogleSheetAsDefault = sheet,
            UseOnlyPlanHours = false,
            MondayPlanHours = 480, TuesdayPlanHours = 480, WednesdayPlanHours = 480,
            ThursdayPlanHours = 480, FridayPlanHours = 480, SaturdayPlanHours = 480, SundayPlanHours = 480,
            StartMonday = 480, EndMonday = 960, BreakMonday = 30,
            StartTuesday = 480, EndTuesday = 960, BreakTuesday = 30,
            StartWednesday = 480, EndWednesday = 960, BreakWednesday = 30,
            StartThursday = 480, EndThursday = 960, BreakThursday = 30,
            StartFriday = 480, EndFriday = 960, BreakFriday = 30,
            StartSaturday = 480, EndSaturday = 960, BreakSaturday = 30,
            StartSunday = 480, EndSunday = 960, BreakSunday = 30,
            WorkflowState = Constants.WorkflowStates.Created,
            CreatedByUserId = 1,
            UpdatedByUserId = 1
        };
        await site.Create(TimePlanningPnDbContext);
        return site;
    }

    private async Task<PlanRegistration> CreateRowAsync(DateTime date, string planText = "",
        int siteId = SiteId, bool planChangedByAdmin = false, double planHours = 0)
    {
        var row = new PlanRegistration
        {
            SdkSitId = siteId,
            Date = date,
            PlanText = planText,
            PlanHours = planHours,
            PlanChangedByAdmin = planChangedByAdmin,
            CommentOffice = "",
            CommentOfficeAll = "",
            WorkflowState = Constants.WorkflowStates.Created,
            CreatedByUserId = 1,
            UpdatedByUserId = 1
        };
        await row.Create(TimePlanningPnDbContext);
        return row;
    }

    /// <summary>
    /// Two-week rotation anchored on <paramref name="anchor"/>: week 0 Monday
    /// 06:00-14:00/0.5, week 1 Monday 09:00-17:00/1, week 0 Tuesday empty.
    /// </summary>
    private async Task<PlanPattern> CreatePatternAsync(AssignedSite site, DateTime anchor,
        DateTime? validFrom = null, int rotationWeeks = 2, string workflowState = Constants.WorkflowStates.Created)
    {
        var pattern = new PlanPattern
        {
            AssignedSiteId = site.Id,
            RotationWeeks = rotationWeeks,
            AnchorDate = anchor,
            ValidFrom = validFrom ?? anchor,
            WorkflowState = workflowState,
            CreatedByUserId = 1,
            UpdatedByUserId = 1
        };
        await pattern.Create(TimePlanningPnDbContext);
        if (workflowState == Constants.WorkflowStates.Removed)
        {
            // Create always stores Created; Delete is the soft delete.
            await pattern.Delete(TimePlanningPnDbContext);
        }
        foreach (var (week, dow, text) in new[] { (0, 0, "6:00-14:00/0.5"), (1, 0, "9:00-17:00/1"), (0, 1, "") })
        {
            await new PlanPatternDay
            {
                PlanPatternId = pattern.Id,
                WeekIndex = week,
                DayOfWeek = dow,
                PlanText = text,
                WorkflowState = Constants.WorkflowStates.Created,
                CreatedByUserId = 1,
                UpdatedByUserId = 1
            }.Create(TimePlanningPnDbContext);
        }
        return pattern;
    }

    private async Task<PlanRegistration> RunAsync(PlanRegistration row, AssignedSite site,
        DateTime? dayOfPayment = null)
    {
        var timeline = await OneMinuteModeTimeline.BuildAsync(TimePlanningPnDbContext, site);
        await PlanRegistrationHelper.UpdatePlanRegistration(row, TimePlanningPnDbContext, site,
            dayOfPayment ?? DayOfPayment, timeline);
        return await TimePlanningPnDbContext.PlanRegistrations.AsNoTracking().FirstAsync(x => x.Id == row.Id);
    }

    private static (string, double, int, int, int, int, int, int) Shape(PlanRegistration r) =>
        (r.PlanText, r.PlanHours, r.PlannedStartOfShift1, r.PlannedEndOfShift1, r.PlannedBreakOfShift1,
            r.PlannedStartOfShift2, r.PlannedEndOfShift2, r.PlannedBreakOfShift2);

    // ---------- invariant: no pattern ----------

    [Test]
    public async Task NoPattern_TemplateWorker_GetsTheWeekdayTemplate()
    {
        var site = await CreateTemplateSiteAsync();
        var row = await CreateRowAsync(NextMonday(), "untouched");

        var stored = await RunAsync(row, site);

        Assert.That(Shape(stored), Is.EqualTo(("untouched", 8.0, 480, 960, 30, 0, 0, 0)));
    }

    [Test]
    public async Task NoPattern_SheetWorker_ParsesTheSheetPlanText()
    {
        var site = await CreateTemplateSiteAsync(sheet: true);
        var row = await CreateRowAsync(NextMonday(), "7:00-15:00/1");

        var stored = await RunAsync(row, site);

        Assert.That(Shape(stored), Is.EqualTo(("7:00-15:00/1", 7.0, 420, 900, 60, 0, 0, 0)));
    }

    [Test]
    public async Task RemovedPattern_IsIgnored()
    {
        var site = await CreateTemplateSiteAsync();
        await CreatePatternAsync(site, NextMonday(), workflowState: Constants.WorkflowStates.Removed);
        var row = await CreateRowAsync(NextMonday(), "untouched");

        var stored = await RunAsync(row, site);

        Assert.That(Shape(stored), Is.EqualTo(("untouched", 8.0, 480, 960, 30, 0, 0, 0)));
    }

    [Test]
    public async Task NullAssignedSite_DoesNotThrow_AndLeavesTheRowAlone()
    {
        var row = await CreateRowAsync(NextMonday(), "untouched", planHours: 3);
        var timeline = await OneMinuteModeTimeline.BuildAsync(TimePlanningPnDbContext, null);

        await PlanRegistrationHelper.UpdatePlanRegistration(row, TimePlanningPnDbContext, null!,
            DayOfPayment, timeline);

        var stored = await TimePlanningPnDbContext.PlanRegistrations.AsNoTracking().FirstAsync(x => x.Id == row.Id);
        Assert.That((stored.PlanText, stored.PlanHours), Is.EqualTo(("untouched", 3.0)));
    }

    // ---------- pattern governs ----------

    [Test]
    public async Task Pattern_Week0Monday_WritesTheWeek0Plan_AndClearsOtherShifts()
    {
        var anchor = NextMonday();
        var site = await CreateTemplateSiteAsync(sheet: true);
        await CreatePatternAsync(site, anchor);
        var row = await CreateRowAsync(anchor, "7:00-9:00;10:00-12:00");

        var stored = await RunAsync(row, site);

        Assert.That(Shape(stored), Is.EqualTo(("6:00-14:00/0.5", 7.5, 360, 840, 30, 0, 0, 0)));
    }

    [Test]
    public async Task Pattern_Week1Monday_PicksTheSecondRotationWeek()
    {
        var anchor = NextMonday();
        var site = await CreateTemplateSiteAsync();
        await CreatePatternAsync(site, anchor);
        var row = await CreateRowAsync(anchor.AddDays(7));

        var stored = await RunAsync(row, site);

        Assert.That(Shape(stored), Is.EqualTo(("9:00-17:00/1", 7.0, 540, 1020, 60, 0, 0, 0)));
    }

    [Test]
    public async Task Pattern_RotationWraps_Week2IsWeek0Again()
    {
        var anchor = NextMonday();
        var site = await CreateTemplateSiteAsync();
        await CreatePatternAsync(site, anchor);
        var row = await CreateRowAsync(anchor.AddDays(14));

        var stored = await RunAsync(row, site);

        Assert.That(stored.PlanText, Is.EqualTo("6:00-14:00/0.5"));
    }

    [Test]
    public async Task Pattern_DateBeforeValidFrom_FallsThroughToTheTemplate()
    {
        var anchor = NextMonday();
        var site = await CreateTemplateSiteAsync();
        await CreatePatternAsync(site, anchor, validFrom: anchor.AddDays(7));
        var row = await CreateRowAsync(anchor, "untouched");

        var stored = await RunAsync(row, site);

        Assert.That(Shape(stored), Is.EqualTo(("untouched", 8.0, 480, 960, 30, 0, 0, 0)));
    }

    [Test]
    public async Task Pattern_EmptyDay_MeansNoPlan()
    {
        var anchor = NextMonday();
        var site = await CreateTemplateSiteAsync();
        await CreatePatternAsync(site, anchor);
        // Week 0 Tuesday has PlanText "".
        var row = await CreateRowAsync(anchor.AddDays(1), "7:00-15:00", planHours: 8);

        var stored = await RunAsync(row, site);

        Assert.That(Shape(stored), Is.EqualTo(("", 0.0, 0, 0, 0, 0, 0, 0)));
    }

    [Test]
    public async Task Pattern_DayWithNoPatternDayRow_MeansNoPlan()
    {
        var anchor = NextMonday();
        var site = await CreateTemplateSiteAsync();
        await CreatePatternAsync(site, anchor);
        // Week 0 Wednesday has no PlanPatternDay at all.
        var row = await CreateRowAsync(anchor.AddDays(2), "x", planHours: 8);

        var stored = await RunAsync(row, site);

        Assert.That((stored.PlanText, stored.PlanHours), Is.EqualTo(("", 0.0)));
    }

    [Test]
    public async Task Pattern_PlanHoursChange_RecomputesFlex()
    {
        var anchor = NextMonday();
        var site = await CreateTemplateSiteAsync();
        await CreatePatternAsync(site, anchor);
        var row = await CreateRowAsync(anchor);

        var stored = await RunAsync(row, site);

        // No hours worked, 7.5 planned: the day is 7.5h short.
        Assert.That(stored.Flex, Is.EqualTo(-7.5));
    }

    [Test]
    public async Task Pattern_PreloadedPattern_IsUsedWithoutLookup()
    {
        var anchor = NextMonday();
        var site = await CreateTemplateSiteAsync();
        await CreatePatternAsync(site, anchor);
        var row = await CreateRowAsync(anchor.AddDays(7));
        var preloaded = await WorkerPlanPattern.LoadAsync(TimePlanningPnDbContext, site);
        var timeline = await OneMinuteModeTimeline.BuildAsync(TimePlanningPnDbContext, site);

        await PlanRegistrationHelper.UpdatePlanRegistration(row, TimePlanningPnDbContext, site,
            DayOfPayment, timeline, preloaded);

        var stored = await TimePlanningPnDbContext.PlanRegistrations.AsNoTracking().FirstAsync(x => x.Id == row.Id);
        Assert.That(stored.PlanText, Is.EqualTo("9:00-17:00/1"));
    }

    // ---------- skip rules ----------

    [Test]
    public async Task Pattern_PlanChangedByAdmin_RowIsUntouched()
    {
        var anchor = NextMonday();
        var site = await CreateTemplateSiteAsync();
        await CreatePatternAsync(site, anchor);
        var row = await CreateRowAsync(anchor, "admin", planChangedByAdmin: true, planHours: 2);

        var stored = await RunAsync(row, site);

        Assert.That((stored.PlanText, stored.PlanHours, stored.Version), Is.EqualTo(("admin", 2.0, row.Version)));
    }

    [Test]
    public async Task Pattern_RowOnOrBeforeDayOfPayment_IsUntouched()
    {
        var anchor = NextMonday();
        var site = await CreateTemplateSiteAsync();
        await CreatePatternAsync(site, anchor);
        var row = await CreateRowAsync(anchor, "old", planHours: 2);

        var stored = await RunAsync(row, site, dayOfPayment: anchor);

        Assert.That((stored.PlanText, stored.PlanHours, stored.Version), Is.EqualTo(("old", 2.0, row.Version)));
    }

    [Test]
    public async Task Pattern_AcceptedHandoverDay_IsUntouched_AndTheTemplateDoesNotRunEither()
    {
        var anchor = NextMonday();
        var site = await CreateTemplateSiteAsync();
        await CreatePatternAsync(site, anchor);
        var row = await CreateRowAsync(anchor, "handed over", planHours: 3);
        row.ContentHandoverRequestId = 42;
        await row.Update(TimePlanningPnDbContext);
        var versionBefore = (await TimePlanningPnDbContext.PlanRegistrations.AsNoTracking()
            .FirstAsync(x => x.Id == row.Id)).Version;

        var stored = await RunAsync(row, site);

        // The template would write 8h; the pattern would write 7.5h.
        Assert.That((stored.PlanText, stored.PlanHours, stored.PlannedStartOfShift1, stored.Version),
            Is.EqualTo(("handed over", 3.0, 0, versionBefore)));
    }

    // ---------- robustness ----------

    [Test]
    public async Task CorruptPattern_FallsBackToTheTemplate_WithoutThrowing()
    {
        var anchor = NextMonday();
        var site = await CreateTemplateSiteAsync();
        await CreatePatternAsync(site, anchor, rotationWeeks: 0);
        var row = await CreateRowAsync(anchor, "untouched");

        var stored = await RunAsync(row, site);

        Assert.That(Shape(stored), Is.EqualTo(("untouched", 8.0, 480, 960, 30, 0, 0, 0)));
    }

    // ---------- nightly horizon ----------

    [Test]
    public async Task Nightly_PatternedWorker_GetsRowsThroughToday180_WithoutDuplicates()
    {
        var today = DateTime.Today;
        var anchor = NextMonday().AddDays(-14);
        var site = await CreateTemplateSiteAsync();
        await CreatePatternAsync(site, anchor, validFrom: anchor);
        // An existing row inside the horizon must not be duplicated.
        await CreateRowAsync(today.AddDays(3));

        await new SearchListJob(DbContextHelper, null!).RecalculateRecentRegistrations();

        var rows = await TimePlanningPnDbContext.PlanRegistrations.AsNoTracking()
            .Where(x => x.SdkSitId == SiteId && x.WorkflowState != Constants.WorkflowStates.Removed)
            .OrderBy(x => x.Date).ToListAsync();
        var mondayW1 = rows.Single(x => x.Date == NextMonday().AddDays(7));

        Assert.Multiple(() =>
        {
            Assert.That(rows.Count, Is.EqualTo(181), "today .. today+180 inclusive, one row per day");
            Assert.That(rows.First().Date, Is.EqualTo(today));
            Assert.That(rows.Last().Date, Is.EqualTo(today.AddDays(180)));
            Assert.That(rows.GroupBy(x => x.Date).All(g => g.Count() == 1), Is.True);
            // anchor is two weeks before next Monday, so next Monday + 7 is week 1.
            Assert.That((mondayW1.PlanText, mondayW1.PlanHours), Is.EqualTo(("9:00-17:00/1", 7.0)));
        });
    }

    [Test]
    public async Task Nightly_FutureValidFrom_StartsTheHorizonThere()
    {
        var anchor = NextMonday();
        var site = await CreateTemplateSiteAsync();
        await CreatePatternAsync(site, anchor, validFrom: anchor.AddDays(7));

        await new SearchListJob(DbContextHelper, null!).RecalculateRecentRegistrations();

        var first = await TimePlanningPnDbContext.PlanRegistrations.AsNoTracking()
            .Where(x => x.SdkSitId == SiteId).MinAsync(x => x.Date);
        Assert.That(first, Is.EqualTo(anchor.AddDays(7)));
    }

    /// <summary>
    /// A resigned worker gets rows only through ResignedAtDate; none with no
    /// date or a date before the horizon starts. resignedOffset null = no date.
    /// </summary>
    [TestCase(10, 11)]
    [TestCase(null, 0)]
    [TestCase(-1, 0)]
    public async Task Horizon_ResignedWorker_StopsAtTheResignationDate(int? resignedOffset, int expectedRows)
    {
        var today = DateTime.Today;
        var site = await CreateTemplateSiteAsync();
        site.Resigned = true;
        site.ResignedAtDate = resignedOffset == null ? null : today.AddDays(resignedOffset.Value);
        await site.Update(TimePlanningPnDbContext);
        await CreatePatternAsync(site, NextMonday().AddDays(-14));
        var pattern = await WorkerPlanPattern.LoadAsync(TimePlanningPnDbContext, site);
        var timeline = await OneMinuteModeTimeline.BuildAsync(TimePlanningPnDbContext, site);

        await SearchListJob.ExtendPlanPatternHorizonAsync(TimePlanningPnDbContext, site, pattern, timeline,
            null, today, today.AddMonths(-1));

        var rows = await TimePlanningPnDbContext.PlanRegistrations.AsNoTracking()
            .Where(x => x.SdkSitId == SiteId).Select(x => x.Date).ToListAsync();
        Assert.That(rows.Count, Is.EqualTo(expectedRows));
        if (expectedRows > 0)
        {
            Assert.That(rows.Max(), Is.EqualTo(today.AddDays(resignedOffset!.Value)));
        }
    }

    [Test]
    public async Task Nightly_WorkerWithoutPattern_GetsNoNewRows()
    {
        var site = await CreateTemplateSiteAsync();
        await CreateRowAsync(DateTime.Today.AddDays(2));

        await new SearchListJob(DbContextHelper, null!).RecalculateRecentRegistrations();

        var count = await TimePlanningPnDbContext.PlanRegistrations.CountAsync(x => x.SdkSitId == SiteId);
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task Nightly_CorruptPattern_IsSkipped_AndOtherWorkersStillGetRows()
    {
        var anchor = NextMonday();
        var bad = await CreateTemplateSiteAsync(SiteId);
        await CreatePatternAsync(bad, anchor, rotationWeeks: 9);
        var good = await CreateTemplateSiteAsync(SiteId + 1);
        await CreatePatternAsync(good, anchor);

        await new SearchListJob(DbContextHelper, null!).RecalculateRecentRegistrations();

        var badCount = await TimePlanningPnDbContext.PlanRegistrations.CountAsync(x => x.SdkSitId == SiteId);
        var goodCount = await TimePlanningPnDbContext.PlanRegistrations.CountAsync(x => x.SdkSitId == SiteId + 1);
        Assert.That((badCount, goodCount > 0), Is.EqualTo((0, true)));
    }

    // ---------- sheet import skip rule ----------

    private async Task<int[]> SkippedAsync(params AssignedSite[] sites) =>
        (await SearchListJob.SheetImportSkippedSiteIdsAsync(TimePlanningPnDbContext, sites)).OrderBy(x => x).ToArray();

    [Test]
    public async Task SheetSkip_ActivePattern_IsSkipped()
    {
        var site = await CreateTemplateSiteAsync(sheet: true);
        await CreatePatternAsync(site, NextMonday());

        Assert.That(await SkippedAsync(site), Is.EqualTo(new[] { SiteId }));
    }

    [Test]
    public async Task SheetSkip_DeletedPattern_FlagFalse_IsSkipped()
    {
        var site = await CreateTemplateSiteAsync(sheet: false);
        await CreatePatternAsync(site, NextMonday(), workflowState: Constants.WorkflowStates.Removed);

        Assert.That(await SkippedAsync(site), Is.EqualTo(new[] { SiteId }));
    }

    [Test]
    public async Task SheetSkip_DeletedPattern_FlagTrue_IsImported()
    {
        var site = await CreateTemplateSiteAsync(sheet: true);
        await CreatePatternAsync(site, NextMonday(), workflowState: Constants.WorkflowStates.Removed);

        Assert.That(await SkippedAsync(site), Is.Empty);
    }

    [Test]
    public async Task SheetSkip_NeverPatterned_IsImportedWhateverTheFlag()
    {
        var sheet = await CreateTemplateSiteAsync(SiteId, sheet: true);
        var template = await CreateTemplateSiteAsync(SiteId + 1, sheet: false);
        // A pattern on someone else must not skip these two.
        var other = await CreateTemplateSiteAsync(SiteId + 2, sheet: true);
        await CreatePatternAsync(other, NextMonday());

        Assert.That(await SkippedAsync(sheet, template, other), Is.EqualTo(new[] { SiteId + 2 }));
    }

    // ---------- GoogleSheetRetired ----------

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("nonsense", false)]
    [TestCase("false", false)]
    [TestCase("true", true)]
    [TestCase("True", true)]
    [TestCase("TRUE", true)]
    [TestCase(" true ", true)]
    [TestCase("1", true)]
    [TestCase("0", false)]
    public async Task IsGoogleSheetRetired_ReadsTheSetting(string value, bool expected)
    {
        if (value != null)
        {
            TimePlanningPnDbContext.Add(new Microting.eFormApi.BasePn.Infrastructure.Database.Entities.PluginConfigurationValue
            {
                Name = "TimePlanningBaseSettings:GoogleSheetRetired",
                Value = value,
                WorkflowState = Constants.WorkflowStates.Created,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                CreatedByUserId = 1,
                UpdatedByUserId = 1
            });
            await TimePlanningPnDbContext.SaveChangesAsync();
        }

        Assert.That(await SearchListJob.IsGoogleSheetRetired(TimePlanningPnDbContext), Is.EqualTo(expected));
    }
}
