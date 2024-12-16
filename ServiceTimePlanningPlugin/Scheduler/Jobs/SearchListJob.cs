using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.TimePlanningBase.Infrastructure.Data;
using Microting.TimePlanningBase.Infrastructure.Data.Entities;
using Sentry;
using ServiceTimePlanningPlugin.Infrastructure.Helpers;

namespace ServiceTimePlanningPlugin.Scheduler.Jobs;

public class SearchListJob(DbContextHelper dbContextHelper, eFormCore.Core _sdkCore) : IJob
{
    public async Task Execute()
    {
        if (DateTime.UtcNow.Hour == 11)
        {
            var dbContext = dbContextHelper.GetDbContext();
            var sdkContext = _sdkCore.DbContextHelper.GetDbContext();
            var privateKeyId = Environment.GetEnvironmentVariable("PRIVATE_KEY_ID");
            if (string.IsNullOrEmpty(privateKeyId))
            {
                return;
            }

            var googleSheetId = await dbContext.PluginConfigurationValues
                .SingleAsync(x => x.Name == "TimePlanningBaseSettings:GoogleSheetId");
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
            var sheetMetadataRequest = service.Spreadsheets.Get(googleSheetId.Value);
            var sheetMetadata = await sheetMetadataRequest.ExecuteAsync();
            var sheet = sheetMetadata.Sheets.First();
            var rowCount = sheet.Properties.GridProperties.RowCount;
            var columnCount = sheet.Properties.GridProperties.ColumnCount;

            // Define request parameters with the determined range
            String range = $"Sheet1!A1:{GetColumnName((int)columnCount!)}{rowCount}";
            SpreadsheetsResource.ValuesResource.GetRequest request =
                service.Spreadsheets.Values.Get(googleSheetId.Value, range);

            // Fetch the data from the sheet
            ValueRange response = await request.ExecuteAsync();
            IList<IList<Object>> values = response.Values;

            if (values != null && values.Count > 0)
            {
                // Skip the header row (first row)
                for (int i = 1; i < values.Count; i++)
                {
                    var row = values[i];
                    // Process each row
                    string date = row[0].ToString();

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

                    // Iterate over each pair of columns starting from the fourth column
                    for (int j = 3; j < row.Count; j += 2)
                    {
                        string siteName = row[j].ToString().Split('-')[0].Trim();
                        var site = await sdkContext.Sites.FirstOrDefaultAsync(x =>
                            x.Name.Replace(" ", "").ToLower() == siteName.Replace(" ", "").ToLower());
                        if (site == null)
                        {
                            continue;
                        }

                        var planHours = row[j].ToString();
                        var planText = row[j + 1].ToString();

                        if (string.IsNullOrEmpty(planHours))
                        {
                            planHours = "0";
                        }

                        // Replace comma with dot if needed
                        if (planHours.Contains(','))
                        {
                            planHours = planHours.Replace(",", ".");
                        }

                        double parsedPlanHours = double.Parse(planHours, NumberStyles.AllowDecimalPoint,
                            NumberFormatInfo.InvariantInfo);

                        var preTimePlanning = await dbContext.PlanRegistrations.AsNoTracking()
                            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                            .Where(x => x.Date < dateValue && x.SdkSitId == (int)site.MicrotingUid!)
                            .OrderByDescending(x => x.Date)
                            .FirstOrDefaultAsync();

                        var planRegistration = await dbContext.PlanRegistrations.SingleOrDefaultAsync(x =>
                            x.Date == dateValue && x.SdkSitId == site.MicrotingUid);

                        if (planRegistration == null)
                        {
                            planRegistration = new PlanRegistration
                            {
                                Date = dateValue,
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
                                planRegistration.SumFlexEnd = preTimePlanning.SumFlexEnd + planRegistration.Flex -
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
                            planRegistration.PlanHours = parsedPlanHours;
                            planRegistration.UpdatedByUserId = 1;

                            if (preTimePlanning != null)
                            {
                                planRegistration.SumFlexStart = preTimePlanning.SumFlexEnd;
                                planRegistration.SumFlexEnd =
                                    preTimePlanning.SumFlexEnd + planRegistration.PlanHours -
                                    planRegistration.NettoHours -
                                    planRegistration.PaiedOutFlex;
                                planRegistration.Flex = planRegistration.NettoHours - planRegistration.PlanHours;
                            }
                            else
                            {
                                planRegistration.SumFlexEnd =
                                    planRegistration.PlanHours - planRegistration.NettoHours -
                                    planRegistration.PaiedOutFlex;
                                planRegistration.SumFlexStart = 0;
                                planRegistration.Flex = planRegistration.NettoHours - planRegistration.PlanHours;
                            }

                            await planRegistration.Update(dbContext);
                        }
                    }
                }
            }
            else
            {
                Console.WriteLine("No data found.");
            }
        }

        if (DateTime.UtcNow.Hour == 18)
        {
            var dbContext = dbContextHelper.GetDbContext();
            var siteIds = await dbContext.AssignedSites
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Select(x => x.SiteId)
                .ToListAsync();

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
                    Console.WriteLine(
                        $@"Checking planRegistration.Id: {planRegistration.Id} for siteId: {siteId} at planRegistration.Date: {planRegistration.Date}");
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
                        planRegistration.SumFlexStart = preSumFlexStart;
                        planRegistration.SumFlexEnd = preSumFlexStart + planRegistration.NettoHours -
                                                      planRegistration.PlanHours -
                                                      planRegistration.PaiedOutFlex;
                        planRegistration.Flex = planRegistration.NettoHours - planRegistration.PlanHours;

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
                        else
                        {
                            Console.WriteLine(
                                $@"PlanRegistration has not changed with id: {planRegistration.Id} for siteId: {siteId} at planRegistration.Date: {planRegistration.Date}");
                        }

                        preSumFlexStart = planRegistration.SumFlexEnd;
                    }
                }
            });
        }


    }


// Helper method to convert column index to column name
    private string GetColumnName(int index)
    {
        const string letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        string columnName = string.Empty;
        while (index > 0)
        {
            index--;
            columnName = letters[index % 26] + columnName;
            index /= 26;
        }
        return columnName;
    }
}