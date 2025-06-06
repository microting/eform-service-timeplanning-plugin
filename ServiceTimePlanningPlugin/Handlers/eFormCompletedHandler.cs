/*
The MIT License (MIT)

Copyright (c) 2007 - 2021 Microting A/S

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


using System.Globalization;
using System.Threading;
using Microting.eForm.Dto;
using Microting.eForm.Infrastructure.Constants;
using Microting.eForm.Infrastructure.Data.Entities;
using Microting.eForm.Infrastructure.Models;
using Sentry;
using ServiceTimePlanningPlugin.Resources;

namespace ServiceTimePlanningPlugin.Handlers;

using System;
using System.Linq;
using System.Threading.Tasks;
using Infrastructure.Helpers;
using Messages;
using Microsoft.EntityFrameworkCore;
using Microting.TimePlanningBase.Infrastructure.Data;
using Microting.TimePlanningBase.Infrastructure.Data.Entities;
using Rebus.Handlers;

public class EFormCompletedHandler : IHandleMessages<eFormCompleted>
{
    private readonly eFormCore.Core _sdkCore;
    private readonly DbContextHelper _dbContextHelper;


    public EFormCompletedHandler(eFormCore.Core sdkCore, DbContextHelper dbContextHelper)
    {
        _dbContextHelper = dbContextHelper;
        _sdkCore = sdkCore;
    }

    public async Task Handle(eFormCompleted message)
    {
        TimePlanningPnDbContext dbContext = _dbContextHelper.GetDbContext();
        Console.WriteLine($"EFormCompletedHandler .Handle called");
        Console.WriteLine($"message.CheckUid: {message.CheckUid}");
        Console.WriteLine($"message.SiteId: {message.SiteId}");
        try
        {
            var eformIdString = await dbContext.PluginConfigurationValues
                .FirstOrDefaultAsync(x => x.Name == "TimePlanningBaseSettings:EformId");

            if (eformIdString == null)
            {
                return;
            }
            var folderId  = int.Parse(dbContext.PluginConfigurationValues
                .First(x => x.Name == "TimePlanningBaseSettings:FolderId")
                .Value);
            var eformId = int.Parse(eformIdString.Value);
            var infoeFormId = int.Parse(dbContext.PluginConfigurationValues
                .First(x => x.Name == "TimePlanningBaseSettings:InfoeFormId")
                .Value);
            var maxHistoryDays = int.Parse(dbContext.PluginConfigurationValues
                .First(x => x.Name == "TimePlanningBaseSettings:MaxHistoryDays").Value);

            await using var sdkDbContext = _sdkCore.DbContextHelper.GetDbContext();
            var site = await sdkDbContext.Sites.FirstOrDefaultAsync(x => x.MicrotingUid == message.SiteId);
            if (site == null)
            {
                Console.WriteLine($"Site with MicrotingUid: {message.SiteId} not found");
                return;
            }
            var cls = await sdkDbContext.Cases
                .Where(x => x.SiteId == site.Id)
                .Where(x => x.MicrotingCheckUid == message.CheckUid)
                .OrderBy(x => x.DoneAt)
                .LastOrDefaultAsync();

            var dateField = await sdkDbContext.Fields.FirstAsync(x => x.OriginalId == "373285" && x.WorkflowState != Constants.WorkflowStates.Removed);
            var shift1StartField = await sdkDbContext.Fields.FirstAsync(x => x.OriginalId == "373286" && x.WorkflowState != Constants.WorkflowStates.Removed);
            var shift1PauseField = await sdkDbContext.Fields.FirstAsync(x => x.OriginalId == "373292" && x.WorkflowState != Constants.WorkflowStates.Removed);
            var shift1StopField = await sdkDbContext.Fields.FirstAsync(x => x.OriginalId == "373287" && x.WorkflowState != Constants.WorkflowStates.Removed);
            var shift2StartField = await sdkDbContext.Fields.FirstAsync(x => x.OriginalId == "373293" && x.WorkflowState != Constants.WorkflowStates.Removed);
            var shift2PauseField = await sdkDbContext.Fields.FirstAsync(x => x.OriginalId == "373294" && x.WorkflowState != Constants.WorkflowStates.Removed);
            var shift2StopField = await sdkDbContext.Fields.FirstAsync(x => x.OriginalId == "373295" && x.WorkflowState != Constants.WorkflowStates.Removed);
            var commentField = await sdkDbContext.Fields.FirstOrDefaultAsync(x => x.OriginalId == "373288" && x.WorkflowState != Constants.WorkflowStates.Removed);

            if (cls != null && cls.CheckListId == eformId)
            {
                var language = await sdkDbContext.Languages.FirstOrDefaultAsync(x => x.Id == site.LanguageId);
                if (language == null)
                {
                    Console.WriteLine($"Language with Id: {site.LanguageId} not found");
                    return;
                }
                var fieldValues = await _sdkCore.Advanced_FieldValueReadList(new() { cls.Id }, language);

                if (string.IsNullOrEmpty(fieldValues.First(x => x.FieldId == dateField.Id).Value))
                {
                    SentrySdk.CaptureMessage($"The date field is empty for site {site.MicrotingUid} and caseId {cls.MicrotingCheckUid}");
                    Console.WriteLine("The date field is empty");
                    return;
                }

                var dateValue = DateTime.Parse(fieldValues.First(x => x.FieldId == dateField.Id).Value);
                if (dateValue < DateTime.UtcNow.AddDays(-maxHistoryDays))
                {
                    Console.WriteLine("The registration is older than maxHistoryDays");
                    return;
                }
                if (dateValue > DateTime.UtcNow)
                {
                    Console.WriteLine("The registration is in the future");
                    return;
                }
                var shift1Start = string.IsNullOrEmpty(fieldValues.First(x => x.FieldId == shift1StartField.Id).Value) ? 0 : int.Parse(fieldValues.First(x => x.FieldId == shift1StartField.Id).Value);
                var shift1Pause = string.IsNullOrEmpty(fieldValues.First(x => x.FieldId == shift1PauseField.Id).Value) ? 0 : int.Parse(fieldValues.First(x => x.FieldId == shift1PauseField.Id).Value);
                var shift1Stop = string.IsNullOrEmpty(fieldValues.First(x => x.FieldId == shift1StopField.Id).Value) ? 0 : int.Parse(fieldValues.First(x => x.FieldId == shift1StopField.Id).Value);
                var shift2Start = string.IsNullOrEmpty(fieldValues.First(x => x.FieldId == shift2StartField.Id).Value) ? 0 : int.Parse(fieldValues.First(x => x.FieldId == shift2StartField.Id).Value);
                var shift2Pause = string.IsNullOrEmpty(fieldValues.First(x => x.FieldId == shift2PauseField.Id).Value) ? 0 : int.Parse(fieldValues.First(x => x.FieldId == shift2PauseField.Id).Value);
                var shift2Stop = string.IsNullOrEmpty(fieldValues.First(x => x.FieldId == shift2StopField.Id).Value) ? 0 : int.Parse(fieldValues.First(x => x.FieldId == shift2StopField.Id).Value);

                var midnight = new DateTime(dateValue.Year, dateValue.Month, dateValue.Day, 0, 0, 0);

                var timePlanning = await dbContext.PlanRegistrations
                    .Where(x => x.SdkSitId == site.MicrotingUid
                                && x.Date == midnight)
                    .FirstOrDefaultAsync();


                if (timePlanning == null)
                {
                    timePlanning = new PlanRegistration
                    {
                        SdkSitId = (int)site.MicrotingUid!,
                        Date = midnight,
                        Pause1Id = shift1Pause,
                        Pause2Id = shift2Pause,
                        Start1Id = shift1Start,
                        Start2Id = shift2Start,
                        Stop1Id = shift1Stop,
                        Stop2Id = shift2Stop,
                        WorkerComment = commentField != null ? fieldValues.First(x => x.FieldId == commentField.Id).Value : "",
                        DataFromDevice = true
                    };

                    await timePlanning.Create(dbContext);
                }
                else
                {
                    if (timePlanning.Pause1Id == 0 || timePlanning.Pause2Id == 0
                                                   || timePlanning.Start1Id == 0 || timePlanning.Start2Id == 0
                                                   || timePlanning.Stop1Id == 0 || timePlanning.Stop2Id == 0)
                    {
                        timePlanning.Pause1Id = timePlanning.Pause1Id == 0 ? shift1Pause : timePlanning.Pause1Id;
                        timePlanning.Pause2Id = timePlanning.Pause2Id == 0 ? shift2Pause : timePlanning.Pause2Id;
                        timePlanning.Start1Id = timePlanning.Start1Id == 0 ? shift1Start : timePlanning.Start1Id;
                        timePlanning.Start2Id = timePlanning.Start2Id == 0 ? shift2Start : timePlanning.Start2Id;
                        timePlanning.Stop1Id = timePlanning.Stop1Id == 0 ? shift1Stop : timePlanning.Stop1Id;
                        timePlanning.Stop2Id = timePlanning.Stop2Id == 0 ? shift2Stop : timePlanning.Stop2Id;
                        if (!string.IsNullOrEmpty(timePlanning.WorkerComment))
                        {
                            timePlanning.WorkerComment += "<br/>";
                        }
                        if (commentField != null)
                        {
                            timePlanning.WorkerComment += fieldValues.First(x => x.FieldId == commentField.Id).Value;
                        }
                        timePlanning.DataFromDevice = true;
                        if (timePlanning.WorkflowState == Constants.WorkflowStates.Removed)
                        {
                            timePlanning.WorkflowState = Constants.WorkflowStates.Created;
                        }

                        await timePlanning.Update(dbContext);
                    }
                }

                var minutesMultiplier = 5;

                double nettoMinutes = timePlanning.Stop1Id - timePlanning.Start1Id;
                nettoMinutes -= timePlanning.Pause1Id > 0 ? timePlanning.Pause1Id - 1 : 0;
                if (timePlanning.Stop2Id != 0)
                {
                    nettoMinutes = nettoMinutes + timePlanning.Stop2Id - timePlanning.Start2Id;
                    nettoMinutes -= timePlanning.Pause2Id > 0 ? timePlanning.Pause2Id - 1 : 0;
                }

                nettoMinutes *= minutesMultiplier;

                double hours = nettoMinutes / 60;
                timePlanning.NettoHours = hours;
                timePlanning.Flex = hours - timePlanning.PlanHours;
                var preTimePlanning =
                    await dbContext.PlanRegistrations.AsNoTracking()
                        .Where(x => x.Date < timePlanning.Date && x.SdkSitId == site.MicrotingUid)
                        .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                        .OrderByDescending(x => x.Date).FirstOrDefaultAsync();
                if (preTimePlanning != null)
                {
                    timePlanning.SumFlexEnd = preTimePlanning.SumFlexEnd + timePlanning.Flex - timePlanning.PaiedOutFlex;
                    timePlanning.SumFlexStart = preTimePlanning.SumFlexEnd;
                }
                else
                {
                    timePlanning.SumFlexEnd = timePlanning.Flex - timePlanning.PaiedOutFlex;
                    timePlanning.SumFlexStart = 0;
                }

                Message theMessage =
                    await dbContext.Messages.FirstOrDefaultAsync(x => x.Id == timePlanning.MessageId);
                string messageText;
                switch (language.LanguageCode)
                {
                    case "da":
                        messageText = theMessage != null ? theMessage.DaName : "";
                        break;
                    case "de":
                        messageText = theMessage != null ? theMessage.DeName : "";
                        break;
                    default:
                        messageText = theMessage != null ? theMessage.EnName : "";
                        break;
                }

                var registrationDevices = await dbContext.RegistrationDevices
                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                    .ToListAsync().ConfigureAwait(false);

                await timePlanning.Update(dbContext);
                if (dbContext.PlanRegistrations.Any(x => x.Date >= timePlanning.Date && x.SdkSitId == site.MicrotingUid && x.Id != timePlanning.Id && x.WorkflowState != Constants.WorkflowStates.Removed))
                {
                    double preSumFlexStart = timePlanning.SumFlexEnd;
                    var list = await dbContext.PlanRegistrations
                        .Where(x => x.Date > timePlanning.Date && x.SdkSitId == site.MicrotingUid && x.Id != timePlanning.Id)
                        .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                        .OrderBy(x => x.Date).ToListAsync();
                    foreach (PlanRegistration planRegistration in list)
                    {
                        Console.WriteLine($"Updating planRegistration {planRegistration.Id} for date {planRegistration.Date}");
                        planRegistration.SumFlexStart = preSumFlexStart;
                        planRegistration.SumFlexEnd = preSumFlexStart + planRegistration.NettoHours -
                                                      planRegistration.PlanHours -
                                                      planRegistration.PaiedOutFlex;
                        planRegistration.Flex = planRegistration.NettoHours - planRegistration.PlanHours;
                        await planRegistration.Update(dbContext);
                        preSumFlexStart = planRegistration.SumFlexEnd;
                    }
                }

            }
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
            Console.WriteLine(ex.Message);
            Console.WriteLine(ex.StackTrace);
        }
    }
}