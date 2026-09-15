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

namespace ServiceTimePlanningPlugin.Infrastructure.Helpers;

using System;
using Interceptors;
using Microsoft.EntityFrameworkCore;
using Microting.TimePlanningBase.Infrastructure.Data;

public class DbContextHelper
{
    private string ConnectionString { get;}

    public DbContextHelper(string connectionString)
    {
        ConnectionString = connectionString;
    }

    /// <summary>
    /// Every production context in this service is built here, so this is
    /// where the reconciled-day lock is attached: no background job can write
    /// a day the web refuses to.
    /// </summary>
    public TimePlanningPnDbContext GetDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<TimePlanningPnDbContext>();

        // The same options TimePlanningPnContextFactory.CreateDbContext sets
        // (this used to call it), which offers no way to add an interceptor.
        // The server version stays hardcoded: ServerVersion.AutoDetect OPENS A
        // CONNECTION and runs a version query, and the jobs call this once per
        // site per run.
        optionsBuilder.UseMySql(
            ConnectionString,
            new MariaDbServerVersion(new Version(10, 5, 0)),
            mySqlOptionsAction: builder => { builder.EnableRetryOnFailure(); });

        optionsBuilder.AddInterceptors(ReconciledDayLockInterceptor.Instance);

        return new TimePlanningPnDbContext(optionsBuilder.Options);
    }
}