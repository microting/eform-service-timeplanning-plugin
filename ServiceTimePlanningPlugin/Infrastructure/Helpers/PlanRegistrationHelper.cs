using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.JavaScript;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microting.eForm.Infrastructure.Constants;
using Microting.eForm.Infrastructure.Data.Entities;
using Microting.eFormApi.BasePn.Infrastructure.Helpers.PluginDbOptions;
using Microting.TimePlanningBase.Infrastructure.Data;
using Microting.TimePlanningBase.Infrastructure.Data.Entities;
using Microting.TimePlanningBase.Infrastructure.Data.Models;
using Sentry;

namespace ServiceTimePlanningPlugin.Infrastructure.Helpers;

public static class PlanRegistrationHelper
{
    public static async Task<PlanRegistration> UpdatePlanRegistration(
        PlanRegistration planRegistration,
        TimePlanningPnDbContext dbContext,
        AssignedSite dbAssignedSite,
        DateTime dayOfPayment
        )
    {
        var tainted = false;
        // foreach (var plan in planningsInPeriod)
        // {
            // var planRegistration = await dbContext.PlanRegistrations.AsTracking().FirstAsync(x => x.Id == planRegistrationId);
            // var midnight = new DateTime(planRegistration.Date.Year, planRegistration.Date.Month,
            //     planRegistration.Date.Day, 0, 0, 0);
            // var toDay = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, 0, 0, 0);
            // var dayOfPayment = toDay.Day >= settingsDayOfPayment
                // ? new DateTime(DateTime.Now.Year, DateTime.Now.Month, settingsDayOfPayment, 0, 0, 0)
                // : new DateTime(DateTime.Now.Year, DateTime.Now.Month - 1, settingsDayOfPayment, 0, 0, 0);

            try
            {
                if (dbAssignedSite.UseGoogleSheetAsDefault)
                {
                    if (!string.IsNullOrEmpty(planRegistration.PlanText))
                    {
                        if (planRegistration.Date > dayOfPayment && !planRegistration.PlanChangedByAdmin)
                        {
                            var splitList = planRegistration.PlanText.Split(';');
                            var firsSplit = splitList[0];

                            var regex = new Regex(@"(.*)-(.*)\/(.*)");
                            var match = regex.Match(firsSplit);
                            if (match.Captures.Count == 0)
                            {
                                regex = new Regex(@"(.*)-(.*)");
                                match = regex.Match(firsSplit);

                                if (match.Captures.Count == 1)
                                {
                                    var firstPart = match.Groups[1].Value;
                                    var firstPartSplit =
                                        firstPart.Split(['.', ':', '½'], StringSplitOptions.RemoveEmptyEntries);
                                    var firstPartHours = int.Parse(firstPartSplit[0]);
                                    var firstPartMinutes = firstPartSplit.Length > 1 ? int.Parse(firstPartSplit[1]) : 0;
                                    var firstPartTotalMinutes = firstPartHours * 60 + firstPartMinutes;
                                    var secondPart = match.Groups[2].Value;
                                    var secondPartSplit =
                                        secondPart.Split(['.', ':', '½'], StringSplitOptions.RemoveEmptyEntries);
                                    var secondPartHours = int.Parse(secondPartSplit[0]);
                                    var secondPartMinutes =
                                        secondPartSplit.Length > 1 ? int.Parse(secondPartSplit[1]) : 0;
                                    var secondPartTotalMinutes = secondPartHours * 60 + secondPartMinutes;
                                    planRegistration.PlannedStartOfShift1 = firstPartTotalMinutes;
                                    planRegistration.PlannedEndOfShift1 = secondPartTotalMinutes;

                                    if (match.Groups.Count == 4)
                                    {
                                        var breakPart = match.Groups[3].Value.Replace(",", ".").Trim();

                                        var breakPartMinutes = BreakTimeCalculator(breakPart);

                                        planRegistration.PlannedBreakOfShift1 = breakPartMinutes;
                                    }
                                    else
                                    {
                                        planRegistration.PlannedBreakOfShift1 = 0;
                                    }
                                }
                            }

                            if (match.Captures.Count == 1)
                            {
                                var firstPart = match.Groups[1].Value;
                                var firstPartSplit =
                                    firstPart.Split(['.', ':', '½'], StringSplitOptions.RemoveEmptyEntries);
                                var firstPartHours = int.Parse(firstPartSplit[0]);
                                var firstPartMinutes = firstPartSplit.Length > 1 ? int.Parse(firstPartSplit[1]) : 0;
                                var firstPartTotalMinutes = firstPartHours * 60 + firstPartMinutes;
                                var secondPart = match.Groups[2].Value;
                                var secondPartSplit =
                                    secondPart.Split(['.', ':', '½'], StringSplitOptions.RemoveEmptyEntries);
                                var secondPartHours = int.Parse(secondPartSplit[0]);
                                var secondPartMinutes =
                                    secondPartSplit.Length > 1 ? int.Parse(secondPartSplit[1]) : 0;
                                var secondPartTotalMinutes = secondPartHours * 60 + secondPartMinutes;
                                planRegistration.PlannedStartOfShift1 = firstPartTotalMinutes;
                                planRegistration.PlannedEndOfShift1 = secondPartTotalMinutes;

                                if (match.Groups.Count == 4)
                                {
                                    var breakPart = match.Groups[3].Value.Replace(",", ".").Trim();

                                    var breakPartMinutes = BreakTimeCalculator(breakPart);

                                    planRegistration.PlannedBreakOfShift1 = breakPartMinutes;
                                }
                                else
                                {
                                    planRegistration.PlannedBreakOfShift1 = 0;
                                }
                            }

                            if (splitList.Length > 1)
                            {
                                var secondSplit = splitList[1];
                                regex = new Regex(@"(.*)-(.*)\/(.*)");
                                match = regex.Match(secondSplit);
                                if (match.Captures.Count == 0)
                                {
                                    regex = new Regex(@"(.*)-(.*)");
                                    match = regex.Match(secondSplit);

                                    if (match.Captures.Count == 1)
                                    {
                                        var firstPart = match.Groups[1].Value;
                                        var firstPartSplit =
                                            firstPart.Split(['.', ':', '½'], StringSplitOptions.RemoveEmptyEntries);
                                        var firstPartHours = int.Parse(firstPartSplit[0]);
                                        var firstPartMinutes =
                                            firstPartSplit.Length > 1 ? int.Parse(firstPartSplit[1]) : 0;
                                        var firstPartTotalMinutes = firstPartHours * 60 + firstPartMinutes;
                                        var secondPart = match.Groups[2].Value;
                                        var secondPartSplit =
                                            secondPart.Split(['.', ':', '½'], StringSplitOptions.RemoveEmptyEntries);
                                        var secondPartHours = int.Parse(secondPartSplit[0]);
                                        var secondPartMinutes =
                                            secondPartSplit.Length > 1 ? int.Parse(secondPartSplit[1]) : 0;
                                        var secondPartTotalMinutes = secondPartHours * 60 + secondPartMinutes;
                                        planRegistration.PlannedStartOfShift2 = firstPartTotalMinutes;
                                        planRegistration.PlannedEndOfShift2 = secondPartTotalMinutes;

                                        if (match.Groups.Count == 4)
                                        {
                                            var breakPart = match.Groups[3].Value.Replace(",", ".").Trim();

                                            var breakPartMinutes = BreakTimeCalculator(breakPart);

                                            planRegistration.PlannedBreakOfShift2 = breakPartMinutes;
                                        }
                                        else
                                        {
                                            planRegistration.PlannedBreakOfShift2 = 0;
                                        }
                                    }
                                }

                                if (match.Captures.Count == 1)
                                {
                                    var firstPart = match.Groups[1].Value;
                                    var firstPartSplit =
                                        firstPart.Split(['.', ':', '½'], StringSplitOptions.RemoveEmptyEntries);
                                    var firstPartHours = int.Parse(firstPartSplit[0]);
                                    var firstPartMinutes = firstPartSplit.Length > 1 ? int.Parse(firstPartSplit[1]) : 0;
                                    var firstPartTotalMinutes = firstPartHours * 60 + firstPartMinutes;
                                    var secondPart = match.Groups[2].Value;
                                    var secondPartSplit =
                                        secondPart.Split(['.', ':', '½'], StringSplitOptions.RemoveEmptyEntries);
                                    var secondPartHours = int.Parse(secondPartSplit[0]);
                                    var secondPartMinutes =
                                        secondPartSplit.Length > 1 ? int.Parse(secondPartSplit[1]) : 0;
                                    var secondPartTotalMinutes = secondPartHours * 60 + secondPartMinutes;
                                    planRegistration.PlannedStartOfShift2 = firstPartTotalMinutes;
                                    planRegistration.PlannedEndOfShift2 = secondPartTotalMinutes;

                                    if (match.Groups.Count == 4)
                                    {
                                        var breakPart = match.Groups[3].Value.Replace(",", ".").Trim();

                                        var breakPartMinutes = BreakTimeCalculator(breakPart);

                                        planRegistration.PlannedBreakOfShift2 = breakPartMinutes;
                                    }
                                    else
                                    {
                                        planRegistration.PlannedBreakOfShift2 = 0;
                                    }
                                }
                            }

                            if (splitList.Length > 2)
                            {
                                var thirdSplit = splitList[2];
                                regex = new Regex(@"(.*)-(.*)\/(.*)");
                                match = regex.Match(thirdSplit);
                                if (match.Captures.Count == 0)
                                {
                                    regex = new Regex(@"(.*)-(.*)");
                                    match = regex.Match(thirdSplit);

                                    if (match.Captures.Count == 1)
                                    {
                                        var firstPart = match.Groups[1].Value;
                                        var firstPartSplit =
                                            firstPart.Split(['.', ':', '½'], StringSplitOptions.RemoveEmptyEntries);
                                        var firstPartHours = int.Parse(firstPartSplit[0]);
                                        var firstPartMinutes =
                                            firstPartSplit.Length > 1 ? int.Parse(firstPartSplit[1]) : 0;
                                        var firstPartTotalMinutes = firstPartHours * 60 + firstPartMinutes;
                                        var secondPart = match.Groups[2].Value;
                                        var secondPartSplit =
                                            secondPart.Split(['.', ':', '½'], StringSplitOptions.RemoveEmptyEntries);
                                        var secondPartHours = int.Parse(secondPartSplit[0]);
                                        var secondPartMinutes =
                                            secondPartSplit.Length > 1 ? int.Parse(secondPartSplit[1]) : 0;
                                        var secondPartTotalMinutes = secondPartHours * 60 + secondPartMinutes;
                                        planRegistration.PlannedStartOfShift3 = firstPartTotalMinutes;
                                        planRegistration.PlannedEndOfShift3 = secondPartTotalMinutes;

                                        if (match.Groups.Count == 4)
                                        {
                                            var breakPart = match.Groups[3].Value.Replace(",", ".").Trim();
                                            var breakPartMinutes = BreakTimeCalculator(breakPart);

                                            planRegistration.PlannedBreakOfShift3 = breakPartMinutes;
                                        }
                                        else
                                        {
                                            planRegistration.PlannedBreakOfShift3 = 0;
                                        }
                                    }
                                }
                            }

                            if (splitList.Length > 3)
                            {
                                var fourthSplit = splitList[3];
                                regex = new Regex(@"(.*)-(.*)\/(.*)");
                                match = regex.Match(fourthSplit);
                                if (match.Captures.Count == 0)
                                {
                                    regex = new Regex(@"(.*)-(.*)");
                                    match = regex.Match(fourthSplit);

                                    if (match.Captures.Count == 1)
                                    {
                                        var firstPart = match.Groups[1].Value;
                                        var firstPartSplit =
                                            firstPart.Split(['.', ':', '½'], StringSplitOptions.RemoveEmptyEntries);
                                        var firstPartHours = int.Parse(firstPartSplit[0]);
                                        var firstPartMinutes =
                                            firstPartSplit.Length > 1 ? int.Parse(firstPartSplit[1]) : 0;
                                        var firstPartTotalMinutes = firstPartHours * 60 + firstPartMinutes;
                                        var secondPart = match.Groups[2].Value;
                                        var secondPartSplit =
                                            secondPart.Split(['.', ':', '½'], StringSplitOptions.RemoveEmptyEntries);
                                        var secondPartHours = int.Parse(secondPartSplit[0]);
                                        var secondPartMinutes =
                                            secondPartSplit.Length > 1 ? int.Parse(secondPartSplit[1]) : 0;
                                        var secondPartTotalMinutes = secondPartHours * 60 + secondPartMinutes;
                                        planRegistration.PlannedStartOfShift4 = firstPartTotalMinutes;
                                        planRegistration.PlannedEndOfShift4 = secondPartTotalMinutes;

                                        if (match.Groups.Count == 4)
                                        {
                                            var breakPart = match.Groups[3].Value.Replace(",", ".").Trim();
                                            var breakPartMinutes = BreakTimeCalculator(breakPart);

                                            planRegistration.PlannedBreakOfShift4 = breakPartMinutes;
                                        }
                                        else
                                        {
                                            planRegistration.PlannedBreakOfShift4 = 0;
                                        }
                                    }
                                }
                            }

                            if (splitList.Length > 4)
                            {
                                var fifthSplit = splitList[4];
                                regex = new Regex(@"(.*)-(.*)\/(.*)");
                                match = regex.Match(fifthSplit);
                                if (match.Captures.Count == 0)
                                {
                                    regex = new Regex(@"(.*)-(.*)");
                                    match = regex.Match(fifthSplit);

                                    if (match.Captures.Count == 1)
                                    {
                                        var firstPart = match.Groups[1].Value;
                                        var firstPartSplit =
                                            firstPart.Split(['.', ':', '½'], StringSplitOptions.RemoveEmptyEntries);
                                        var firstPartHours = int.Parse(firstPartSplit[0]);
                                        var firstPartMinutes =
                                            firstPartSplit.Length > 1 ? int.Parse(firstPartSplit[1]) : 0;
                                        var firstPartTotalMinutes = firstPartHours * 60 + firstPartMinutes;
                                        var secondPart = match.Groups[2].Value;
                                        var secondPartSplit =
                                            secondPart.Split(['.', ':', '½'], StringSplitOptions.RemoveEmptyEntries);
                                        var secondPartHours = int.Parse(secondPartSplit[0]);
                                        var secondPartMinutes =
                                            secondPartSplit.Length > 1 ? int.Parse(secondPartSplit[1]) : 0;
                                        var secondPartTotalMinutes = secondPartHours * 60 + secondPartMinutes;
                                        planRegistration.PlannedStartOfShift5 = firstPartTotalMinutes;
                                        planRegistration.PlannedEndOfShift5 = secondPartTotalMinutes;

                                        if (match.Groups.Count == 4)
                                        {
                                            var breakPart = match.Groups[3].Value.Replace(",", ".").Trim();

                                            var breakPartMinutes = BreakTimeCalculator(breakPart);

                                            planRegistration.PlannedBreakOfShift5 = breakPartMinutes;
                                        }
                                        else
                                        {
                                            planRegistration.PlannedBreakOfShift5 = 0;
                                        }
                                    }
                                }
                            }

                            var calculatedPlanHoursInMinutes = 0;
                            var originalPlanHours = planRegistration.PlanHours;
                            if (planRegistration.PlannedStartOfShift1 != 0 && planRegistration.PlannedEndOfShift1 != 0)
                            {
                                calculatedPlanHoursInMinutes += planRegistration.PlannedEndOfShift1 -
                                                                planRegistration.PlannedStartOfShift1 -
                                                                planRegistration.PlannedBreakOfShift1;
                                planRegistration.PlanHours = calculatedPlanHoursInMinutes / 60.0;
                            }

                            if (planRegistration.PlannedStartOfShift2 != 0 && planRegistration.PlannedEndOfShift2 != 0)
                            {
                                calculatedPlanHoursInMinutes += planRegistration.PlannedEndOfShift2 -
                                                                planRegistration.PlannedStartOfShift2 -
                                                                planRegistration.PlannedBreakOfShift2;
                                planRegistration.PlanHours = calculatedPlanHoursInMinutes / 60.0;
                            }

                            if (planRegistration.PlannedStartOfShift3 != 0 && planRegistration.PlannedEndOfShift3 != 0)
                            {
                                calculatedPlanHoursInMinutes += planRegistration.PlannedEndOfShift3 -
                                                                planRegistration.PlannedStartOfShift3 -
                                                                planRegistration.PlannedBreakOfShift3;
                                planRegistration.PlanHours = calculatedPlanHoursInMinutes / 60.0;
                            }

                            if (planRegistration.PlannedStartOfShift4 != 0 && planRegistration.PlannedEndOfShift4 != 0)
                            {
                                calculatedPlanHoursInMinutes += planRegistration.PlannedEndOfShift4 -
                                                                planRegistration.PlannedStartOfShift4 -
                                                                planRegistration.PlannedBreakOfShift4;
                                planRegistration.PlanHours = calculatedPlanHoursInMinutes / 60.0;
                            }

                            if (planRegistration.PlannedStartOfShift5 != 0 && planRegistration.PlannedEndOfShift5 != 0)
                            {
                                calculatedPlanHoursInMinutes += planRegistration.PlannedEndOfShift5 -
                                                                planRegistration.PlannedStartOfShift5 -
                                                                planRegistration.PlannedBreakOfShift5;
                                planRegistration.PlanHours = calculatedPlanHoursInMinutes / 60.0;
                            }

                            if (originalPlanHours != planRegistration.PlanHours || tainted)
                            {
                                SentrySdk.CaptureEvent(
                                    new SentryEvent
                                    {
                                        Message = $"PlanRegistrationHelper.UpdatePlanRegistrationsInPeriod: " +
                                                  $"Plan hours changed from {originalPlanHours} to {planRegistration.PlanHours} " +
                                                  $"for plan registration with ID {planRegistration.Id} and date {planRegistration.Date}",
                                        Level = SentryLevel.Warning
                                    });
                                tainted = true;
                                var preTimePlanning =
                                    await dbContext.PlanRegistrations.AsNoTracking()
                                        .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                                        .Where(x => x.Date < planRegistration.Date
                                                    && x.SdkSitId == dbAssignedSite.SiteId)
                                        .OrderByDescending(x => x.Date)
                                        .FirstOrDefaultAsync();

                                if (preTimePlanning != null)
                                {
                                    if (planRegistration.NettoHoursOverrideActive)
                                    {
                                        planRegistration.SumFlexStart = preTimePlanning.SumFlexEnd;
                                        planRegistration.SumFlexEnd =
                                            preTimePlanning.SumFlexEnd + planRegistration.NettoHoursOverride -
                                            planRegistration.PlanHours -
                                            planRegistration.PaiedOutFlex;
                                        planRegistration.Flex = planRegistration.NettoHoursOverride - planRegistration.PlanHours;
                                    } else
                                    {
                                        planRegistration.SumFlexStart = preTimePlanning.SumFlexEnd;
                                        planRegistration.SumFlexEnd =
                                            preTimePlanning.SumFlexEnd + planRegistration.NettoHours -
                                            planRegistration.PlanHours -
                                            planRegistration.PaiedOutFlex;
                                        planRegistration.Flex = planRegistration.NettoHours - planRegistration.PlanHours;
                                    }
                                }
                                else
                                {
                                    if (planRegistration.NettoHoursOverrideActive)
                                    {
                                        planRegistration.SumFlexEnd =
                                            planRegistration.NettoHoursOverride - planRegistration.PlanHours -
                                            planRegistration.PaiedOutFlex;
                                        planRegistration.SumFlexStart = 0;
                                        planRegistration.Flex = planRegistration.NettoHoursOverride - planRegistration.PlanHours;
                                    }
                                    else
                                    {
                                        planRegistration.SumFlexEnd =
                                            planRegistration.NettoHours - planRegistration.PlanHours -
                                            planRegistration.PaiedOutFlex;
                                        planRegistration.SumFlexStart = 0;
                                        planRegistration.Flex = planRegistration.NettoHours - planRegistration.PlanHours;
                                    }
                                }
                            }

                            await planRegistration.Update(dbContext).ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    if (planRegistration.Date > dayOfPayment && !planRegistration.PlanChangedByAdmin)
                    {
                        var dayOfWeek = planRegistration.Date.DayOfWeek;
                        var originalPlanHours = planRegistration.PlanHours;
                        switch (dayOfWeek)
                        {
                            case DayOfWeek.Monday:
                                planRegistration.PlanHours = dbAssignedSite.MondayPlanHours != 0
                                    ? (double)dbAssignedSite.MondayPlanHours / 60
                                    : 0;
                                if (!dbAssignedSite.UseOnlyPlanHours)
                                {
                                    planRegistration.PlannedStartOfShift1 = dbAssignedSite.StartMonday ?? 0;
                                    planRegistration.PlannedEndOfShift1 = dbAssignedSite.EndMonday ?? 0;
                                    planRegistration.PlannedBreakOfShift1 = dbAssignedSite.BreakMonday ?? 0;
                                    planRegistration.PlannedStartOfShift2 =
                                        dbAssignedSite.StartMonday2NdShift ?? 0;
                                    planRegistration.PlannedEndOfShift2 = dbAssignedSite.EndMonday2NdShift ?? 0;
                                    planRegistration.PlannedBreakOfShift2 =
                                        dbAssignedSite.BreakMonday2NdShift ?? 0;
                                    planRegistration.PlannedStartOfShift3 =
                                        dbAssignedSite.StartMonday3RdShift ?? 0;
                                    planRegistration.PlannedEndOfShift3 = dbAssignedSite.EndMonday3RdShift ?? 0;
                                    planRegistration.PlannedBreakOfShift3 =
                                        dbAssignedSite.BreakMonday3RdShift ?? 0;
                                    planRegistration.PlannedStartOfShift4 =
                                        dbAssignedSite.StartMonday4ThShift ?? 0;
                                    planRegistration.PlannedEndOfShift4 = dbAssignedSite.EndMonday4ThShift ?? 0;
                                    planRegistration.PlannedBreakOfShift4 =
                                        dbAssignedSite.BreakMonday4ThShift ?? 0;
                                    planRegistration.PlannedStartOfShift5 =
                                        dbAssignedSite.StartMonday5ThShift ?? 0;
                                    planRegistration.PlannedEndOfShift5 = dbAssignedSite.EndMonday5ThShift ?? 0;
                                    planRegistration.PlannedBreakOfShift5 =
                                        dbAssignedSite.BreakMonday5ThShift ?? 0;
                                }

                                break;
                            case DayOfWeek.Tuesday:
                                planRegistration.PlanHours = dbAssignedSite.TuesdayPlanHours != 0
                                    ? (double)dbAssignedSite.TuesdayPlanHours / 60
                                    : 0;
                                if (!dbAssignedSite.UseOnlyPlanHours)
                                {
                                    planRegistration.PlannedStartOfShift1 = dbAssignedSite.StartTuesday ?? 0;
                                    planRegistration.PlannedEndOfShift1 = dbAssignedSite.EndTuesday ?? 0;
                                    planRegistration.PlannedBreakOfShift1 = dbAssignedSite.BreakTuesday ?? 0;
                                    planRegistration.PlannedStartOfShift2 =
                                        dbAssignedSite.StartTuesday2NdShift ?? 0;
                                    planRegistration.PlannedEndOfShift2 =
                                        dbAssignedSite.EndTuesday2NdShift ?? 0;
                                    planRegistration.PlannedBreakOfShift2 =
                                        dbAssignedSite.BreakTuesday2NdShift ?? 0;
                                    planRegistration.PlannedStartOfShift3 =
                                        dbAssignedSite.StartTuesday3RdShift ?? 0;
                                    planRegistration.PlannedEndOfShift3 =
                                        dbAssignedSite.EndTuesday3RdShift ?? 0;
                                    planRegistration.PlannedBreakOfShift3 =
                                        dbAssignedSite.BreakTuesday3RdShift ?? 0;
                                    planRegistration.PlannedStartOfShift4 =
                                        dbAssignedSite.StartTuesday4ThShift ?? 0;
                                    planRegistration.PlannedEndOfShift4 =
                                        dbAssignedSite.EndTuesday4ThShift ?? 0;
                                    planRegistration.PlannedBreakOfShift4 =
                                        dbAssignedSite.BreakTuesday4ThShift ?? 0;
                                    planRegistration.PlannedStartOfShift5 =
                                        dbAssignedSite.StartTuesday5ThShift ?? 0;
                                    planRegistration.PlannedEndOfShift5 =
                                        dbAssignedSite.EndTuesday5ThShift ?? 0;
                                    planRegistration.PlannedBreakOfShift5 =
                                        dbAssignedSite.BreakTuesday5ThShift ?? 0;
                                }

                                break;
                            case DayOfWeek.Wednesday:
                                planRegistration.PlanHours = dbAssignedSite.WednesdayPlanHours != 0
                                    ? (double)dbAssignedSite.WednesdayPlanHours / 60
                                    : 0;
                                if (!dbAssignedSite.UseOnlyPlanHours)
                                {
                                    planRegistration.PlannedStartOfShift1 = dbAssignedSite.StartWednesday ?? 0;
                                    planRegistration.PlannedEndOfShift1 = dbAssignedSite.EndWednesday ?? 0;
                                    planRegistration.PlannedBreakOfShift1 = dbAssignedSite.BreakWednesday ?? 0;
                                    planRegistration.PlannedStartOfShift2 =
                                        dbAssignedSite.StartWednesday2NdShift ?? 0;
                                    planRegistration.PlannedEndOfShift2 =
                                        dbAssignedSite.EndWednesday2NdShift ?? 0;
                                    planRegistration.PlannedBreakOfShift2 =
                                        dbAssignedSite.BreakWednesday2NdShift ?? 0;
                                    planRegistration.PlannedStartOfShift3 =
                                        dbAssignedSite.StartWednesday3RdShift ?? 0;
                                    planRegistration.PlannedEndOfShift3 =
                                        dbAssignedSite.EndWednesday3RdShift ?? 0;
                                    planRegistration.PlannedBreakOfShift3 =
                                        dbAssignedSite.BreakWednesday3RdShift ?? 0;
                                    planRegistration.PlannedStartOfShift4 =
                                        dbAssignedSite.StartWednesday4ThShift ?? 0;
                                    planRegistration.PlannedEndOfShift4 =
                                        dbAssignedSite.EndWednesday4ThShift ?? 0;
                                    planRegistration.PlannedBreakOfShift4 =
                                        dbAssignedSite.BreakWednesday4ThShift ?? 0;
                                    planRegistration.PlannedStartOfShift5 =
                                        dbAssignedSite.StartWednesday5ThShift ?? 0;
                                    planRegistration.PlannedEndOfShift5 =
                                        dbAssignedSite.EndWednesday5ThShift ?? 0;
                                    planRegistration.PlannedBreakOfShift5 =
                                        dbAssignedSite.BreakWednesday5ThShift ?? 0;
                                }

                                break;
                            case DayOfWeek.Thursday:
                                planRegistration.PlanHours = dbAssignedSite.ThursdayPlanHours != 0
                                    ? (double)dbAssignedSite.ThursdayPlanHours / 60
                                    : 0;
                                if (!dbAssignedSite.UseOnlyPlanHours)
                                {
                                    planRegistration.PlannedStartOfShift1 = dbAssignedSite.StartThursday ?? 0;
                                    planRegistration.PlannedEndOfShift1 = dbAssignedSite.EndThursday ?? 0;
                                    planRegistration.PlannedBreakOfShift1 = dbAssignedSite.BreakThursday ?? 0;
                                    planRegistration.PlannedStartOfShift2 =
                                        dbAssignedSite.StartThursday2NdShift ?? 0;
                                    planRegistration.PlannedEndOfShift2 =
                                        dbAssignedSite.EndThursday2NdShift ?? 0;
                                    planRegistration.PlannedBreakOfShift2 =
                                        dbAssignedSite.BreakThursday2NdShift ?? 0;
                                    planRegistration.PlannedStartOfShift3 =
                                        dbAssignedSite.StartThursday3RdShift ?? 0;
                                    planRegistration.PlannedEndOfShift3 =
                                        dbAssignedSite.EndThursday3RdShift ?? 0;
                                    planRegistration.PlannedBreakOfShift3 =
                                        dbAssignedSite.BreakThursday3RdShift ?? 0;
                                    planRegistration.PlannedStartOfShift4 =
                                        dbAssignedSite.StartThursday4ThShift ?? 0;
                                    planRegistration.PlannedEndOfShift4 =
                                        dbAssignedSite.EndThursday4ThShift ?? 0;
                                    planRegistration.PlannedBreakOfShift4 =
                                        dbAssignedSite.BreakThursday4ThShift ?? 0;
                                    planRegistration.PlannedStartOfShift5 =
                                        dbAssignedSite.StartThursday5ThShift ?? 0;
                                    planRegistration.PlannedEndOfShift5 =
                                        dbAssignedSite.EndThursday5ThShift ?? 0;
                                    planRegistration.PlannedBreakOfShift5 =
                                        dbAssignedSite.BreakThursday5ThShift ?? 0;
                                }

                                break;
                            case DayOfWeek.Friday:
                                planRegistration.PlanHours = dbAssignedSite.FridayPlanHours != 0
                                    ? (double)dbAssignedSite.FridayPlanHours / 60
                                    : 0;
                                if (!dbAssignedSite.UseOnlyPlanHours)
                                {
                                    planRegistration.PlannedStartOfShift1 = dbAssignedSite.StartFriday ?? 0;
                                    planRegistration.PlannedEndOfShift1 = dbAssignedSite.EndFriday ?? 0;
                                    planRegistration.PlannedBreakOfShift1 = dbAssignedSite.BreakFriday ?? 0;
                                    planRegistration.PlannedStartOfShift2 =
                                        dbAssignedSite.StartFriday2NdShift ?? 0;
                                    planRegistration.PlannedEndOfShift2 = dbAssignedSite.EndFriday2NdShift ?? 0;
                                    planRegistration.PlannedBreakOfShift2 =
                                        dbAssignedSite.BreakFriday2NdShift ?? 0;
                                    planRegistration.PlannedStartOfShift3 =
                                        dbAssignedSite.StartFriday3RdShift ?? 0;
                                    planRegistration.PlannedEndOfShift3 = dbAssignedSite.EndFriday3RdShift ?? 0;
                                    planRegistration.PlannedBreakOfShift3 =
                                        dbAssignedSite.BreakFriday3RdShift ?? 0;
                                    planRegistration.PlannedStartOfShift4 =
                                        dbAssignedSite.StartFriday4ThShift ?? 0;
                                    planRegistration.PlannedEndOfShift4 = dbAssignedSite.EndFriday4ThShift ?? 0;
                                    planRegistration.PlannedBreakOfShift4 =
                                        dbAssignedSite.BreakFriday4ThShift ?? 0;
                                    planRegistration.PlannedStartOfShift5 =
                                        dbAssignedSite.StartFriday5ThShift ?? 0;
                                    planRegistration.PlannedEndOfShift5 = dbAssignedSite.EndFriday5ThShift ?? 0;
                                    planRegistration.PlannedBreakOfShift5 =
                                        dbAssignedSite.BreakFriday5ThShift ?? 0;
                                }

                                break;
                            case DayOfWeek.Saturday:
                                planRegistration.PlanHours = dbAssignedSite.SaturdayPlanHours != 0
                                    ? (double)dbAssignedSite.SaturdayPlanHours / 60
                                    : 0;
                                if (!dbAssignedSite.UseOnlyPlanHours)
                                {
                                    planRegistration.PlannedStartOfShift1 = dbAssignedSite.StartSaturday ?? 0;
                                    planRegistration.PlannedEndOfShift1 = dbAssignedSite.EndSaturday ?? 0;
                                    planRegistration.PlannedBreakOfShift1 = dbAssignedSite.BreakSaturday ?? 0;
                                    planRegistration.PlannedStartOfShift2 =
                                        dbAssignedSite.StartSaturday2NdShift ?? 0;
                                    planRegistration.PlannedEndOfShift2 =
                                        dbAssignedSite.EndSaturday2NdShift ?? 0;
                                    planRegistration.PlannedBreakOfShift2 =
                                        dbAssignedSite.BreakSaturday2NdShift ?? 0;
                                    planRegistration.PlannedStartOfShift3 =
                                        dbAssignedSite.StartSaturday3RdShift ?? 0;
                                    planRegistration.PlannedEndOfShift3 =
                                        dbAssignedSite.EndSaturday3RdShift ?? 0;
                                    planRegistration.PlannedBreakOfShift3 =
                                        dbAssignedSite.BreakSaturday3RdShift ?? 0;
                                    planRegistration.PlannedStartOfShift4 =
                                        dbAssignedSite.StartSaturday4ThShift ?? 0;
                                    planRegistration.PlannedEndOfShift4 =
                                        dbAssignedSite.EndSaturday4ThShift ?? 0;
                                    planRegistration.PlannedBreakOfShift4 =
                                        dbAssignedSite.BreakSaturday4ThShift ?? 0;
                                    planRegistration.PlannedStartOfShift5 =
                                        dbAssignedSite.StartSaturday5ThShift ?? 0;
                                    planRegistration.PlannedEndOfShift5 =
                                        dbAssignedSite.EndSaturday5ThShift ?? 0;
                                    planRegistration.PlannedBreakOfShift5 =
                                        dbAssignedSite.BreakSaturday5ThShift ?? 0;
                                }

                                break;
                            case DayOfWeek.Sunday:
                                planRegistration.PlanHours = dbAssignedSite.SundayPlanHours != 0
                                    ? (double)dbAssignedSite.SundayPlanHours / 60
                                    : 0;
                                if (!dbAssignedSite.UseOnlyPlanHours)
                                {
                                    planRegistration.PlannedStartOfShift1 = dbAssignedSite.StartSunday ?? 0;
                                    planRegistration.PlannedEndOfShift1 = dbAssignedSite.EndSunday ?? 0;
                                    planRegistration.PlannedBreakOfShift1 = dbAssignedSite.BreakSunday ?? 0;
                                    planRegistration.PlannedStartOfShift2 =
                                        dbAssignedSite.StartSunday2NdShift ?? 0;
                                    planRegistration.PlannedEndOfShift2 = dbAssignedSite.EndSunday2NdShift ?? 0;
                                    planRegistration.PlannedBreakOfShift2 =
                                        dbAssignedSite.BreakSunday2NdShift ?? 0;
                                    planRegistration.PlannedStartOfShift3 =
                                        dbAssignedSite.StartSunday3RdShift ?? 0;
                                    planRegistration.PlannedEndOfShift3 = dbAssignedSite.EndSunday3RdShift ?? 0;
                                    planRegistration.PlannedBreakOfShift3 =
                                        dbAssignedSite.BreakSunday3RdShift ?? 0;
                                    planRegistration.PlannedStartOfShift4 =
                                        dbAssignedSite.StartSunday4ThShift ?? 0;
                                    planRegistration.PlannedEndOfShift4 = dbAssignedSite.EndSunday4ThShift ?? 0;
                                    planRegistration.PlannedBreakOfShift4 =
                                        dbAssignedSite.BreakSunday4ThShift ?? 0;
                                    planRegistration.PlannedStartOfShift5 =
                                        dbAssignedSite.StartSunday5ThShift ?? 0;
                                    planRegistration.PlannedEndOfShift5 = dbAssignedSite.EndSunday5ThShift ?? 0;
                                    planRegistration.PlannedBreakOfShift5 =
                                        dbAssignedSite.BreakSunday5ThShift ?? 0;
                                }

                                break;
                        }

                        if (originalPlanHours != planRegistration.PlanHours || tainted)
                        {
                            SentrySdk.CaptureEvent(
                                new SentryEvent
                                {
                                    Message = $"PlanRegistrationHelper.UpdatePlanRegistrationsInPeriod: " +
                                              $"Plan hours changed from {originalPlanHours} to {planRegistration.PlanHours} " +
                                              $"for plan registration with ID {planRegistration.Id} and date {planRegistration.Date}",
                                    Level = SentryLevel.Warning
                                });
                            tainted = true;
                            var preTimePlanning =
                                await dbContext.PlanRegistrations.AsNoTracking()
                                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                                    .Where(x => x.Date < planRegistration.Date
                                                && x.SdkSitId == dbAssignedSite.SiteId)
                                    .OrderByDescending(x => x.Date)
                                    .FirstOrDefaultAsync();

                            if (preTimePlanning != null)
                            {
                                planRegistration.SumFlexStart = preTimePlanning.SumFlexEnd;
                                planRegistration.SumFlexEnd =
                                    preTimePlanning.SumFlexEnd + planRegistration.NettoHours -
                                    planRegistration.PlanHours -
                                    planRegistration.PaiedOutFlex;
                                planRegistration.Flex = planRegistration.NettoHours - planRegistration.PlanHours;
                            }
                            else
                            {
                                planRegistration.SumFlexEnd =
                                    planRegistration.NettoHours - planRegistration.PlanHours -
                                    planRegistration.PaiedOutFlex;
                                planRegistration.SumFlexStart = 0;
                                planRegistration.Flex = planRegistration.NettoHours - planRegistration.PlanHours;
                            }
                        }

                        Console.WriteLine($"The plannedHours are now: {planRegistration.PlanHours}");

                        await planRegistration.Update(dbContext).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception e)
            {
                SentrySdk.CaptureMessage(
                    $"Could not parse PlanText for planning with id: {planRegistration.Id} the PlanText was: {planRegistration.PlanText}");
            }
        // }
        return planRegistration;
    }

    private static int BreakTimeCalculator(string breakPart)
    {
        return breakPart switch
        {
            "0.1" => 5,
            ".1" => 5,
            "0.15" => 10,
            ".15" => 10,
            "0.25" => 15,
            ".25" => 15,
            "0.3" => 20,
            ".3" => 20,
            "0.4" => 25,
            ".4" => 25,
            "0.5" => 30,
            ".5" => 30,
            "0.6" => 35,
            ".6" => 35,
            "0.7" => 40,
            ".7" => 40,
            "0.75" => 45,
            ".75" => 45,
            "0.8" => 50,
            ".8" => 50,
            "0.9" => 55,
            ".9" => 55,
            "¾" => 45,
            "½" => 30,
            "1" => 60,
            _ => 0
        };
    }
}