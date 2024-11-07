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
                var planRegistrationsForSite = dbContext.PlanRegistrations
                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                    .Where(x => x.SdkSitId == siteId)
                    .OrderBy(x => x.Date)
                    .ToList();

                foreach (var planRegistration in planRegistrationsForSite)
                {
                    planRegistration.SumFlexStart = preSumFlexStart;
                    planRegistration.SumFlexEnd = preSumFlexStart + planRegistration.NettoHours -
                                                  planRegistration.PlanHours -
                                                  planRegistration.PaiedOutFlex;
                    planRegistration.Flex = planRegistration.NettoHours - planRegistration.PlanHours;
                    var entry = dbContext.Entry(planRegistration);
                    if (entry.State == EntityState.Modified)
                    {
                        SentrySdk.CaptureMessage($"PlanRegistration has changed with id: {planRegistration.Id} for siteId: {siteId} at planRegistration.Date: {planRegistration.Date}");
                    }

                    preSumFlexStart = planRegistration.SumFlexEnd;
                }
            });
        }
    }
}