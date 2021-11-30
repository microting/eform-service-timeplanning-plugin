using System;

namespace ServiceTimePlanningPlugin
{
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

            if (trigger.MicrotingUId != null && trigger.CheckUId != null)
            {
                int caseId = (int)trigger.MicrotingUId;
                int checkListId = trigger.CheckListId;
                int checkUId = (int)trigger.CheckUId;
                int siteId = trigger.SiteUId;
                _bus.SendLocal(new eFormCompleted(caseId, checkListId, checkUId, siteId));
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
                string rabbitmqHost = connectionString.Contains("frontend") ? $"frontend-{dbPrefix}-rabbitmq" : "localhost";

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
                        , new RebusInstaller(connectionString, _maxParallelism, _numberOfWorkers, "admin", "password", rabbitmqHost)
                    );

                    _bus = _container.Resolve<IBus>();
                }
                Console.WriteLine("ServiceTimePlanningPlugin started");
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
            try
            {
                if (_coreAvailable && !_coreStatChanging)
                {
                    _coreStatChanging = true;

                    _coreAvailable = false;

                    var tries = 0;
                    while (_coreThreadRunning)
                    {
                        Thread.Sleep(100);
                        _bus.Dispose();
                        tries++;
                    }
                    _sdkCore.Close();

                    _coreStatChanging = false;
                }
            }
            catch (ThreadAbortException)
            {
                //"Even if you handle it, it will be automatically re-thrown by the CLR at the end of the try/catch/finally."
                Thread.ResetAbort(); //This ends the re-throwning
            }

            return true;
        }

        public bool Restart(int sameExceptionCount, int sameExceptionCountMax, bool shutdownReallyFast)
        {
            return true;
        }

        public void StartSdkCoreSqlOnly(string sdkConnectionString)
        {
            _sdkCore = new eFormCore.Core();

            _sdkCore.StartSqlOnly(sdkConnectionString);
        }
    }
}
