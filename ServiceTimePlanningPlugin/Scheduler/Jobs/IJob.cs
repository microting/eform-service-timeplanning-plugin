namespace ServiceTimePlanningPlugin.Scheduler.Jobs;

using System.Threading.Tasks;
public interface IJob
{
    Task Execute();
}