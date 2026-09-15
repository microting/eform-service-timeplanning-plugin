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
    /// Tenant 1063 types "7,00-15,00/1" in the sheet. The time parts only split
    /// on '.', ':' and '½', so the comma used to throw inside the helper's
    /// catch-all and leave the day with no planned shift or hours.
    /// </summary>
    [TestCase("7,00-15,00/1")]
    [TestCase("7:00-15:00/1")]
    [TestCase("7.00-15.00/1")]
    public async Task UpdatePlanRegistration_AnyTimeSeparator_DerivesTheShiftAndHours(string planText)
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

        await PlanRegistrationHelper.UpdatePlanRegistration(planRegistration, TimePlanningPnDbContext,
            assignedSite, DateTime.Now.AddMonths(-1), timeline);

        Assert.That(
            (planRegistration.PlannedStartOfShift1, planRegistration.PlannedEndOfShift1,
                planRegistration.PlannedBreakOfShift1, planRegistration.PlanHours),
            Is.EqualTo((7 * 60, 15 * 60, 60, 7.0)));
    }
}
