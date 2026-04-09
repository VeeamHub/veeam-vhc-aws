namespace VeeamVhcAws.Core.Clients;

public interface IVbrClient
{
    List<Dictionary<string, object>> GetRepositoryStates();
    List<Dictionary<string, object>> GetScaleoutRepositories();
    List<Dictionary<string, object>> GetSessions(int lookbackHours = 24);
    List<Dictionary<string, object>> GetJobs();
    List<Dictionary<string, object>> GetBackups();
    List<Dictionary<string, object>> GetRestorePoints(int limit = 500, int offset = 0);
    List<Dictionary<string, object>> RescanRepositories(List<string> repoIds);
    Dictionary<string, object> GetServerInfo();
    List<Dictionary<string, object>> GetManagedServers(string? typeFilter = null);
    List<Dictionary<string, object>> GetInventory(string hostname,
        string hierarchyType = "VmsAndTemplates", int limit = 200);
    Dictionary<string, object> GetConnectionCertificate(string serverName,
        string credentialsId, string type = "ViHost", int port = 443);
}
