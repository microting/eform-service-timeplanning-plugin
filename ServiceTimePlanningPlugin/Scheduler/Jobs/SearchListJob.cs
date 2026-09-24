using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.TimePlanningBase.Infrastructure.Data.Entities;
using Microting.TimePlanningBase.Infrastructure.Helpers;
using Sentry;
using ServiceTimePlanningPlugin.Infrastructure.Helpers;

namespace ServiceTimePlanningPlugin.Scheduler.Jobs;

public class SearchListJob(DbContextHelper dbContextHelper, eFormCore.Core sdkCore) : IJob
{
    public async Task Execute()
    {
        switch (DateTime.UtcNow.Hour)
        {
            case 1:
            case 4:
            case 7:
            case 10:
            case 13:
            case 16:
            case 19:
            case 21:
            {
                try
                {
                    var dbContext = dbContextHelper.GetDbContext();
                    var sdkContext = sdkCore.DbContextHelper.GetDbContext();
                    var privateKeyId = Environment.GetEnvironmentVariable("PRIVATE_KEY_ID");
                    if (string.IsNullOrEmpty(privateKeyId))
                    {
                        return;
                    }

                    var googleSheetId = await dbContext.PluginConfigurationValues
                        .FirstOrDefaultAsync(x => x.Name == "TimePlanningBaseSettings:GoogleSheetId");

                    if (googleSheetId == null)
                    {
                        return;
                    }

                    if (string.IsNullOrEmpty(googleSheetId.Value))
                    {
                        return;
                    }

                    var applicationName = "Google Sheets API Integration";
                    var privateKey = Environment.GetEnvironmentVariable("PRIVATE_KEY");
                    var clientEmail = Environment.GetEnvironmentVariable("CLIENT_EMAIL");
                    var projectId = Environment.GetEnvironmentVariable("PROJECT_ID");
                    var clientId = Environment.GetEnvironmentVariable("CLIENT_ID");

                    string serviceAccountJson = $@"
            {{
              ""type"": ""service_account"",
              ""project_id"": ""{projectId}"",
              ""private_key_id"": ""{privateKeyId}"",
              ""private_key"": ""{privateKey}"",
              ""client_email"": ""{clientEmail}"",
              ""client_id"": ""{clientId}"",
              ""auth_uri"": ""https://accounts.google.com/o/oauth2/auth"",
              ""token_uri"": ""https://oauth2.googleapis.com/token"",
              ""auth_provider_x509_cert_url"": ""https://www.googleapis.com/oauth2/v1/certs"",
              ""client_x509_cert_url"": ""https://www.googleapis.com/robot/v1/metadata/x509/{clientEmail}""
            }}";

                    // Authenticate using the dynamically constructed JSON
                    var credential = GoogleCredential.FromJson(serviceAccountJson)
                        .CreateScoped(SheetsService.Scope.Spreadsheets);

                    var service = new SheetsService(new BaseClientService.Initializer
                    {
                        HttpClientInitializer = credential,
                        ApplicationName = applicationName
                    });

                    // Define request parameters.
                    // Get the sheet metadata to determine the range

                    // Define request parameters with the determined range
                    var range = $"PlanTimer";
                    var request =
                        service.Spreadsheets.Values.Get(googleSheetId.Value, range);

                    // Fetch the data from the sheet
                    var response = await request.ExecuteAsync();
                    var values = response.Values;

                    if (values is { Count: > 0 })
                    {
                        // Columns are resolved to sites once per run, not per (date, column) cell.
                        var layout = PlanTimerSheetColumns.Map(values[0]);
                        var assignedSites = await dbContext.AssignedSites
                            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                            .ToListAsync();
                        var assignedSiteIds = assignedSites.Select(x => x.SiteId).ToList();
                        var sites = await sdkContext.Sites
                            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                            .Where(x => x.MicrotingUid != null && assignedSiteIds.Contains(x.MicrotingUid.Value))
                            .Select(x => new { x.Name, SiteId = x.MicrotingUid!.Value })
                            .ToListAsync();
                        var resolution = PlanTimerSheetColumns.Resolve(layout.Workers,
                            sites.Select(x => (x.Name, x.SiteId)), assignedSites);

                        foreach (var message in resolution.Unmatched)
                        {
                            Console.WriteLine($"info: PlanTimer sheet {message}");
                        }

                        foreach (var problem in layout.Problems.Concat(resolution.Problems))
                        {
                            Console.WriteLine($"warn: PlanTimer sheet: {problem}");
                            SentrySdk.CaptureMessage($"PlanTimer sheet: {problem}", SentryLevel.Warning);
                        }

                        // Built once per worker, never per cell -- see OneMinuteModeTimeline.
                        var timelines = new Dictionary<int, OneMinuteModeTimeline>();
                        foreach (var worker in resolution.Workers)
                        {
                            timelines[worker.SiteId] =
                                await OneMinuteModeTimeline.BuildAsync(dbContext, worker.AssignedSite);
                        }

                        // Every worker's reconciled boundary, resolved ONCE for the run
                        // rather than per (date, site) cell. A reconciled day is frozen,
                        // so the sheet must not overwrite it -- and one rejected save
                        // would abort this whole run for every site, since the only catch
                        // is the one around all of it.
                        //
                        // Keyed off resolution.Workers, the very list the row loop below
                        // iterates, so a worker cannot reach that loop without an entry
                        // here: DayLockHelper.IsLocked reads a site MISSING from the map
                        // as having no boundary, i.e. silently open, so the map's key set
                        // must not be derived independently of the loop's.
                        var lockedThroughBySite = await DayLockHelper.LockedThroughForSitesAsync(
                            dbContext, resolution.Workers.Select(x => x.SiteId).Distinct().ToList());

                        // Skip the header row (first row)
                        for (var i = 1; i < values.Count; i++)
                        {
                            var row = values[i];
                            var date = PlanTimerSheetColumns.CellAt(row, 0);

                            if (!DateTime.TryParseExact(date, "dd.MM.yyyy", CultureInfo.InvariantCulture,
                                    DateTimeStyles.None, out var dateValue))
                            {
                                continue;
                            }

                            if (dateValue < DateTime.Now.AddDays(-1))
                            {
                                Console.WriteLine($"info: Skipping past date: {dateValue}");
                                continue;
                            }

                            if (dateValue > DateTime.Now.AddDays(180))
                            {
                                Console.WriteLine($"info: Skipping future date beyond 6 months: {dateValue}");
                                continue;
                            }

                            foreach (var (columns, siteName, siteId, assignedSite) in resolution.Workers)
                            {
                                Console.WriteLine($"info: Processing site: {siteName} for date: {dateValue}");

                                // Before the row is loaded: a tracked locked row would
                                // be flushed by the next save on this shared context.
                                if (DayLockHelper.IsLocked(lockedThroughBySite, siteId, dateValue))
                                {
                                    Console.WriteLine(
                                        $"info: Skipping locked (reconciled) date: {dateValue} for site: {siteName}");
                                    continue;
                                }

                                // null leaves the field untouched: the sheet has no such
                                // column for this worker, or the hours cell is not a number.
                                var planText = columns.TextColumn == null
                                    ? null
                                    : PlanTimerSheetColumns.CellAt(row, columns.TextColumn);
                                double? parsedPlanHours = null;
                                if (columns.HoursColumn != null)
                                {
                                    var planHours = PlanTimerSheetColumns.CellAt(row, columns.HoursColumn);
                                    parsedPlanHours = PlanTimerSheetColumns.ParseHours(planHours);
                                    if (parsedPlanHours == null)
                                    {
                                        // One bad cell must not abort the import for every other worker.
                                        Console.WriteLine(
                                            $"warn: PlanTimer sheet hours \"{planHours.Trim()}\" for site: {siteName} and date: {dateValue} is not a number; hours not imported");
                                    }
                                }

                                // var preTimePlanning = await dbContext.PlanRegistrations.AsNoTracking()
                                //     .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                                //     .Where(x => x.Date < dateValue && x.SdkSitId == (int)site.MicrotingUid!)
                                //     .OrderByDescending(x => x.Date)
                                //     .FirstOrDefaultAsync();

                                var midnight = new DateTime(dateValue.Year, dateValue.Month, dateValue.Day, 0, 0, 0);

                                var planRegistrations = await dbContext.PlanRegistrations.Where(x =>
                                        x.Date == midnight && x.SdkSitId == siteId)
                                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                                    .ToListAsync();
                                if (planRegistrations.Count > 1)
                                {
                                    Console.WriteLine(
                                        $"fail: Found multiple plan registrations for site: {siteName} and date: {dateValue}. This should not happen.");
                                    SentrySdk.CaptureMessage(
                                        $"fail: Found multiple plan registrations for site: {siteName} and date: {dateValue}. This should not happen.");
                                    foreach (var plan in planRegistrations)
                                    {
                                        Console.WriteLine(
                                            $"fail: PlanRegistration ID: {plan.Id}, PlanText: {plan.PlanText}, PlanHours: {plan.PlanHours}, Date: {plan.Date}, workflowState: {plan.WorkflowState}, SdkSitId: {plan.SdkSitId}");
                                        SentrySdk.CaptureMessage(
                                            $"fail: PlanRegistration ID: {plan.Id}, PlanText: {plan.PlanText}, PlanHours: {plan.PlanHours}, Date: {plan.Date}, workflowState: {plan.WorkflowState}, SdkSitId: {plan.SdkSitId}");
                                    }

                                    continue;
                                }

                                var planRegistration = planRegistrations.FirstOrDefault();

                                if (planRegistration == null)
                                {
                                    planRegistration = new PlanRegistration
                                    {
                                        Date = midnight,
                                        PlanText = planText ?? string.Empty,
                                        PlanHours = parsedPlanHours ?? 0,
                                        SdkSitId = siteId,
                                        CreatedByUserId = 0,
                                        UpdatedByUserId = 0,
                                        NettoHours = 0,
                                        PaiedOutFlex = 0,
                                        Pause1Id = 0,
                                        Pause2Id = 0,
                                        Start1Id = 0,
                                        Start2Id = 0,
                                        Stop1Id = 0,
                                        Stop2Id = 0,
                                        Flex = 0,
                                        StatusCaseId = 0
                                    };

                                    // Commented out flex calculations to let UpdatePlanRegistration handle it
                                    // if (preTimePlanning != null)
                                    // {
                                    //     planRegistration.SumFlexStart = preTimePlanning.SumFlexEnd;
                                    //     planRegistration.SumFlexEnd =
                                    //         preTimePlanning.SumFlexEnd + planRegistration.Flex -
                                    //         planRegistration.PaiedOutFlex;
                                    //     planRegistration.Flex = -planRegistration.PlanHours;
                                    // }
                                    // else
                                    // {
                                    //     planRegistration.Flex = -planRegistration.PlanHours;
                                    //     planRegistration.SumFlexEnd = planRegistration.Flex;
                                    //     planRegistration.SumFlexStart = 0;
                                    // }

                                    await planRegistration.Create(dbContext);
                                }
                                else
                                {
                                    PlanTimerSheetColumns.ApplyTo(planRegistration, planText, parsedPlanHours,
                                        $"site: {siteName} and date: {dateValue}");

                                    planRegistration.UpdatedByUserId = 0;

                                    await planRegistration.Update(dbContext);
                                }

                                await PlanRegistrationHelper.UpdatePlanRegistration(planRegistration, dbContext,
                                    assignedSite, DateTime.Now.AddMonths(-1), timelines[siteId]);
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("fail: No data found.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"fail: {ex.Message}");
                    Console.WriteLine($"fail: {ex.StackTrace}");
                    SentrySdk.CaptureException(ex);
                }
            }
                break;
            case 18:
                await RecalculateRecentRegistrations();
                break;
        }
    }

    /// <summary>
    /// The nightly per-site recalculation of the last month's registrations,
    /// which also soft-deletes rows dated more than 6 months ahead. Days at or
    /// before a site's reconciled boundary are skipped.
    ///
    /// No hourly schedule gate here -- <see cref="Execute"/> is what the
    /// service timer calls; this is the entry point integration tests call
    /// directly (as FlexChainCatchUpJob.RunCatchUp is) so a test run does not
    /// depend on the wall-clock hour.
    /// </summary>
    public async Task RecalculateRecentRegistrations()
    {
        var dbContext = dbContextHelper.GetDbContext();
        var siteIds = await dbContext.AssignedSites
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .Select(x => x.SiteId)
            .ToListAsync();

        var toDay = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, 0, 0, 0);
        var dayOfPayment = toDay.AddMonths(-1);

        Parallel.ForEach(siteIds, siteId =>
        {
            try
            {
                var innerDbContext = dbContextHelper.GetDbContext();

                // Hoisted out of the row loop below: assignedSite does not vary
                // per row, and the timeline built from it must be built ONCE per
                // site -- never per row (~1 month of daily registrations per site
                // here) -- otherwise every row issues its own AssignedSiteVersions
                // query. See OneMinuteModeTimeline.
                var assignedSite = innerDbContext.AssignedSites
                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                    .FirstOrDefault(x => x.SiteId == siteId);
                var oneMinuteTimeline =
                    OneMinuteModeTimeline.BuildAsync(innerDbContext, assignedSite).GetAwaiter().GetResult();

                // A reconciled day is frozen, so rows at or before this site's
                // boundary are never selected: a locked row that is never loaded
                // is never tracked, so no save below can flush a change into it
                // and end the rest of the site's run.
                var lockedThrough =
                    DayLockHelper.LockedThroughAsync(innerDbContext, siteId).GetAwaiter().GetResult();

                var planRegistrationIdsForSite = innerDbContext.PlanRegistrations
                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                    .Where(x => x.SdkSitId == siteId)
                    .Where(x => x.Date > dayOfPayment)
                    .WhereOpen(lockedThrough)
                    .OrderBy(x => x.Date)
                    .Select(x => x.Id)
                    .ToList();

                // The earliest day whose balance this run changed; the days
                // after it are carried forward once, after the loop (R4).
                DateTime? earliestChanged = null;

                foreach (var planRegistrationId in planRegistrationIdsForSite)
                {
                    var planRegistration = innerDbContext.PlanRegistrations
                        .AsTracking()
                        .First(x => x.Id == planRegistrationId);
                    if (planRegistration.Date > DateTime.Now.AddMonths(6))
                    {
                        planRegistration.Delete(innerDbContext).GetAwaiter().GetResult();
                        Console.WriteLine(
                            $@"info: Deleting planRegistration.Id: {planRegistration.Id} for siteId: {siteId} at planRegistration.Date: {planRegistration.Date} since it is more than 6 months in the future");
                    }
                    else
                    {
                        var originalPlanRegistration = innerDbContext.PlanRegistrations.AsNoTracking()
                            .First(x => x.Id == planRegistration.Id);

                        planRegistration = PlanRegistrationHelper
                            .UpdatePlanRegistration(planRegistration, innerDbContext, assignedSite,
                                dayOfPayment, oneMinuteTimeline)
                            .GetAwaiter().GetResult();

                        if (originalPlanRegistration.SumFlexEnd != planRegistration.SumFlexEnd ||
                            originalPlanRegistration.Flex != planRegistration.Flex)
                        {
                            SentrySdk.CaptureMessage(
                                $"PlanRegistration has changed with id: {planRegistration.Id} for siteId: {siteId} at planRegistration.Date: {planRegistration.Date}, " +
                                $"SumFlexStart changed from {originalPlanRegistration.SumFlexStart} to {planRegistration.SumFlexStart}" +
                                $"and SumFlexEnd changed from {originalPlanRegistration.SumFlexEnd} to {planRegistration.SumFlexEnd}",
                                SentryLevel.Error);
                            Console.WriteLine(
                                $@"fail: PlanRegistration has changed with id: {planRegistration.Id} for siteId: {siteId} at planRegistration.Date: {planRegistration.Date}, " +
                                $"SumFlexStart changed from {originalPlanRegistration.SumFlexStart} to {planRegistration.SumFlexStart}" +
                                $"and SumFlexEnd changed from {originalPlanRegistration.SumFlexEnd} to {planRegistration.SumFlexEnd}");
                            planRegistration.Update(innerDbContext).GetAwaiter().GetResult();
                            // Ids are ordered by date (OrderBy above), so the first change is the earliest.
                            earliestChanged ??= planRegistration.Date;
                        }
                    }
                }

                if (earliestChanged is { } fromDate)
                {
                    FlexChainRecompute
                        .RunForwardAsync(innerDbContext, assignedSite, siteId, fromDate)
                        .GetAwaiter().GetResult();
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"fail: {ex.Message}");
                Console.WriteLine($"fail: {ex.StackTrace}");
                SentrySdk.CaptureException(ex);
            }
        });
    }
}