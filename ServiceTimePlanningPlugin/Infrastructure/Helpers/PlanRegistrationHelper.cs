using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microting.eForm.Infrastructure.Constants;
using Microting.eForm.Infrastructure.Data.Entities;
using Microting.eFormApi.BasePn.Infrastructure.Helpers.PluginDbOptions;
using Microting.TimePlanningBase.Infrastructure.Data;
using Microting.TimePlanningBase.Infrastructure.Data.Entities;
using Microting.TimePlanningBase.Infrastructure.Data.Models;
using Microting.TimePlanningBase.Infrastructure.Helpers;
using Sentry;

namespace ServiceTimePlanningPlugin.Infrastructure.Helpers;

public static class PlanRegistrationHelper
{
    public static async Task<PlanRegistration> UpdatePlanRegistration(
        PlanRegistration planRegistration,
        TimePlanningPnDbContext dbContext,
        AssignedSite dbAssignedSite,
        DateTime dayOfPayment,
        OneMinuteModeTimeline oneMinuteTimeline
        )
    {
        var tainted = false;
        // Every mode fork below reads the resulting rowIsOneMinute, never the
        // site's CURRENT flag — recomputing closed days under a newly-enabled
        // one-minute flag would silently rewrite historical balances. The
        // timeline itself is a required parameter (built ONCE per site by the
        // caller, before its row loop) rather than built here per call — see
        // OneMinuteModeTimeline: "Build once per site per request scope —
        // never per row."
        var rowIsOneMinute = oneMinuteTimeline.WasOneMinuteForRow(planRegistration);
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
                            var originalPlanHours = planRegistration.PlanHours;

                            // Shift columns and PlanHours come from the shared parser
                            // in the base, so the service, the plugin and the sheet
                            // import all read one PlanText grammar.
                            PlanRegistrationPlanText.ParseInto(planRegistration);

                            // The parser never throws, so a parse failure no longer
                            // arrives as an exception. Text that was meant to be a
                            // shift but produced none is the signal instead.
                            if (LooksLikeAShift(planRegistration) && !HasAnyPlannedShift(planRegistration))
                            {
                                SentrySdk.CaptureMessage(
                                    $"Could not parse PlanText for planning with id: {planRegistration.Id} the PlanText was: {planRegistration.PlanText}");
                            }

                            if (originalPlanHours != planRegistration.PlanHours || tainted)
                            {
                                tainted = true;
                                var preTimePlanning =
                                    await dbContext.PlanRegistrations.AsNoTracking()
                                        .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                                        .Where(x => x.Date < planRegistration.Date
                                                    && x.SdkSitId == dbAssignedSite.SiteId)
                                        .OrderByDescending(x => x.Date)
                                        .FirstOrDefaultAsync();

                                // Fork on the mode AT REGISTRATION, not the site's current
                                // flag — see OneMinuteModeTimeline for why.
                                if (rowIsOneMinute)
                                {
                                    FlexChain.ApplyNettoFlexChainSecondPrecision(
                                        planRegistration, preTimePlanning,
                                        oneMinuteTimeline.WasOneMinuteFor(preTimePlanning));
                                }
                                else
                                {
                                    FlexChain.ApplyNettoFlexChainDecimal(planRegistration, preTimePlanning);
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
                            tainted = true;
                            var preTimePlanning =
                                await dbContext.PlanRegistrations.AsNoTracking()
                                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                                    .Where(x => x.Date < planRegistration.Date
                                                && x.SdkSitId == dbAssignedSite.SiteId)
                                    .OrderByDescending(x => x.Date)
                                    .FirstOrDefaultAsync();

                            // Fork on the mode AT REGISTRATION, not the site's current
                            // flag — see OneMinuteModeTimeline for why.
                            if (rowIsOneMinute)
                            {
                                FlexChain.ApplyNettoFlexChainSecondPrecision(
                                    planRegistration, preTimePlanning,
                                    oneMinuteTimeline.WasOneMinuteFor(preTimePlanning));
                            }
                            else
                            {
                                FlexChain.ApplyNettoFlexChainDecimal(planRegistration, preTimePlanning);
                            }
                        }

                        Console.WriteLine($"The plannedHours are now: {planRegistration.PlanHours}");

                        await planRegistration.Update(dbContext).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception e)
            {
                // Parsing no longer throws — the shared parser leaves what it
                // cannot read unset, and the unparseable case is reported above.
                // What reaches here is a failure of the surrounding update.
                SentrySdk.CaptureException(e);
            }
        // }
        return planRegistration;
    }

    /// <summary>
    /// True when the text was meant to describe a shift. Absence markers such
    /// as "Ferie" or "Fri" are legitimate cell contents rather than parse
    /// failures, and reporting them would fire on every pass over the sheet.
    /// A '-' separates a start from an end, so text without one was never a
    /// shift to begin with.
    /// </summary>
    private static bool LooksLikeAShift(PlanRegistration planRegistration) =>
        planRegistration.PlanText?.Contains('-') == true;

    /// <summary>
    /// The parser writes every slot, so all-zero columns mean nothing in the
    /// text was readable as a shift.
    /// </summary>
    private static bool HasAnyPlannedShift(PlanRegistration planRegistration) =>
        planRegistration.PlannedStartOfShift1 != 0 || planRegistration.PlannedEndOfShift1 != 0
        || planRegistration.PlannedStartOfShift2 != 0 || planRegistration.PlannedEndOfShift2 != 0
        || planRegistration.PlannedStartOfShift3 != 0 || planRegistration.PlannedEndOfShift3 != 0
        || planRegistration.PlannedStartOfShift4 != 0 || planRegistration.PlannedEndOfShift4 != 0
        || planRegistration.PlannedStartOfShift5 != 0 || planRegistration.PlannedEndOfShift5 != 0;
}