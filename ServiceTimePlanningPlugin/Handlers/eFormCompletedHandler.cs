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


namespace ServiceTimePlanningPlugin.Handlers
{
    using System;
    using System.Linq;
    using System.Threading.Tasks;
    using Infrastructure.Helpers;
    using Messages;
    using Microsoft.EntityFrameworkCore;
    using Microting.eForm.Infrastructure.Constants;
    using Microting.TimePlanningBase.Infrastructure.Data;
    using Microting.TimePlanningBase.Infrastructure.Data.Entities;
    using Rebus.Handlers;

    public class EFormCompletedHandler : IHandleMessages<eFormCompleted>
    {
        private readonly eFormCore.Core _sdkCore;
        private readonly TimePlanningPnDbContext _dbContext;

        public EFormCompletedHandler(eFormCore.Core sdkCore, DbContextHelper dbContextHelper)
        {
            _dbContext = dbContextHelper.GetDbContext();
            _sdkCore = sdkCore;
        }

        public async Task Handle(eFormCompleted message)
        {
            Console.WriteLine($"EFormCompletedHandler .Handle called");
            Console.WriteLine($"message.CheckId: {message.CheckId}");
            Console.WriteLine($"message.MicrotingId: {message.MicrotingId}");
            Console.WriteLine($"message.SiteId: {message.SiteId}");
            Console.WriteLine($"message.CheckUId: {message.CheckUId}");
            try
            {
                var eformIdString = _dbContext.PluginConfigurationValues
                    .First(x => x.Name == "TimePlanningBaseSettings:EformId")
                    .Value;
                var eformId = int.Parse(eformIdString);

                await using var sdkDbContext = _sdkCore.DbContextHelper.GetDbContext();
                var site = await sdkDbContext.Sites.SingleOrDefaultAsync(x => x.MicrotingUid == message.SiteId);
                if (site == null)
                {
                    Console.WriteLine($"Site with MicrotingUid: {message.SiteId} not found");
                    return;
                }
                var cls = await sdkDbContext.Cases
                    .Where(x => x.SiteId == site.Id)
                    .Where(x => x.MicrotingUid == message.MicrotingId)
                    .OrderBy(x => x.DoneAt)
                    .LastOrDefaultAsync();
                if (cls != null && cls.CheckListId == eformId)
                {
                    var language = await sdkDbContext.Languages.SingleOrDefaultAsync(x => x.Id == site.LanguageId);
                    if (language == null)
                    {
                        Console.WriteLine($"Language with Id: {site.LanguageId} not found");
                        return;
                    }
                    var fieldValues = await _sdkCore.Advanced_FieldValueReadList(new() { cls.Id }, language);

                    var dateValue = DateTime.Parse(fieldValues.First().Value);
                    var shift1Start = string.IsNullOrEmpty(fieldValues[1].Value) ? 0 : int.Parse(fieldValues[1].Value);
                    var shift1Pause = string.IsNullOrEmpty(fieldValues[2].Value) ? 0 : int.Parse(fieldValues[2].Value);
                    var shift1Stop = string.IsNullOrEmpty(fieldValues[3].Value) ? 0 : int.Parse(fieldValues[3].Value);
                    var shift2Start = string.IsNullOrEmpty(fieldValues[4].Value) ? 0 : int.Parse(fieldValues[4].Value);
                    var shift2Pause = string.IsNullOrEmpty(fieldValues[5].Value) ? 0 : int.Parse(fieldValues[5].Value);
                    var shift2Stop = string.IsNullOrEmpty(fieldValues[6].Value) ? 0 : int.Parse(fieldValues[6].Value);

                    var assignedSiteId = await _dbContext.AssignedSites
                        .Where(x => x.SiteId == message.SiteId
                                    && x.WorkflowState != Constants.WorkflowStates.Removed)
                        .Select(x => x.Id)
                        .FirstOrDefaultAsync();
                    if (assignedSiteId == 0)
                    {
                        Console.WriteLine($"AssignedSite with SiteId: {message.SiteId} not found");
                        return;
                    }

                    var timePlanning = await _dbContext.PlanRegistrations
                        .Where(x => x.AssignedSiteId == assignedSiteId
                                    && x.Date == dateValue)
                        .FirstOrDefaultAsync();


                    if (timePlanning == null)
                    {
                        timePlanning = new PlanRegistration
                        {
                            AssignedSiteId = assignedSiteId,
                            Date = dateValue,
                            Pause1Id = shift1Pause,
                            Pause2Id = shift2Pause,
                            Start1Id = shift1Start,
                            Start2Id = shift2Start,
                            Stop1Id = shift1Stop,
                            Stop2Id = shift2Stop
                        };

                        await timePlanning.Create(_dbContext);
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

                            await timePlanning.Update(_dbContext);
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
                        await _dbContext.PlanRegistrations.SingleOrDefaultAsync(x => x.Date == timePlanning.Date.AddDays(-1)
                            && x.AssignedSiteId == assignedSiteId);
                    if (preTimePlanning != null)
                    {
                        timePlanning.SumFlex = preTimePlanning.SumFlex + timePlanning.Flex;
                    }
                    else
                    {
                        timePlanning.SumFlex = timePlanning.Flex;
                    }
                    await timePlanning.Update(_dbContext);
                    if (_dbContext.PlanRegistrations.Any(x => x.Date >timePlanning.Date && x.AssignedSiteId == assignedSiteId))
                    {
                        double preSumFlex = timePlanning.SumFlex;
                        var list = await _dbContext.PlanRegistrations.Where(x => x.Date > timePlanning.Date && x.AssignedSiteId == assignedSiteId).ToListAsync();
                        foreach (PlanRegistration planRegistration in list)
                        {
                            Console.WriteLine($"Updating planRegistration {planRegistration.Id} for date {planRegistration.Date}");
                            planRegistration.SumFlex = planRegistration.Flex + preSumFlex;
                            await planRegistration.Update(_dbContext);
                            preSumFlex = planRegistration.SumFlex;
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
    }
}