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

#nullable enable
namespace ServiceTimePlanningPlugin.Infrastructure.Helpers;

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.TimePlanningBase.Infrastructure.Data;
using Microting.TimePlanningBase.Infrastructure.Data.Entities;
using Microting.TimePlanningBase.Infrastructure.Helpers;

/// <summary>
/// The part of EFormCompletedHandler that touches the plan: once the device
/// submission has been written onto the day's row, compute that day's hours
/// and flex, save it, and carry the balance to the worker's later rows.
///
/// Split out of the handler so it can be tested without the eForm SDK: the
/// handler needs an SDK Core (cases, fields, field values) only to READ the
/// submission, never to compute anything from it.
/// </summary>
public static class PlanRegistrationDeviceSubmission
{
    /// <param name="timePlanning">
    /// The submitted day's row, already persisted and tracked by
    /// <paramref name="dbContext"/>.
    /// </param>
    /// <param name="assignedSite">The worker's AssignedSite, or null when none exists.</param>
    public static async Task ApplyAsync(
        TimePlanningPnDbContext dbContext, AssignedSite? assignedSite, PlanRegistration timePlanning)
    {
        // ONE query, in-memory lookups: resolves the mode that was in force
        // when each row was REGISTERED. Built ONCE here, before the cascade
        // loop below, and reused for every row in it -- every mode fork in
        // this helper reads the resulting per-row mode, never the site's
        // CURRENT flag. See OneMinuteModeTimeline.
        var oneMinuteTimeline = await OneMinuteModeTimeline.BuildAsync(dbContext, assignedSite);
        var rowIsOneMinute = oneMinuteTimeline.WasOneMinuteForRow(timePlanning);

        var preTimePlanning =
            await dbContext.PlanRegistrations.AsNoTracking()
                .Where(x => x.Date < timePlanning.Date && x.SdkSitId == timePlanning.SdkSitId)
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .OrderByDescending(x => x.Date).FirstOrDefaultAsync();

        if (rowIsOneMinute)
        {
            FlexChain.ApplyNettoFlexChainSecondPrecision(
                timePlanning, preTimePlanning, oneMinuteTimeline.WasOneMinuteFor(preTimePlanning));
        }
        else
        {
            // The one five-minute hours computation (shifts 1-5). A shift
            // counts only once it has a stop at or after its start, so an
            // open shift is 0 hours, never negative (R1).
            timePlanning.NettoHours = FlexChain.ComputeNettoMinutesFlagOff(timePlanning) / 60.0;

            FlexChain.ApplyNettoFlexChainDecimal(timePlanning, preTimePlanning);
        }

        await timePlanning.Update(dbContext);
        if (dbContext.PlanRegistrations.Any(x => x.Date >= timePlanning.Date && x.SdkSitId == timePlanning.SdkSitId && x.Id != timePlanning.Id && x.WorkflowState != Constants.WorkflowStates.Removed))
        {
            // Ascending, unbounded walk carrying its running seed IN
            // MEMORY (the just-updated preceding row, not a re-query).
            PlanRegistration previousRegistration = timePlanning;
            var list = await dbContext.PlanRegistrations
                .Where(x => x.Date > timePlanning.Date && x.SdkSitId == timePlanning.SdkSitId && x.Id != timePlanning.Id)
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .OrderBy(x => x.Date).ToListAsync();
            foreach (PlanRegistration planRegistration in list)
            {
                Console.WriteLine($"Updating planRegistration {planRegistration.Id} for date {planRegistration.Date}");
                // Fork on the mode AT REGISTRATION, not the site's current
                // flag -- see OneMinuteModeTimeline for why.
                if (oneMinuteTimeline.WasOneMinuteForRow(planRegistration))
                {
                    FlexChain.ApplyNettoFlexChainSecondPrecision(
                        planRegistration, previousRegistration,
                        oneMinuteTimeline.WasOneMinuteFor(previousRegistration));
                }
                else
                {
                    FlexChain.ApplyNettoFlexChainDecimal(planRegistration, previousRegistration);
                }
                await planRegistration.Update(dbContext);
                previousRegistration = planRegistration;
            }
        }
    }
}
