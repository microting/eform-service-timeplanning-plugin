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
            var commentField = await sdkDbContext.Fields.FirstAsync(x => x.OriginalId == "373288" && x.WorkflowState != Constants.WorkflowStates.Removed);

            if (cls != null && cls.CheckListId == eformId)
            {
                var language = await sdkDbContext.Languages.FirstOrDefaultAsync(x => x.Id == site.LanguageId);
                if (language == null)
                {
                    Console.WriteLine($"Language with Id: {site.LanguageId} not found");
                    return;
                }
                var fieldValues = await _sdkCore.Advanced_FieldValueReadList(new() { cls.Id }, language);

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

                var timePlanning = await dbContext.PlanRegistrations
                    .Where(x => x.SdkSitId == site.MicrotingUid
                                && x.Date == dateValue)
                    .FirstOrDefaultAsync();


                if (timePlanning == null)
                {
                    timePlanning = new PlanRegistration
                    {
                        SdkSitId = (int)site.MicrotingUid!,
                        Date = dateValue,
                        Pause1Id = shift1Pause,
                        Pause2Id = shift2Pause,
                        Start1Id = shift1Start,
                        Start2Id = shift2Start,
                        Stop1Id = shift1Stop,
                        Stop2Id = shift2Stop,
                        WorkerComment = fieldValues.First(x => x.FieldId == commentField.Id).Value,
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
                        timePlanning.WorkerComment += fieldValues[7].Value;
                        timePlanning.DataFromDevice = true;

                        await timePlanning.Update(dbContext);
                    }
                }

                var minutesMultiplier = 5;
                double nettoMinutes = 0;

                nettoMinutes = timePlanning.Stop1Id - timePlanning.Start1Id;
                nettoMinutes = nettoMinutes - (timePlanning.Pause1Id > 0 ? timePlanning.Pause1Id - 1 : 0);
                nettoMinutes = nettoMinutes + timePlanning.Stop2Id - timePlanning.Start2Id;
                nettoMinutes = nettoMinutes - (timePlanning.Pause2Id > 0 ? timePlanning.Pause2Id - 1 : 0);

                nettoMinutes = nettoMinutes * minutesMultiplier;

                double hours = nettoMinutes / 60;
                timePlanning.NettoHours = hours;
                timePlanning.Flex = hours - timePlanning.PlanHours;
                var preTimePlanning =
                    await dbContext.PlanRegistrations.AsNoTracking().Where(x => x.Date < timePlanning.Date
                        && x.SdkSitId == site.MicrotingUid).OrderByDescending(x => x.Date).FirstOrDefaultAsync();
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
                timePlanning.StatusCaseId = await DeployResults(timePlanning, maxHistoryDays, infoeFormId, _sdkCore, site, folderId, messageText);
                await timePlanning.Update(dbContext);
                if (dbContext.PlanRegistrations.Any(x => x.Date >= timePlanning.Date && x.SdkSitId == site.MicrotingUid && x.Id != timePlanning.Id))
                {
                    double preSumFlexStart = timePlanning.SumFlexEnd;
                    var list = await dbContext.PlanRegistrations.Where(x => x.Date > timePlanning.Date
                                                                            && x.SdkSitId == site.MicrotingUid && x.Id != timePlanning.Id)
                        .OrderBy(x => x.Date).ToListAsync();
                    foreach (PlanRegistration planRegistration in list)
                    {
                        Console.WriteLine($"Updating planRegistration {planRegistration.Id} for date {planRegistration.Date}");
                        planRegistration.SumFlexStart = preSumFlexStart;
                        planRegistration.SumFlexEnd = planRegistration.Flex + preSumFlexStart - planRegistration.PaiedOutFlex;
                        if (planRegistration.DataFromDevice)
                        {
                            theMessage =
                                await dbContext.Messages.FirstOrDefaultAsync(x => x.Id == planRegistration.MessageId);
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

                            if (planRegistration.Date <= DateTime.UtcNow)
                            {
                                planRegistration.StatusCaseId = await DeployResults(planRegistration, maxHistoryDays, infoeFormId, _sdkCore, site, folderId, messageText);
                            }
                        }
                        await planRegistration.Update(dbContext);
                        preSumFlexStart = planRegistration.SumFlexEnd;
                    }
                }

            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            Console.WriteLine(ex.StackTrace);
        }
    }

    private async Task<int> DeployResults(PlanRegistration planRegistration, int maxHistoryDays, int eFormId, eFormCore.Core core, Site siteInfo, int folderId, string messageText)
    {
        if (planRegistration.StatusCaseId != 0)
        {
            await core.CaseDelete(planRegistration.StatusCaseId);
        }
        await using var sdkDbContext = core.DbContextHelper.GetDbContext();
        var language = await sdkDbContext.Languages.FirstAsync(x => x.Id == siteInfo.LanguageId);
        var folder = await sdkDbContext.Folders.FirstOrDefaultAsync(x => x.Id == folderId);
        var mainElement = await core.ReadeForm(eFormId, language);
        Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language.LanguageCode);
        CultureInfo ci = new CultureInfo(language.LanguageCode);
        mainElement.Label = planRegistration.Date.ToString("dddd dd. MMM yyyy", ci);
        mainElement.EndDate = planRegistration.Date.AddDays(maxHistoryDays);
        mainElement.StartDate = planRegistration.Date.AddDays(-1).ToUniversalTime();
        DateTime startDate = new DateTime(2020, 1, 1);
        mainElement.DisplayOrder = (startDate - planRegistration.Date).Days;
        DataElement element = (DataElement)mainElement.ElementList.First();
        element.Label = mainElement.Label;
        element.DoneButtonEnabled = false;
        CDataValue cDataValue = new CDataValue
        {
            InderValue = $"<strong>{Translations.NettoHours}: {planRegistration.NettoHours:0.00}</strong><br/>" +
                         $"{messageText}"
        };
        element.Description = cDataValue;
        DataItem dataItem = element.DataItemList.First();
        dataItem.Color = Constants.FieldColors.Yellow;
        dataItem.Label = $"<strong>{Translations.Date}: {planRegistration.Date.ToString("dddd dd. MMM yyyy", ci)}</strong>";
        cDataValue = new CDataValue
        {
            InderValue = $"{Translations.PlanText}: {planRegistration.PlanText}<br/>"+
                         $"{Translations.PlanHours}: {planRegistration.PlanHours}<br/><br/>" +
                         $"{Translations.Shift_1__start}: {planRegistration.Options[planRegistration.Start1Id > 0 ? planRegistration.Start1Id - 1 : 0]}<br/>" +
                         $"{Translations.Shift_1__pause}: {planRegistration.Options[planRegistration.Pause1Id > 0 ? planRegistration.Pause1Id - 1 : 0]}<br/>" +
                         $"{Translations.Shift_1__end}: {planRegistration.Options[planRegistration.Stop1Id > 0 ? planRegistration.Stop1Id - 1 : 0]}<br/><br/>" +
                         $"{Translations.Shift_2__start}: {planRegistration.Options[planRegistration.Start2Id > 0 ? planRegistration.Start2Id - 1 : 0]}<br/>" +
                         $"{Translations.Shift_2__pause}: {planRegistration.Options[planRegistration.Pause2Id > 0 ? planRegistration.Pause2Id - 1 : 0]}<br/>" +
                         $"{Translations.Shift_2__end}: {planRegistration.Options[planRegistration.Stop2Id > 0 ? planRegistration.Stop2Id - 1 : 0]}<br/><br/>" +
                         $"<strong>{Translations.NettoHours}: {Math.Round(planRegistration.NettoHours, 2) :0.00}</strong><br/><br/>" +
                         $"{Translations.Flex}: {Math.Round(planRegistration.Flex ,2):0.00}<br/>" +
                         $"{Translations.SumFlexEnd}: {Math.Round(planRegistration.SumFlexEnd, 2):0.00}<br/>" +
                         $"{Translations.PaidOutFlex}: {Math.Round(planRegistration.PaiedOutFlex, 2):0.00}<br/><br/>" +
                         $"<strong>{Translations.Message}:</strong><br/>" +
                         $"{messageText}<br/><br/>" +
                         $"<strong>{Translations.Comments}:</strong><br/>" +
                         $"{planRegistration.WorkerComment?.Replace("\n", "<br/>")}<br/><br/>" +
                         $"<strong>{Translations.Comment_office}:</strong><br/>" +
                         $"{planRegistration.CommentOffice?.Replace("\n", "<br/>")}<br/><br/>"// +
            // $"<strong>{Translations.Comment_office_all}:</strong><br/>" +
            // $"{planRegistration.CommentOffice}<br/>"
        };
        dataItem.Description = cDataValue;

        if (folder != null) mainElement.CheckListFolderName = folder.MicrotingUid.ToString();

        return (int)await core.CaseCreate(mainElement, "", (int)siteInfo.MicrotingUid, folderId);
    }




}