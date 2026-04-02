namespace VhcMonitor.Core.Clients;

public interface IVbrClient
{
    List<Dictionary<string, object>> GetRepositoryStates();
    List<Dictionary<string, object>> GetScaleoutRepositories();
    List<Dictionary<string, object>> GetSessions(int lookbackHours = 48);
    List<Dictionary<string, object>> GetJobs();
    List<Dictionary<string, object>> GetBackups();
    List<Dictionary<string, object>> GetRestorePoints(int limit = 500, int offset = 0);
    List<Dictionary<string, object>> RescanRepositories(List<string> repoIds);
    Dictionary<string, object> GetServerInfo();
}
