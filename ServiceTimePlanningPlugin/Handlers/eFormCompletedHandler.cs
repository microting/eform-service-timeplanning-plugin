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
    using Microting.eForm.Infrastructure;
    using Microting.eForm.Infrastructure.Constants;
    using Microting.eForm.Infrastructure.Data.Entities;
    using Microting.eForm.Infrastructure.Models;
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
            await using MicrotingDbContext sdkDbContext = _sdkCore.DbContextHelper.GetDbContext();

            Site site = await sdkDbContext.Sites.SingleAsync(x => x.Id == message.SiteId);
            Language language = await sdkDbContext.Languages.SingleAsync(x => x.Id == site.LanguageId);
            ReplyElement caseModel = await _sdkCore.CaseRead(message.MicrotingId, message.CheckUId, language);
            var fieldValues = await _sdkCore.Advanced_FieldValueReadList(new() { caseModel.Id }, language);

            var dateValue = DateTime.Parse(fieldValues.First().Value);
            var shift1Start = int.Parse(fieldValues[1].Value);
            var shift1Pause = int.Parse(fieldValues[2].Value);
            var shift1Stop = int.Parse(fieldValues[3].Value);
            var shift2Start = int.Parse(fieldValues[4].Value);
            var shift2Pause = int.Parse(fieldValues[5].Value);
            var shift2Stop = int.Parse(fieldValues[6].Value);

            var assignedSiteId = await _dbContext.AssignedSites
                .Where(x => x.SiteId == message.SiteId
                            && x.WorkflowState != Constants.WorkflowStates.Removed)
                .Select(x => x.Id)
                .FirstOrDefaultAsync();

            var timePlanning = await _dbContext.PlanRegistrations
                .Where(x => x.AssignedSiteId == assignedSiteId
                            && x.Date == dateValue)
                .FirstOrDefaultAsync();

            if (timePlanning == null)
            {
                PlanRegistration newPlan = new PlanRegistration
                {
                    AssignedSiteId = assignedSiteId,
                    Date = dateValue,
                    Pause1Id = shift1Pause,
                    Pause2Id = shift2Pause,
                    Start1Id = shift1Start,
                    Start2Id = shift2Start,
                    Stop1Id = shift1Stop,
                    Stop2Id = shift2Stop,
                };

                await newPlan.Create(_dbContext);
            }
            else
            {
                timePlanning.Pause1Id = shift1Pause;
                timePlanning.Pause2Id = shift2Pause;
                timePlanning.Start1Id = shift1Start;
                timePlanning.Start2Id = shift2Start;
                timePlanning.Stop1Id = shift1Stop;
                timePlanning.Stop2Id = shift2Stop;

                await timePlanning.Update(_dbContext);
            }
        }
    }
}