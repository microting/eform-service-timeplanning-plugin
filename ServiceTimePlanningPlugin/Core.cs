using System.Threading.Tasks;

namespace ServiceTimePlanningPlugin;

using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Castle.MicroKernel.Registration;
using Castle.Windsor;
using Infrastructure.Helpers;
using Installers;
using Messages;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Dto;
using Microting.TimePlanningBase.Infrastructure.Data;
using Microting.TimePlanningBase.Infrastructure.Data.Factories;
using Microting.WindowsService.BasePn;
using Rebus.Bus;

[Export(typeof(ISdkEventHandler))]
public class Core : ISdkEventHandler
{
    private eFormCore.Core _sdkCore;
    private IWindsorContainer _container;
    private IBus _bus;
    private bool _coreThreadRunning = false;
    private bool _coreStatChanging;
    private bool _coreAvailable;
    private string _serviceLocation;
    private static int _maxParallelism = 1;
    private static int _numberOfWorkers = 1;
    private TimePlanningPnDbContext _dbContext;
    private DbContextHelper _dbContextHelper;

    public void CoreEventException(object sender, EventArgs args)
    {
        // Do nothing
    }

    public void UnitActivated(object sender, EventArgs args)
    {
        // Do nothing
    }

    public void eFormProcessed(object sender, EventArgs args)
    {
        // Do nothing
    }

    public void eFormProcessingError(object sender, EventArgs args)
    {
        // Do nothing
    }

    public void eFormRetrived(object sender, EventArgs args)
    {
        // Do nothing
    }

    public void CaseCompleted(object sender, EventArgs args)
    {
        CaseDto trigger = (CaseDto)sender;

        if (trigger.CheckUId != null)
        {
            _bus.SendLocal(new eFormCompleted((int)trigger.CheckUId, trigger.SiteUId));
        }
    }

    public void CaseDeleted(object sender, EventArgs args)
    {
        // Do nothing
    }

    public void NotificationNotFound(object sender, EventArgs args)
    {
        // Do nothing
    }

    public bool Start(string sdkConnectionString, string serviceLocation)
    {
        Console.WriteLine("ServiceTimePlanningPlugin start called");
        try
        {
            var dbNameSection = Regex.Match(sdkConnectionString, @"(Database=\w*;)").Groups[0].Value;
            var dbPrefix = Regex.Match(sdkConnectionString, @"Database=(\d*)_").Groups[1].Value;

            var pluginDbName = $"Database={dbPrefix}_eform-angular-time-planning-plugin;";
            var connectionString = sdkConnectionString.Replace(dbNameSection, pluginDbName);

            if (!_coreAvailable && !_coreStatChanging)
            {
                _serviceLocation = serviceLocation;
                _coreStatChanging = true;

                if (string.IsNullOrEmpty(_serviceLocation))
                    throw new ArgumentException("serviceLocation is not allowed to be null or empty");

                if (string.IsNullOrEmpty(connectionString))
                    throw new ArgumentException("serverConnectionString is not allowed to be null or empty");

                TimePlanningPnContextFactory contextFactory = new TimePlanningPnContextFactory();

                _dbContext = contextFactory.CreateDbContext(new[] { connectionString });
                _dbContext.Database.Migrate();

                _dbContextHelper = new DbContextHelper(connectionString);

                _coreAvailable = true;
                _coreStatChanging = false;

                StartSdkCoreSqlOnly(sdkConnectionString);
                Console.WriteLine($"Connection string: {sdkConnectionString}");

                var rabbitmqHost = _sdkCore.GetSdkSetting(Settings.rabbitMqHost).GetAwaiter().GetResult();
                Console.WriteLine($"rabbitmqHost: {rabbitmqHost}");
                var rabbitMqUser = _sdkCore.GetSdkSetting(Settings.rabbitMqUser).GetAwaiter().GetResult();
                Console.WriteLine($"rabbitMqUser: {rabbitMqUser}");
                var rabbitMqPassword = _sdkCore.GetSdkSetting(Settings.rabbitMqPassword).GetAwaiter().GetResult();
                Console.WriteLine($"rabbitMqPassword: {rabbitMqPassword}");

                string temp = _dbContext.PluginConfigurationValues
                    .SingleOrDefault(x => x.Name == "TimePlanningBaseSettings:MaxParallelism")?.Value;
                _maxParallelism = string.IsNullOrEmpty(temp) ? 1 : int.Parse(temp);

                temp = _dbContext.PluginConfigurationValues
                    .SingleOrDefault(x => x.Name == "TimePlanningBaseSettings:NumberOfWorkers")?.Value;
                _numberOfWorkers = string.IsNullOrEmpty(temp) ? 1 : int.Parse(temp);


                _container = new WindsorContainer();
                _container.Register(Component.For<IWindsorContainer>().Instance(_container));
                _container.Register(Component.For<DbContextHelper>().Instance(_dbContextHelper));
                _container.Register(Component.For<eFormCore.Core>().Instance(_sdkCore));
                _container.Install(
                    new RebusHandlerInstaller()
                    , new RebusInstaller(dbPrefix, connectionString, _maxParallelism, _numberOfWorkers, rabbitMqUser, rabbitMqPassword, rabbitmqHost)
                );

                _bus = _container.Resolve<IBus>();
            }
            Console.WriteLine("ServiceTimePlanningPlugin started");

            CheckRegistrationIntegrity().GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkRed;
            Console.WriteLine("Start failed " + ex.Message);
            throw;
        }
    }

    public bool Stop(bool shutdownReallyFast)
    {
        if (_coreAvailable && !_coreStatChanging)
        {
            _coreStatChanging = true;

            _coreAvailable = false;

            while (_coreThreadRunning)
            {
                Thread.Sleep(100);
                _bus.Dispose();
            }
            _sdkCore.Close();

            _coreStatChanging = false;
        }
        return true;
    }

    public bool Restart(int sameExceptionCount, int sameExceptionCountMax, bool shutdownReallyFast)
    {
        return true;
    }

    private void StartSdkCoreSqlOnly(string sdkConnectionString)
    {
        _sdkCore = new eFormCore.Core();

        _sdkCore.StartSqlOnly(sdkConnectionString).GetAwaiter().GetResult();
    }

    private async Task CheckRegistrationIntegrity()
    {
        var sdkDbContext = _sdkCore.DbContextHelper.GetDbContext();

        var dbContext = _dbContextHelper.GetDbContext();

        var siteIdsForCheck = await dbContext.PluginConfigurationValues
            .FirstOrDefaultAsync(x => x.Name == "TimePlanningBaseSettings:SiteIdsForCheck");

        var maxHistoryDays = await dbContext.PluginConfigurationValues
            .FirstOrDefaultAsync(x => x.Name == "TimePlanningBaseSettings:MaxHistoryDays");

        if (string.IsNullOrEmpty(siteIdsForCheck?.Value))
        {
            return;
        }

        if (string.IsNullOrEmpty(maxHistoryDays?.Value))
        {
            return;
        }

        var siteIds = siteIdsForCheck.Value.Split(",").Select(int.Parse).ToList();

        foreach (var siteId in siteIds)
        {
            var cases = await sdkDbContext.Cases
                .Where(x => x.SiteId == siteId && x.DoneAt > DateTime.Now.AddDays(-int.Parse(maxHistoryDays.Value)))
                .ToListAsync();

            var site = await sdkDbContext.Sites.SingleAsync(x => x.Id == siteId);

            foreach (var @case in cases)
            {
                await _bus.SendLocal(new eFormCompleted((int)@case.MicrotingCheckUid!, (int)site.MicrotingUid!));
            }
        }
    }
}