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

using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.TimePlanningBase.Infrastructure.Data;
using Microting.TimePlanningBase.Infrastructure.Data.Entities;
using NUnit.Framework;
using ServiceTimePlanningPlugin.Infrastructure.Interceptors;

namespace ServiceTimePlanningPlugin.Integration.Test;

/// <summary>
/// The interceptor is a copy of the plugin repo's, so a divergence between
/// the two copies would be invisible from the plugin side. These tests go
/// through this repo's own context construction -- DbContextHelper, which
/// TestBaseSetup also uses for TimePlanningPnDbContext -- so they prove the
/// wiring here, and mirror the plugin's DayLockInterceptorTests for the
/// cases a divergence would most likely break (the boundary day itself, soft
/// deletes, the permitted payroll write and the two-slot original/current
/// check).
/// </summary>
[TestFixture]
public class DayLockInterceptorTests : TestBaseSetup
{
    private static async Task<PlanRegistration> SeedPlain(
        TimePlanningPnDbContext db, int site, DateTime date)
    {
        var row = new PlanRegistration
        {
            SdkSitId = site, Date = date,
            PlanText = "", CommentOffice = "", CommentOfficeAll = "",
            WorkflowState = Constants.WorkflowStates.Created,
            CreatedByUserId = 1, UpdatedByUserId = 1,
        };
        await row.Create(db);
        return row;
    }

    private static async Task<PlanRegistration> SeedReconciled(
        TimePlanningPnDbContext db, int site, DateTime date)
    {
        var row = new PlanRegistration
        {
            SdkSitId = site, Date = date,
            Reconciled = true, ReconciledAt = new DateTime(2026, 1, 20, 9, 12, 0),
            PlanText = "", CommentOffice = "", CommentOfficeAll = "",
            WorkflowState = Constants.WorkflowStates.Created,
            CreatedByUserId = 1, UpdatedByUserId = 1,
        };
        await row.Create(db);
        return row;
    }

    [Test]
    public async Task AWriteToALockedDay_IsRejected_ThroughThisReposOwnHelper()
    {
        // A context of its own, straight from the helper every job uses, so
        // this cannot pass on a context wired some other way.
        await using var db = DbContextHelper.GetDbContext();

        // Order matters: create the earlier row FIRST. Seeding the boundary
        // first would put this Create inside the locked range, and the arrange
        // step would throw before the assertion was ever reached.
        var earlier = await SeedPlain(db, 950, new DateTime(2026, 1, 13));
        await SeedReconciled(db, 950, new DateTime(2026, 1, 16));

        earlier.PlanHours = 9;

        Assert.ThrowsAsync<DayLockedException>(async () => await earlier.Update(db),
            "background jobs must be as locked out as the web is -- if this fails, the "
            + "two copies of the interceptor have diverged");
    }

    [Test]
    public async Task Modifying_TheBoundaryDayItself_IsRejected()
    {
        // The boundary day is locked too (<=, not <): an off-by-one drift in
        // either copy would let the jobs rewrite the very day that was reconciled.
        var boundary = await SeedReconciled(TimePlanningPnDbContext, 957, new DateTime(2026, 1, 16));

        boundary.PlanHours = 9;

        Assert.ThrowsAsync<DayLockedException>(async () =>
            await boundary.Update(TimePlanningPnDbContext));
    }

    [Test]
    public async Task SoftDeleting_ALockedRow_IsRejected()
    {
        // PnBase.Delete is a soft delete -- it sets WorkflowState = Removed via
        // UpdateInternal, so this arrives at the interceptor as Modified, not
        // Deleted. SearchListJob's nightly recalculation soft-deletes rows, so
        // this path is live here.
        var earlier = await SeedPlain(TimePlanningPnDbContext, 958, new DateTime(2026, 1, 13));
        await SeedReconciled(TimePlanningPnDbContext, 958, new DateTime(2026, 1, 16));

        Assert.ThrowsAsync<DayLockedException>(async () =>
            await earlier.Delete(TimePlanningPnDbContext));
    }

    [Test]
    public async Task Creating_ARowInsideALockedRange_IsRejected()
    {
        // The sheet pull and the device handler both CREATE rows, so the
        // Added arm matters here as much as the Modified one.
        await SeedReconciled(TimePlanningPnDbContext, 951, new DateTime(2026, 1, 16));

        Assert.ThrowsAsync<DayLockedException>(async () =>
            await SeedPlain(TimePlanningPnDbContext, 951, new DateTime(2026, 1, 14)));
    }

    [Test]
    public async Task SettingTheTransferredToPayrollFlag_OnALockedDay_IsAllowed()
    {
        // Mirrors the plugin's test of the same name (spec §11.2): Reconciled
        // and TransferredToPayroll are independent, so this write must pass
        // here exactly as it does on the web.
        var earlier = await SeedPlain(TimePlanningPnDbContext, 952, new DateTime(2026, 1, 13));
        await SeedReconciled(TimePlanningPnDbContext, 952, new DateTime(2026, 1, 16));

        earlier.TransferredToPayroll = true;
        earlier.TransferredToPayrollAt = DateTime.UtcNow;
        await earlier.Update(TimePlanningPnDbContext);

        var reloaded = await TimePlanningPnDbContext.PlanRegistrations
            .AsNoTracking()
            .FirstAsync(x => x.Id == earlier.Id);
        Assert.That(reloaded.TransferredToPayroll, Is.True,
            "the payroll flag must persist on a locked day");
    }

    [Test]
    public async Task ChangingHours_AlongsideThePayrollFlag_IsRejected()
    {
        // The payroll exemption is not a loophole: as soon as anything else
        // about the row changes in the same save, the lock still applies.
        var earlier = await SeedPlain(TimePlanningPnDbContext, 953, new DateTime(2026, 1, 13));
        await SeedReconciled(TimePlanningPnDbContext, 953, new DateTime(2026, 1, 16));

        earlier.TransferredToPayroll = true;
        earlier.PlanHours = 9;

        Assert.ThrowsAsync<DayLockedException>(async () =>
            await earlier.Update(TimePlanningPnDbContext));
    }

    [Test]
    public async Task MovingALockedRowOutOfTheLockedRange_IsRejected()
    {
        // The new slot (Jan 20) is open; only the ORIGINAL slot is locked, so
        // this passes only if the original (SdkSitId, Date) is checked too.
        var earlier = await SeedPlain(TimePlanningPnDbContext, 954, new DateTime(2026, 1, 13));
        await SeedReconciled(TimePlanningPnDbContext, 954, new DateTime(2026, 1, 16));

        earlier.Date = new DateTime(2026, 1, 20);

        Assert.ThrowsAsync<DayLockedException>(async () =>
            await earlier.Update(TimePlanningPnDbContext));
    }

    [Test]
    public async Task MovingALockedRowToAnotherWorker_IsRejected()
    {
        // Same escape via SdkSitId: worker 956 has no boundary, so only the
        // original slot's site holds the lock.
        var earlier = await SeedPlain(TimePlanningPnDbContext, 955, new DateTime(2026, 1, 13));
        await SeedReconciled(TimePlanningPnDbContext, 955, new DateTime(2026, 1, 16));

        earlier.SdkSitId = 956;

        Assert.ThrowsAsync<DayLockedException>(async () =>
            await earlier.Update(TimePlanningPnDbContext));
    }
}
