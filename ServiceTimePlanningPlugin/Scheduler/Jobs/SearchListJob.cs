using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.TimePlanningBase.Infrastructure.Data;
using Sentry;
using ServiceTimePlanningPlugin.Infrastructure.Helpers;

namespace ServiceTimePlanningPlugin.Scheduler.Jobs;

public class SearchListJob(DbContextHelper dbContextHelper) : IJob
{
    public async Task Execute()
    {
        if (DateTime.UtcNow.Hour == 18)
        {
            var dbContext = dbContextHelper.GetDbContext();
            var siteIds = await dbContext.AssignedSites.Select(x => x.SiteId).ToListAsync();

            Parallel.ForEach(siteIds, siteId =>
            {
                double preSumFlexStart = 0;
                var innerDbContext = dbContextHelper.GetDbContext();
                var planRegistrationsForSite = innerDbContext.PlanRegistrations
                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                    .Where(x => x.SdkSitId == siteId)
                    .OrderBy(x => x.Date)
                    .ToList();

                foreach (var planRegistration in planRegistrationsForSite)
                {
                    var originalPlanRegistration = innerDbContext.PlanRegistrations.AsNoTracking()
                        .First(x => x.Id == planRegistration.Id);
                    planRegistration.SumFlexStart = preSumFlexStart;
                    planRegistration.SumFlexEnd = preSumFlexStart + planRegistration.NettoHours -
                                                  planRegistration.PlanHours -
                                                  planRegistration.PaiedOutFlex;
                    planRegistration.Flex = planRegistration.NettoHours - planRegistration.PlanHours;
                    Console.WriteLine($@"Checking planRegistration.Id: {planRegistration.Id} for siteId: {siteId} at planRegistration.Date: {planRegistration.Date}");

                    if (originalPlanRegistration.SumFlexEnd != planRegistration.SumFlexEnd || originalPlanRegistration.Flex != planRegistration.Flex)
                    {
                        SentrySdk.CaptureMessage($"PlanRegistration has changed with id: {planRegistration.Id} for siteId: {siteId} at planRegistration.Date: {planRegistration.Date}, " +
                                                 $"SumFlexStart changed from {originalPlanRegistration.SumFlexStart} to {planRegistration.SumFlexStart}" +
                                                 $"and SumFlexEnd changed from {originalPlanRegistration.SumFlexEnd} to {planRegistration.SumFlexEnd}", SentryLevel.Error);
                        planRegistration.Update(innerDbContext).GetAwaiter().GetResult();
                    }
                    else
                    {
                        Console.WriteLine($@"PlanRegistration has not changed with id: {planRegistration.Id} for siteId: {siteId} at planRegistration.Date: {planRegistration.Date}");
                    }
                    preSumFlexStart = planRegistration.SumFlexEnd;
                }
            });
        }
    }
}