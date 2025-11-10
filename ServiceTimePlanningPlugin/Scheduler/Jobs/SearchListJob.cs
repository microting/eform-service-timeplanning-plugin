using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.TimePlanningBase.Infrastructure.Data.Entities;
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

                    var headerRows = values?.FirstOrDefault();
                    if (values is { Count: > 0 })
                    {
                        // Skip the header row (first row)
                        for (var i = 1; i < values.Count; i++)
                        {
                            var row = values[i];
                            // Process each row
                            var date = row[0].ToString();

                            // Process the dato as date

                            // Parse date and validate
                            if (!DateTime.TryParseExact(date, "dd.MM.yyyy", CultureInfo.InvariantCulture,
                                    DateTimeStyles.None, out var _))
                            {
                                continue;
                            }

                            var dateValue = DateTime.ParseExact(date, "dd.MM.yyyy", CultureInfo.InvariantCulture);
                            if (dateValue < DateTime.Now.AddDays(-1))
                            {
                                continue;
                            }

                            if (dateValue > DateTime.Now.AddDays(180))
                            {
                                continue;
                            }

                            // This is done since google api skips empty columns at the end of the row
                            if (row.Count < headerRows.Count)
                            {
                                var itemsToAdd = headerRows.Count - row.Count;
                                for (int k = 0; k < itemsToAdd; k++)
                                {
                                    row.Add(string.Empty);
                                }
                            }

                            // Iterate over each pair of columns starting from the fourth column
                            for (int j = 3; j < row.Count; j += 2)
                            {
                                var siteName = headerRows[j].ToString().Split(" - ").Length > 1
                                    ? headerRows[j].ToString().Split(" - ")[0].ToLower().Replace(" ", "").Trim()
                                    : headerRows[j].ToString().Split(" - ").First().ToLower().Replace(" ", "").Trim();
                                Console.WriteLine($"Processing site: {siteName} for date: {dateValue}");
                                var site = await sdkContext.Sites
                                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                                    .FirstOrDefaultAsync(x =>
                                        x.Name.Replace(" ", "").ToLower() == siteName);
                                if (site == null)
                                {
                                    continue;
                                }

                                var assignedSite = await dbContext.AssignedSites
                                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                                    .FirstOrDefaultAsync(x => x.SiteId == site.MicrotingUid);

                                if (assignedSite == null)
                                {
                                    continue;
                                }

                                if (!assignedSite.UseGoogleSheetAsDefault)
                                {
                                    continue;
                                }

                                var planHours = row.Count > j ? row[j].ToString() : string.Empty;
                                var planText = row.Count > j + 1 ? row[j + 1].ToString() : string.Empty;

                                if (string.IsNullOrEmpty(planHours))
                                {
                                    planHours = "0";
                                }

                                // Replace comma with dot if needed
                                if (planHours.Contains(','))
                                {
                                    planHours = planHours.Replace(",", ".");
                                }

                                var parsedPlanHours = double.Parse(planHours, NumberStyles.AllowDecimalPoint,
                                    NumberFormatInfo.InvariantInfo);

                                var preTimePlanning = await dbContext.PlanRegistrations.AsNoTracking()
                                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                                    .Where(x => x.Date < dateValue && x.SdkSitId == (int)site.MicrotingUid!)
                                    .OrderByDescending(x => x.Date)
                                    .FirstOrDefaultAsync();

                                var midnight = new DateTime(dateValue.Year, dateValue.Month, dateValue.Day, 0, 0, 0);

                                var planRegistrations = await dbContext.PlanRegistrations.Where(x =>
                                        x.Date == midnight && x.SdkSitId == site.MicrotingUid)
                                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                                    .ToListAsync();
                                if (planRegistrations.Count > 1)
                                {
                                    Console.WriteLine(
                                        $"Found multiple plan registrations for site: {site.Name} and date: {dateValue}. This should not happen.");
                                    SentrySdk.CaptureMessage(
                                        $"Found multiple plan registrations for site: {site.Name} and date: {dateValue}. This should not happen.");
                                    foreach (var plan in planRegistrations)
                                    {
                                        Console.WriteLine(
                                            $"PlanRegistration ID: {plan.Id}, PlanText: {plan.PlanText}, PlanHours: {plan.PlanHours}, Date: {plan.Date}, workflowState: {plan.WorkflowState}, SdkSitId: {plan.SdkSitId}");
                                        SentrySdk.CaptureMessage(
                                            $"PlanRegistration ID: {plan.Id}, PlanText: {plan.PlanText}, PlanHours: {plan.PlanHours}, Date: {plan.Date}, workflowState: {plan.WorkflowState}, SdkSitId: {plan.SdkSitId}");
                                    }

                                    continue;
                                }

                                var planRegistration = planRegistrations.FirstOrDefault();

                                if (planRegistration == null)
                                {
                                    planRegistration = new PlanRegistration
                                    {
                                        Date = midnight,
                                        PlanText = planText,
                                        PlanHours = parsedPlanHours,
                                        SdkSitId = (int)site.MicrotingUid!,
                                        CreatedByUserId = 1,
                                        UpdatedByUserId = 1,
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

                                    if (preTimePlanning != null)
                                    {
                                        planRegistration.SumFlexStart = preTimePlanning.SumFlexEnd;
                                        planRegistration.SumFlexEnd =
                                            preTimePlanning.SumFlexEnd + planRegistration.Flex -
                                            planRegistration.PaiedOutFlex;
                                        planRegistration.Flex = -planRegistration.PlanHours;
                                    }
                                    else
                                    {
                                        planRegistration.Flex = -planRegistration.PlanHours;
                                        planRegistration.SumFlexEnd = planRegistration.Flex;
                                        planRegistration.SumFlexStart = 0;
                                    }

                                    await planRegistration.Create(dbContext);
                                }
                                else
                                {
                                    // print to console if the current PlanText is different from the one in the database
                                    if (planRegistration.PlanText != planText)
                                    {
                                        Console.WriteLine(
                                            $"PlanText for site: {site.Name} and date: {dateValue} has changed from {planRegistration.PlanText} to {planText}");
                                    }

                                    planRegistration.PlanText = planText;
                                    // print to console if the current PlanHours is different from the one in the database
                                    if (planRegistration.PlanHours != parsedPlanHours)
                                    {
                                        Console.WriteLine(
                                            $"PlanHours for site: {site.Name} and date: {dateValue} has changed from {planRegistration.PlanHours} to {parsedPlanHours}");
                                    }

                                    if (!planRegistration.PlanChangedByAdmin)
                                    {
                                        planRegistration.PlanHours = parsedPlanHours;
                                    }

                                    planRegistration.UpdatedByUserId = 1;

                                    if (preTimePlanning != null)
                                    {
                                        planRegistration.SumFlexStart = preTimePlanning.SumFlexEnd;
                                        planRegistration.SumFlexEnd =
                                            preTimePlanning.SumFlexEnd + planRegistration.PlanHours -
                                            planRegistration.NettoHours -
                                            planRegistration.PaiedOutFlex;
                                        planRegistration.Flex =
                                            planRegistration.NettoHours - planRegistration.PlanHours;
                                    }
                                    else
                                    {
                                        planRegistration.SumFlexEnd =
                                            planRegistration.PlanHours - planRegistration.NettoHours -
                                            planRegistration.PaiedOutFlex;
                                        planRegistration.SumFlexStart = 0;
                                        planRegistration.Flex =
                                            planRegistration.NettoHours - planRegistration.PlanHours;
                                    }

                                    await planRegistration.Update(dbContext);
                                }

                                await PlanRegistrationHelper.UpdatePlanRegistration(planRegistration, dbContext,
                                    assignedSite, DateTime.Now.AddMonths(-1));
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("No data found.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    Console.WriteLine(ex.StackTrace);
                    SentrySdk.CaptureException(ex);
                }
            }
                break;
            case 18:
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

                        var planRegistrationIdsForSite = innerDbContext.PlanRegistrations
                            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                            .Where(x => x.SdkSitId == siteId)
                            .Where(x => x.Date > dayOfPayment)
                            .OrderBy(x => x.Date)
                            .Select(x => x.Id)
                            .ToList();

                        foreach (var planRegistrationId in planRegistrationIdsForSite)
                        {
                            var planRegistration = innerDbContext.PlanRegistrations
                                .AsTracking()
                                .First(x => x.Id == planRegistrationId);
                            if (planRegistration.Date > DateTime.Now.AddMonths(6))
                            {
                                planRegistration.Delete(innerDbContext).GetAwaiter().GetResult();
                                Console.WriteLine(
                                    $@"Deleting planRegistration.Id: {planRegistration.Id} for siteId: {siteId} at planRegistration.Date: {planRegistration.Date} since it is more than 6 months in the future");
                            }
                            else
                            {
                                var originalPlanRegistration = innerDbContext.PlanRegistrations.AsNoTracking()
                                    .First(x => x.Id == planRegistration.Id);

                                var assignedSite = innerDbContext.AssignedSites
                                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                                    .FirstOrDefault(x => x.SiteId == siteId);
                                planRegistration = PlanRegistrationHelper
                                    .UpdatePlanRegistration(planRegistration, innerDbContext, assignedSite,
                                        dayOfPayment)
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
                                        $@"PlanRegistration has changed with id: {planRegistration.Id} for siteId: {siteId} at planRegistration.Date: {planRegistration.Date}, " +
                                        $"SumFlexStart changed from {originalPlanRegistration.SumFlexStart} to {planRegistration.SumFlexStart}" +
                                        $"and SumFlexEnd changed from {originalPlanRegistration.SumFlexEnd} to {planRegistration.SumFlexEnd}");
                                    planRegistration.Update(innerDbContext).GetAwaiter().GetResult();
                                }
                            }
                        }

                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                        Console.WriteLine(ex.StackTrace);
                        SentrySdk.CaptureException(ex);
                    }
                });
                break;
            }
        }
    }
}