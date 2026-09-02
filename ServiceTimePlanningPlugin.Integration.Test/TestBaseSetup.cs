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
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microting.TimePlanningBase.Infrastructure.Data;
using NUnit.Framework;
using ServiceTimePlanningPlugin.Infrastructure.Helpers;
using Testcontainers.MariaDb;

namespace ServiceTimePlanningPlugin.Integration.Test;

/// <summary>
/// Real-MariaDB fixture for the service plugin's integration tests, mirroring
/// the pattern used by eform-angular-timeplanning-plugin's
/// TimePlanning.Pn.Test/TestBaseSetup.cs: one Testcontainers MariaDB instance
/// shared across a fixture's tests, with the TimePlanningPnDbContext database
/// dropped and re-migrated fresh per test so state does not leak between
/// tests.
///
/// FlexChainCatchUpJob only touches TimePlanningPnDbContext (AssignedSites,
/// PlanRegistrations) -- no SDK Sites table is involved -- so this fixture
/// does not need the SDK/base contexts the plugin's equivalent fixture also
/// carries.
/// </summary>
public abstract class TestBaseSetup
{
    private readonly MariaDbContainer _mariadbTestcontainer = new MariaDbBuilder()
        .WithImage("mariadb:10.8")
        .WithDatabase("myDb").WithUsername("bla").WithPassword("secretpassword")
        .WithEnvironment("MYSQL_ROOT_PASSWORD", "Qq1234567$")
        .WithCommand("--max_allowed_packet", "32505856")
        .Build();

    protected TimePlanningPnDbContext TimePlanningPnDbContext = null!;
    protected DbContextHelper DbContextHelper = null!;

    private string ConnectionString =>
        _mariadbTestcontainer.GetConnectionString()
            .Replace("myDb", "420_service_timeplanning_test")
            .Replace("bla", "root");

    [SetUp]
    public async Task Setup()
    {
        if (_mariadbTestcontainer.State == TestcontainersStates.Undefined)
        {
            await _mariadbTestcontainer.StartAsync();
        }

        DbContextHelper = new DbContextHelper(ConnectionString);
        TimePlanningPnDbContext = DbContextHelper.GetDbContext();
        TimePlanningPnDbContext.Database.SetCommandTimeout(300);

        // Drop and recreate the database fresh for each test to avoid state
        // pollution -- migrations only, matching the plugin repo's fixture
        // (EnsureCreated()/SQL scripts conflict with migrations).
        TimePlanningPnDbContext.Database.EnsureDeleted();
        TimePlanningPnDbContext.Database.Migrate();
    }

    [TearDown]
    public async Task TearDown()
    {
        await TimePlanningPnDbContext.DisposeAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        Console.WriteLine($"{DateTime.Now} : Stopping MariaDb Container...");
        await _mariadbTestcontainer.StopAsync();
        await _mariadbTestcontainer.DisposeAsync();
        Console.WriteLine($"{DateTime.Now} : Stopped MariaDb Container");
    }
}
